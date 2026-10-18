using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using Wildmere.Core;

namespace Wildmere.World
{
    /// <summary>
    /// Owns all loaded columns: streaming around a focus point, background generation and meshing,
    /// block queries/edits, and the link to the save. One instance per loaded world.
    /// </summary>
    public sealed class VoxelWorld : MonoBehaviour
    {
        public static VoxelWorld Instance { get; private set; }

        public IWorldGenerator Gen { get; private set; }
        public WorldSave Save { get; private set; }
        public int ViewDistance = 10;       // chunks (render radius)
        public float MainBudgetMs = 4f;     // per-frame budget for applying worker results
        public Vector3 Focus;               // usually the player position
        public Vector3 FocusDir = Vector3.forward;

        /// <summary>(x, y, z, oldBlock, newBlock) after any edit.</summary>
        public event Action<int, int, int, byte, byte> BlockChanged;

        readonly Dictionary<long, Chunk> mChunks = new Dictionary<long, Chunk>();
        WorkerPool mPool;
        ChunkMesher[] mMeshers;
        readonly ConcurrentBag<MeshBuild> mBuilds = new ConcurrentBag<MeshBuild>();
        readonly List<Vector2Int> mRing = new List<Vector2Int>();
        int mRingRadius = -1;
        int mGenInFlight, mMeshInFlight;
        float mNextUnload;
        bool mFancyLeaves = true;

        public int Loaded => mChunks.Count;
        public int MeshedCount { get; private set; }
        public int GenInFlight => mGenInFlight;
        public int MeshInFlight => mMeshInFlight;
        public int Generated { get; private set; }
        public long TotalVertices { get; private set; }
        public WorkerPool Pool => mPool;

        public bool FancyLeaves
        {
            get => mFancyLeaves;
            set
            {
                if (mFancyLeaves == value) return;
                mFancyLeaves = value;
                if (mMeshers != null) foreach (var m in mMeshers) m.FancyLeaves = value;
                foreach (var c in mChunks.Values) c.Version++;
            }
        }

        public static VoxelWorld Create(IWorldGenerator gen, WorldSave save, int threads)
        {
            var go = new GameObject("VoxelWorld");
            var w = go.AddComponent<VoxelWorld>();
            w.Gen = gen;
            w.Save = save;
            w.mPool = new WorkerPool(threads);
            w.mMeshers = new ChunkMesher[threads];
            for (int i = 0; i < threads; i++) w.mMeshers[i] = new ChunkMesher { FancyLeaves = w.mFancyLeaves };
            Instance = w;
            VoxelMaterials.Init();
            Debug.Log($"WM_WORLD create seed={gen.Seed} threads={threads}");
            return w;
        }

        void OnDestroy()
        {
            mPool?.Dispose();
            foreach (var c in mChunks.Values) ChunkView.Return(c.View);
            mChunks.Clear();
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ queries

        public Chunk GetChunk(int cx, int cz) => mChunks.TryGetValue(Chunk.Key(cx, cz), out var c) ? c : null;

        public Chunk ChunkAt(int x, int z) => GetChunk(x >> 4, z >> 4);

        /// <summary>Block at world coords. Unloaded columns read as Air above sea level and Stone below (keeps physics sane).</summary>
        public byte GetBlock(int x, int y, int z)
        {
            if (y < 0) return B.Bedrock;
            if (y >= Chunk.SY) return B.Air;
            var c = ChunkAt(x, z);
            if (c == null || c.State != ChunkState.Generated) return y <= 62 ? B.Stone : B.Air;
            return c.Blocks[(x & 15) + ((z & 15) << 4) + (y << 8)];
        }

        public bool IsLoaded(int x, int z)
        {
            var c = ChunkAt(x, z);
            return c != null && c.State == ChunkState.Generated;
        }

        public bool IsMeshed(int x, int z)
        {
            var c = ChunkAt(x, z);
            return c != null && c.MeshedVersion >= 0;
        }

        public byte GetMeta(int x, int y, int z)
        {
            var c = ChunkAt(x, z);
            if (c == null) return 0;
            lock (c.Meta) return c.Meta.TryGetValue(Chunk.Index(x & 15, y, z & 15), out var m) ? m : (byte)0;
        }

        /// <summary>Highest non-air, non-plant block in a column (or -1 if the column isn't loaded).</summary>
        public int SurfaceY(int x, int z)
        {
            var c = ChunkAt(x, z);
            if (c == null || c.State != ChunkState.Generated) return -1;
            for (int y = Math.Min(Chunk.SY - 1, c.MaxY + 1); y >= 0; y--)
            {
                byte b = c.Blocks[(x & 15) + ((z & 15) << 4) + (y << 8)];
                if (b != B.Air && Blocks.ShapeOf[b] != RenderShape.Cross) return y;
            }
            return -1;
        }

        /// <summary>True when every column within <paramref name="radius"/> chunks of p is generated and meshed.</summary>
        public bool ReadyAround(Vector3 p, int radius)
        {
            int cx = Mathf.FloorToInt(p.x) >> 4, cz = Mathf.FloorToInt(p.z) >> 4;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var c = GetChunk(cx + dx, cz + dz);
                    if (c == null || c.State != ChunkState.Generated || c.MeshedVersion < 0) return false;
                }
            return true;
        }

