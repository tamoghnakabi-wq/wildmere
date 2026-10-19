using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Wildmere.World.Gen;

namespace Wildmere.World.Decor
{
    /// <summary>
    /// Draws generator-placed decoration props (Blender models in Resources/Models/Props): per frame,
    /// visible columns contribute instance matrices per (model, LOD), drawn SRP-batched; LOD1 is
    /// used beyond <see cref="LodDistance"/>. Large props also become collision boxes for voxel physics.
    /// A prop disappears when its anchor cell is built into or its supporting block is removed.
    /// </summary>
    public sealed class PropRenderer : MonoBehaviour
    {
        public static PropRenderer Instance { get; private set; }
        public float LodDistance = 42f;
        public float DrawDistance = 150f;

        sealed class Part { public Mesh Mesh; public Matrix4x4 Local; public Material[] Mats; }
        sealed class Model
        {
            public string Name;
            public List<Part> Lod0 = new List<Part>(), Lod1 = new List<Part>();
            public Bounds Bounds;
            public bool Solid;
            public float SolidShrink = 0.8f;
        }

        struct PropInst { public Matrix4x4 M; public Bounds WorldBox; public int Kind; public int X, Y, Z; }

        readonly Dictionary<Chunk, List<PropInst>> mChunks = new Dictionary<Chunk, List<PropInst>>();
        Model[] mModels;
        readonly Dictionary<(int kind, int lod), List<Matrix4x4>> mBatches = new Dictionary<(int, int), List<Matrix4x4>>();
        readonly Matrix4x4[] mScratch = new Matrix4x4[1023];
        readonly Plane[] mPlanes = new Plane[6];

        static readonly HashSet<string> kSolid = new HashSet<string>
        {
            "Boulder_A", "Boulder_B", "Boulder_C", "SandstonePillar", "FallenLog_Oak", "FallenLog_Birch", "Stump",
            "GiantMushroom", "IceSpike", "DeadTree",
        };

        static readonly bool sInstancing = Core.Cli.Has("propinst");

        void Awake()
        {
            Instance = this;
            VoxelPhysics.ExtraSolid = OverlapsProps;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (VoxelPhysics.ExtraSolid == OverlapsProps) VoxelPhysics.ExtraSolid = null;
        }