        public float LoadProgress(Vector3 p, int radius)
        {
            int cx = Mathf.FloorToInt(p.x) >> 4, cz = Mathf.FloorToInt(p.z) >> 4, ok = 0, n = 0;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++, n++)
                {
                    var c = GetChunk(cx + dx, cz + dz);
                    if (c != null && c.State == ChunkState.Generated) ok++;
                    if (c != null && c.MeshedVersion >= 0) ok++;
                }
            return ok / (2f * n);
        }

        // ------------------------------------------------------------------ edits

        /// <summary>
        /// Set a block (main thread). Records the change in the save, remeshes affected columns
        /// (the edited one first) and pops unsupported plants above. Returns false if unchanged/unloaded.
        /// </summary>
        public bool SetBlock(int x, int y, int z, byte id, byte meta = 0)
        {
            if (y < 0 || y >= Chunk.SY) return false;
            var c = ChunkAt(x, z);
            if (c == null || c.State != ChunkState.Generated) return false;
            int lx = x & 15, lz = z & 15, idx = Chunk.Index(lx, y, lz);
            byte old = c.Blocks[idx];
            byte oldMeta;
            lock (c.Meta) c.Meta.TryGetValue(idx, out oldMeta);
            if (old == id && oldMeta == meta) return false;

            c.Blocks[idx] = id;
            lock (c.Meta) { if (meta != 0) c.Meta[idx] = meta; else c.Meta.Remove(idx); }
            c.RecomputeColumn(lx, lz);
            if (id != B.Air && y > c.MaxY) c.MaxY = y;
            c.Dirty = true;
            Save?.Record(c, idx, id, meta);

            bool lightChange = Blocks.IsOpaque[old] != Blocks.IsOpaque[id] || Blocks.Emit[old] != Blocks.Emit[id] ||
                               Blocks.Absorb[old] != Blocks.Absorb[id];
            Remesh(c, -1000f);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    bool need = lightChange;
                    if (!need)
                    {
                        // Without a light change only faces touching the edited block (AO/culling) are affected.
                        bool xOk = dx == 0 || (dx == -1 && lx == 0) || (dx == 1 && lx == 15);
                        bool zOk = dz == 0 || (dz == -1 && lz == 0) || (dz == 1 && lz == 15);
                        need = xOk && zOk;
                    }
                    if (!need) continue;
                    var n = GetChunk(c.CX + dx, c.CZ + dz);
                    if (n != null) Remesh(n, -500f);
                }

            Decor.PropRenderer.Instance?.OnBlockChanged(x, y, z);
            BlockChanged?.Invoke(x, y, z, old, id);

            // Plants and torches need a block underneath.
            if (y + 1 < Chunk.SY && !Blocks.IsSolid[id])
            {
                byte above = c.Blocks[idx + 256];
                if (Blocks.Defs[above] != null && Blocks.Defs[above].NeedsSupport) SetBlock(x, y + 1, z, B.Air);
            }
            return true;
        }

        void Remesh(Chunk c, float priority)
        {
            c.Version++;
            if (c.MeshQueued || c.State != ChunkState.Generated) return;
            TryQueueMesh(c, priority);
        }

        // ------------------------------------------------------------------ streaming

        void BuildRing(int radius)
        {
            mRing.Clear();
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dz * dz <= radius * radius + radius) mRing.Add(new Vector2Int(dx, dz));
            mRing.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));
            mRingRadius = radius;
        }

        void Update()
        {
            if (mPool == null) return;
            int genRadius = ViewDistance + 1;
            if (mRingRadius != genRadius) BuildRing(genRadius);

            int fcx = Mathf.FloorToInt(Focus.x) >> 4, fcz = Mathf.FloorToInt(Focus.z) >> 4;
            int maxGen = mPool.ThreadCount * 3, maxMesh = mPool.ThreadCount * 3;

            // Walk outward: queue generation for missing columns, meshing for columns whose neighbours exist.
            foreach (var o in mRing)
            {
                int cx = fcx + o.x, cz = fcz + o.y;
                long key = Chunk.Key(cx, cz);
                if (!mChunks.TryGetValue(key, out var c))
                {
                    if (mGenInFlight >= maxGen) continue;
                    c = new Chunk(cx, cz) { State = ChunkState.Queued };
                    mChunks[key] = c;
                    QueueGenerate(c, Score(cx, cz));
                    continue;
                }
                if (c.State != ChunkState.Generated) continue;
                bool inView = o.x * o.x + o.y * o.y <= ViewDistance * ViewDistance + ViewDistance;
                if (inView && !c.MeshQueued && c.MeshedVersion != c.Version && mMeshInFlight < maxMesh)
                    TryQueueMesh(c, Score(cx, cz));
            }

            mPool.Drain(MainBudgetMs);
            VoxelMaterials.FlushTints();

            if (Time.unscaledTime >= mNextUnload)
            {
                mNextUnload = Time.unscaledTime + 0.5f;
                Unload(fcx, fcz, ViewDistance + 3);
            }
        }

        float Score(int cx, int cz)
        {
            float dx = cx * 16 + 8 - Focus.x, dz = cz * 16 + 8 - Focus.z;
            float d2 = dx * dx + dz * dz;
            var dir = new Vector2(FocusDir.x, FocusDir.z);
            float len = Mathf.Sqrt(d2);
            if (len > 24f && dir.sqrMagnitude > 0.01f)
            {
                float dot = (dx * dir.x + dz * dir.y) / (len * dir.magnitude);
                d2 *= 1.6f - 0.6f * dot; // prefer what the player is looking at
            }
            return d2;
        }

        void QueueGenerate(Chunk c, float priority)
        {
            mGenInFlight++;
            c.State = ChunkState.Generating;
            var gen = Gen;
            mPool.Enqueue(new WorkerPool.Job
            {
                Priority = priority, Tag = $"gen {c.CX},{c.CZ}",
                Run = _ =>
                {
                    gen.Generate(c);
                    mPool.Post(() => OnGenerated(c));
                },
            });
        }

        void OnGenerated(Chunk c)
        {
            mGenInFlight--;
            if (!mChunks.TryGetValue(Chunk.Key(c.CX, c.CZ), out var cur) || cur != c) return; // unloaded meanwhile
            if (Save != null && Save.ApplyTo(c)) c.RecomputeHeights();
            VoxelMaterials.WriteTints(c);
            c.State = ChunkState.Generated;
            Generated++;
            Decor.PropRenderer.Instance?.OnChunkLoaded(c);
        }

        bool TryQueueMesh(Chunk c, float priority)
        {
            var n = new Chunk[9];
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var nc = GetChunk(c.CX + dx, c.CZ + dz);
                    if (nc == null || nc.State != ChunkState.Generated) return false;
                    n[dx + 1 + (dz + 1) * 3] = nc;
                }
            c.MeshQueued = true;
            mMeshInFlight++;
            int version = c.Version;
            mPool.Enqueue(new WorkerPool.Job
            {
                Priority = priority, Tag = $"mesh {c.CX},{c.CZ}",
                Run = worker =>
                {
                    if (!mBuilds.TryTake(out var build)) build = new MeshBuild();
                    mMeshers[worker].Build(n, build);
                    mPool.Post(() => OnMeshed(c, build, version));
                },
            });
            return true;
        }

        void OnMeshed(Chunk c, MeshBuild build, int version)
        {
            mMeshInFlight--;
            c.MeshQueued = false;
            bool alive = mChunks.TryGetValue(Chunk.Key(c.CX, c.CZ), out var cur) && cur == c;
            if (alive && version > c.MeshedVersion)
            {
                if (c.View == null) { c.View = ChunkView.Rent(c); MeshedCount++; }
                TotalVertices -= c.View.Vertices;
                c.View.Apply(build);
                TotalVertices += c.View.Vertices;
                c.MeshedVersion = version;
            }
            mBuilds.Add(build);
            // An edit landed while we were meshing: go again right away.
            if (alive && c.MeshedVersion != c.Version) TryQueueMesh(c, -800f);
        }

        readonly List<long> mDrop = new List<long>();

        void Unload(int fcx, int fcz, int radius)
        {
            mDrop.Clear();
            foreach (var kv in mChunks)
            {
                var c = kv.Value;
                int dx = c.CX - fcx, dz = c.CZ - fcz;
                if (dx * dx + dz * dz > radius * radius && c.State == ChunkState.Generated && !c.MeshQueued) mDrop.Add(kv.Key);
            }
            foreach (var k in mDrop)
            {
                var c = mChunks[k];
                if (c.View != null) { TotalVertices -= c.View.Vertices; ChunkView.Return(c.View); c.View = null; MeshedCount--; }
                Decor.PropRenderer.Instance?.OnChunkUnloaded(c);
                mChunks.Remove(k);
            }
        }

        /// <summary>Drop every column and its meshes (e.g. after changing a setting that affects meshing).</summary>
        public void RemeshAll()
        {
            foreach (var c in mChunks.Values) if (c.State == ChunkState.Generated) c.Version++;
        }

        public IEnumerable<Chunk> Chunks => mChunks.Values;
    }
}