        void EnsureModels()
        {
            if (mModels != null) return;
            mModels = new Model[PropCatalog.Names.Length];
            for (int k = 0; k < mModels.Length; k++)
            {
                string name = PropCatalog.Names[k];
                var go = Resources.Load<GameObject>("Models/Props/" + name);
                var m = new Model { Name = name, Solid = kSolid.Contains(name) };
                if (name == "DeadTree" || name == "GiantMushroom") m.SolidShrink = 0.3f; // trunk/stem only
                if (go != null)
                {
                    bool any = false;
                    foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var r = mf.GetComponent<MeshRenderer>();
                        if (mf.sharedMesh == null || r == null) continue;
                        var src = r.sharedMaterials;
                        var mats = new Material[mf.sharedMesh.subMeshCount];
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var mat = PropMaterials.ForModel(i < src.Length ? src[i] : null);
                            mats[i] = mat;
                        }
                        var part = new Part
                        {
                            Mesh = mf.sharedMesh, Mats = mats,
                            Local = go.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix,
                        };
                        bool lod1 = mf.name.Contains("_LOD1") || mf.transform.parent != null && mf.transform.parent.name.Contains("_LOD1");
                        (lod1 ? m.Lod1 : m.Lod0).Add(part);
                        if (!lod1)
                        {
                            var b = TransformBounds(part.Local, mf.sharedMesh.bounds);
                            if (!any) { m.Bounds = b; any = true; } else m.Bounds.Encapsulate(b);
                        }
                    }
                    if (m.Lod1.Count == 0) m.Lod1 = m.Lod0;
                    if (m.Lod0.Count > 0)
                        Debug.Log($"WM_PROPS model {name} parts={m.Lod0.Count}/{m.Lod1.Count} color={m.Lod0[0].Mesh.HasVertexAttribute(VertexAttribute.Color)} " +
                                  $"fmt={(m.Lod0[0].Mesh.HasVertexAttribute(VertexAttribute.Color) ? m.Lod0[0].Mesh.GetVertexAttributeFormat(VertexAttribute.Color).ToString() : "-")} mats={string.Join(",", System.Array.ConvertAll(m.Lod0[0].Mats, x => x.name))}");
                }
                else Debug.LogWarning($"WM_PROPS missing model {name}");
                mModels[k] = m;
            }
        }

        static Bounds TransformBounds(Matrix4x4 m, Bounds b)
        {
            var c = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var x = m.MultiplyVector(new Vector3(e.x, 0, 0));
            var y = m.MultiplyVector(new Vector3(0, e.y, 0));
            var z = m.MultiplyVector(new Vector3(0, 0, e.z));
            var ext = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(c, ext * 2f);
        }

        public void OnChunkLoaded(Chunk c)
        {
            if (c.Props.Count == 0) return;
            EnsureModels();
            var list = new List<PropInst>(c.Props.Count);
            foreach (var p in c.Props)
            {
                if (p.Kind >= mModels.Length || mModels[p.Kind].Lod0.Count == 0) continue;
                if (!Supported(c, p)) continue;
                var pos = new Vector3(p.X + 0.5f, p.Y, p.Z + 0.5f);
                float s = p.Scale > 0 ? p.Scale : 1f;
                var m = Matrix4x4.TRS(pos, Quaternion.Euler(0, p.Yaw, 0), Vector3.one * s);
                list.Add(new PropInst { M = m, WorldBox = TransformBounds(m, mModels[p.Kind].Bounds), Kind = p.Kind, X = p.X, Y = p.Y, Z = p.Z });
            }
            if (list.Count > 0) mChunks[c] = list;
        }

        /// <summary>A prop survives only while its anchor is free and its support is solid (player edits persist this way).</summary>
        static bool Supported(Chunk c, PropPlacement p)
        {
            int lx = p.X - c.CX * 16, lz = p.Z - c.CZ * 16;
            if (lx < 0 || lx > 15 || lz < 0 || lz > 15 || p.Y < 1 || p.Y >= Chunk.SY) return false;
            byte at = c.Get(lx, p.Y, lz), below = c.Get(lx, p.Y - 1, lz);
            bool lily = PropCatalog.Names[p.Kind] == "LilyPad";
            if (lily) return at == B.Air && below == B.Water;
            return (at == B.Air || Blocks.IsPlant(at)) && Blocks.IsSolid[below];
        }

        public void OnChunkUnloaded(Chunk c) => mChunks.Remove(c);

        /// <summary>Called by the world after an edit: drop props whose anchor/support changed.</summary>
        public void OnBlockChanged(int x, int y, int z)
        {
            var w = VoxelWorld.Instance;
            var c = w?.ChunkAt(x, z);
            if (c == null || !mChunks.TryGetValue(c, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var it = list[i];
                bool anchor = it.X == x && it.Z == z && (it.Y == y || it.Y - 1 == y);
                bool inside = it.WorldBox.Contains(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) && mModels[it.Kind].Solid;
                if (anchor || inside) list.RemoveAt(i);
            }
        }

        bool OverlapsProps(Vector3 min, Vector3 max)
        {
            if (mModels == null) return false;
            var box = new Bounds((min + max) * 0.5f, max - min);
            var w = VoxelWorld.Instance;
            if (w == null) return false;
            int cx0 = Mathf.FloorToInt(min.x - 3) >> 4, cx1 = Mathf.FloorToInt(max.x + 3) >> 4;
            int cz0 = Mathf.FloorToInt(min.z - 3) >> 4, cz1 = Mathf.FloorToInt(max.z + 3) >> 4;
            for (int cz = cz0; cz <= cz1; cz++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    var c = w.GetChunk(cx, cz);
                    if (c == null || !mChunks.TryGetValue(c, out var list)) continue;
                    foreach (var it in list)
                    {
                        var model = mModels[it.Kind];
                        if (!model.Solid) continue;
                        var b = it.WorldBox;
                        var e = b.extents;
                        b.extents = new Vector3(e.x * model.SolidShrink, e.y * 0.98f, e.z * model.SolidShrink);
                        if (b.Intersects(box)) return true;
                    }
                }
            return false;
        }

        static readonly bool sNoProps = Core.Cli.Has("noprops");

        void LateUpdate()
        {
            if (sNoProps || mModels == null || mChunks.Count == 0) return;
            var cam = Camera.main;
            if (cam == null) return;
            GeometryUtility.CalculateFrustumPlanes(cam, mPlanes);
            var camPos = cam.transform.position;
            float lod2 = LodDistance * LodDistance, draw2 = DrawDistance * DrawDistance;

            foreach (var l in mBatches.Values) l.Clear();
            foreach (var kv in mChunks)
            {
                var c = kv.Key;
                var center = new Vector3(c.CX * 16 + 8, 0, c.CZ * 16 + 8);
                float dx = center.x - camPos.x, dz = center.z - camPos.z;
                if (dx * dx + dz * dz > draw2 + 512) continue;
                foreach (var it in kv.Value)
                {
                    if (!GeometryUtility.TestPlanesAABB(mPlanes, it.WorldBox)) continue;
                    float d2 = (it.WorldBox.center - camPos).sqrMagnitude;
                    if (d2 > draw2) continue;
                    var key = (it.Kind, d2 > lod2 ? 1 : 0);
                    if (!mBatches.TryGetValue(key, out var bl)) mBatches[key] = bl = new List<Matrix4x4>(64);
                    bl.Add(it.M);
                }
            }

            foreach (var kv in mBatches)
            {
                if (kv.Value.Count == 0) continue;
                var model = mModels[kv.Key.kind];
                var parts = kv.Key.lod == 0 ? model.Lod0 : model.Lod1;
                foreach (var part in parts)
                    for (int sub = 0; sub < part.Mats.Length; sub++)
                    {
                        var rp = new RenderParams(part.Mats[sub])
                        {
                            shadowCastingMode = kv.Key.lod == 0 ? ShadowCastingMode.On : ShadowCastingMode.Off,
                            receiveShadows = true,
                            layer = Layers.Props,
                            worldBounds = new Bounds(camPos, Vector3.one * DrawDistance * 2.2f),
                        };
                        int n = kv.Value.Count;
                        for (int start = 0; start < n; start += mScratch.Length)
                        {
                            int count = Mathf.Min(mScratch.Length, n - start);
                            for (int i = 0; i < count; i++) mScratch[i] = kv.Value[start + i] * part.Local;
                            // Measured on M4: SRP-batched RenderMesh beat RenderMeshInstanced (4.6 vs 5.1 ms/frame).
                            if (sInstancing) Graphics.RenderMeshInstanced(rp, part.Mesh, sub, mScratch, count);
                            else for (int i = 0; i < count; i++) Graphics.RenderMesh(rp, part.Mesh, sub, mScratch[i]);
                        }
                    }
            }
        }

        /// <summary>World position of the instance nearest to p (harness/debug), or null.</summary>
        public Vector3? Nearest(Vector3 p, string kind = null)
        {
            Vector3? best = null; float bd = float.MaxValue;
            foreach (var l in mChunks.Values)
                foreach (var it in l)
                {
                    if (kind != null && mModels[it.Kind].Name != kind) continue;
                    var c = new Vector3(it.X + 0.5f, it.Y, it.Z + 0.5f);
                    float d = (c - p).sqrMagnitude;
                    if (d < bd) { bd = d; best = c; }
                }
            return best;
        }

        /// <summary>Debug (-propaudit): props whose support block is gone or that sit far above the terrain.</summary>
        public int Audit()
        {
            var w = VoxelWorld.Instance;
            if (w == null || mModels == null) return 0;
            int total = 0, unsupported = 0, high = 0;
            var kinds = new Dictionary<string, int>();
            var examples = new List<string>();
            foreach (var kv in mChunks)
                foreach (var it in kv.Value)
                {
                    total++;
                    byte below = w.GetBlock(it.X, it.Y - 1, it.Z);
                    int surf = w.SurfaceY(it.X, it.Z);
                    bool bad = !Blocks.IsSolid[below] && PropCatalog.Names[it.Kind] != "LilyPad";
                    bool tall = surf > 0 && it.Y > surf + 2;
                    if (bad) unsupported++;
                    if (tall) high++;
                    if (bad || tall)
                    {
                        string k = PropCatalog.Names[it.Kind];
                        kinds[k] = kinds.TryGetValue(k, out int n) ? n + 1 : 1;
                        if (examples.Count < 8) examples.Add($"{k}@({it.X},{it.Y},{it.Z}) below={Blocks.Get(below).Name} surf={surf}");
                    }
                }
            var ks = new List<string>(); foreach (var kv in kinds) ks.Add(kv.Key + "=" + kv.Value);
            Debug.Log($"WM_PROPAUDIT total={total} unsupported={unsupported} aboveSurface={high} kinds=[{string.Join(",", ks)}] e.g. {string.Join(" ; ", examples)}");
            return unsupported + high;
        }

        public int InstanceCount { get { int n = 0; foreach (var l in mChunks.Values) n += l.Count; return n; } }
    }
}
