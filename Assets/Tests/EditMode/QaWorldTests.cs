using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Wildmere.Core;
using Wildmere.World;
using Object = UnityEngine.Object;

namespace Wildmere.Tests
{
    /// <summary>QA: the minimal JSON reader/writer used by saves and settings.</summary>
    public class QaJsonTests
    {
        [Test]
        public void RoundTrip_NestedValues()
        {
            var src = new Dictionary<string, object>
            {
                { "name", "Wörld \"quoted\" \\ back\nslash\t✓ 🌲" },
                { "seed", "-9223372036854775807" },
                { "n", 1234567.25 }, { "neg", -0.000125 }, { "big", 1e300 }, { "zero", 0.0 },
                { "t", true }, { "f", false }, { "nul", null },
                { "list", new List<object> { 1.0, "two", new Dictionary<string, object> { { "k", 3.0 } }, new List<object>() } },
                { "empty", new Dictionary<string, object>() },
                { "ctrl", "a\u0001b" },
            };
            foreach (bool pretty in new[] { false, true })
            {
                var back = Json.Parse(Json.Write(src, pretty)) as Dictionary<string, object>;
                Assert.IsNotNull(back);
                Assert.AreEqual(src["name"], back["name"]);
                Assert.AreEqual(src["seed"], back["seed"]);
                Assert.AreEqual(1234567.25, (double)back["n"]);
                Assert.AreEqual(-0.000125, (double)back["neg"]);
                Assert.AreEqual(1e300, (double)back["big"]);
                Assert.AreEqual(true, back["t"]); Assert.AreEqual(false, back["f"]);
                Assert.IsTrue(back.ContainsKey("nul") && back["nul"] == null);
                var l = (List<object>)back["list"];
                Assert.AreEqual(4, l.Count);
                Assert.AreEqual("two", l[1]);
                Assert.AreEqual(3.0, Json.Num(l[2], "k"));
                Assert.AreEqual(0, ((List<object>)l[3]).Count);
                Assert.AreEqual(0, ((Dictionary<string, object>)back["empty"]).Count);
                Assert.AreEqual("a\u0001b", back["ctrl"]);
            }
        }

        [Test]
        public void IntsFloatsLongs_WriteAsNumbers()
        {
            var back = (Dictionary<string, object>)Json.Parse(Json.Write(new Dictionary<string, object> { { "i", 7 }, { "f", 0.5f }, { "l", 1L << 40 } }));
            Assert.AreEqual(7.0, back["i"]);
            Assert.AreEqual(0.5, back["f"]);
            Assert.AreEqual((double)(1L << 40), back["l"]);
        }

        [Test]
        public void DoublesRoundTripExactly()
        {
            var rng = new System.Random(11);
            for (int i = 0; i < 500; i++)
            {
                double d = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-8, 9));
                var back = (List<object>)Json.Parse(Json.Write(new List<object> { d }));
                Assert.AreEqual(d, (double)back[0]);
            }
        }

        [Test]
        public void Accessors_FallBackOnMissingOrWrongType()
        {
            var o = (Dictionary<string, object>)Json.Parse("{\"a\":\"x\",\"b\":2,\"c\":true}");
            Assert.AreEqual(5, Json.Num(o, "a", 5));
            Assert.AreEqual("d", Json.Str(o, "b", "d"));
            Assert.IsTrue(Json.Bool(o, "zz", true));
            Assert.IsNull(Json.Obj(o, "a"));
            Assert.IsNull(Json.Arr(null, "a"));
        }

        [Test]
        public void MalformedInput_Throws()
        {
            foreach (var bad in new[] { "", "{", "{\"a\" 1}", "[1,2", "{\"a\":}", "tru", "\"unterminated" })
                Assert.Catch(() => Json.Parse(bad), $"'{bad}' should not parse");
        }

        [Test, Category("KnownDefect")]
        public void NonFiniteNumbers_ProduceParseableJson()
        {
            // QA-D: Json.Write emits NaN/Infinity verbatim ("NaN"), which Json.Parse (and any JSON reader) rejects.
            // A NaN player coordinate written by SaveWorld would make the whole world.json unreadable.
            string text = Json.Write(new Dictionary<string, object> { { "x", double.NaN }, { "y", double.PositiveInfinity } });
            Assert.DoesNotThrow(() => Json.Parse(text), text);
        }
    }

    /// <summary>QA: world persistence (mods.bin + world.json) in a temp directory.</summary>
    public class QaWorldSaveTests
    {
        string mDir;

        [SetUp] public void SetUp() { mDir = Path.Combine(Path.GetTempPath(), "wm-qa-save-" + Guid.NewGuid().ToString("N")); }
        [TearDown] public void TearDown() { if (Directory.Exists(mDir)) Directory.Delete(mDir, true); }

        [Test]
        public void Mods_RoundTrip_ManyChunks_NegativeCoords_Meta()
        {
            var s = new WorldSave(mDir);
            s.Name = "QA";
            s.Seed = -4611686018427387000L;
            var rng = new System.Random(42);
            var expect = new Dictionary<(int, int), Dictionary<int, ushort>>();
            for (int k = 0; k < 300; k++)
            {
                int cx = rng.Next(-40, 40), cz = rng.Next(-40, 40);
                var c = new Chunk(cx, cz);
                int idx = rng.Next(Chunk.Volume);
                byte b = (byte)rng.Next(0, B.Count);
                byte meta = (byte)(rng.NextDouble() < 0.3 ? rng.Next(1, 4) : 0);
                s.Record(c, idx, b, meta);
                if (!expect.TryGetValue((cx, cz), out var d)) expect[(cx, cz)] = d = new Dictionary<int, ushort>();
                d[idx] = (ushort)(b | (meta << 8));
            }
            // Extreme coordinates survive the key packing.
            foreach (var (cx, cz) in new[] { (int.MinValue / 32, int.MaxValue / 32), (-1, -1), (0, -1), (-1, 0), (1875000, -1875000) })
            {
                s.Record(new Chunk(cx, cz), 5, B.Glass, 0);
                expect[(cx, cz)] = new Dictionary<int, ushort> { { 5, B.Glass } };
            }
            s.Save();
            Assert.IsTrue(File.Exists(Path.Combine(mDir, "world.json")));
            Assert.IsTrue(File.Exists(Path.Combine(mDir, "mods.bin")));
            Assert.IsFalse(File.Exists(Path.Combine(mDir, "mods.bin.tmp")));

            var r = WorldSave.TryLoadMeta(mDir);
            Assert.IsNotNull(r);
            Assert.AreEqual("QA", r.Name);
            Assert.AreEqual(-4611686018427387000L, r.Seed);
            r.LoadMods();
            Assert.AreEqual(s.ModifiedBlocks, r.ModifiedBlocks);
            Assert.AreEqual(expect.Count, r.ModifiedChunks);
            foreach (var kv in expect)
            {
                var c = new Chunk(kv.Key.Item1, kv.Key.Item2);
                Assert.IsTrue(r.ApplyTo(c), $"chunk {kv.Key} not restored");
                foreach (var e in kv.Value)
                {
                    Assert.AreEqual((byte)(e.Value & 0xFF), c.Blocks[e.Key], $"chunk {kv.Key} idx {e.Key}");
                    byte meta = (byte)(e.Value >> 8);
                    c.Meta.TryGetValue(e.Key, out byte got);
                    Assert.AreEqual(meta, got, $"meta chunk {kv.Key} idx {e.Key}");
                }
            }
            Assert.IsFalse(r.ApplyTo(new Chunk(1000, 1000)), "untouched chunk reports no change");
        }

        [Test]
        public void LaterEditWins_AndRevertToAirIsKept()
        {
            var s = new WorldSave(mDir);
            var c = new Chunk(2, 3);
            s.Record(c, 100, B.Stone, 0);
            s.Record(c, 100, B.Furnace, 2);
            s.Record(c, 101, B.Air, 0);
            s.Save();
            var r = new WorldSave(mDir); r.LoadMods();
            var g = new Chunk(2, 3);
            g.Blocks[101] = B.Dirt; // generator content that the player dug out
            r.ApplyTo(g);
            Assert.AreEqual(B.Furnace, g.Blocks[100]);
            Assert.AreEqual(2, g.Meta[100]);
            Assert.AreEqual(B.Air, g.Blocks[101]);
        }

        [Test]
        public void SaveTwice_OverwritesCleanly()
        {
            var s = new WorldSave(mDir);
            s.Record(new Chunk(0, 0), 1, B.Stone, 0);
            s.Save();
            s.Record(new Chunk(0, 0), 2, B.Dirt, 0);
            s.Meta["time"] = 0.75;
            s.Save();
            var r = WorldSave.TryLoadMeta(mDir); r.LoadMods();
            Assert.AreEqual(2, r.ModifiedBlocks);
            Assert.AreEqual(0.75, Json.Num(r.Meta, "time"));
        }

        [Test]
        public void NewerFormat_IsRejected_CorruptMetaIsNull()
        {
            Directory.CreateDirectory(mDir);
            File.WriteAllText(Path.Combine(mDir, "world.json"), "{\"version\": 99, \"name\": \"future\"}");
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.IsNull(WorldSave.TryLoadMeta(mDir));
                File.WriteAllText(Path.Combine(mDir, "world.json"), "{\"version\": 1, \"name\": ");
                Assert.IsNull(WorldSave.TryLoadMeta(mDir));
                Assert.IsNull(WorldSave.TryLoadMeta(Path.Combine(mDir, "missing")));
            }
            finally { UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false; }
        }

        [Test]
        public void CorruptModsFile_Throws()
        {
            Directory.CreateDirectory(mDir);
            File.WriteAllBytes(Path.Combine(mDir, "mods.bin"), new byte[] { 1, 2, 3, 4 });
            Assert.Catch(() => new WorldSave(mDir).LoadMods());
        }

        [Test]
        public void Slug_IsFilesystemSafe()
        {
            Assert.AreEqual("my-world", WorldSave.Slug("My World"));
            Assert.AreEqual("world", WorldSave.Slug("///"));
            Assert.AreEqual("a-b", WorldSave.Slug("a/b"));
            foreach (var n in new[] { "..", "../../etc", "C:\\x", "con", "a:b*c?d" })
                Assert.IsFalse(WorldSave.Slug(n).Contains("/") || WorldSave.Slug(n).Contains("\\") || WorldSave.Slug(n).Contains(".."), n);
        }
    }

    /// <summary>QA: greedy mesher face counts, culling, merging and winding.</summary>
    public class QaMesherTests
    {
        static Chunk[] Area()
        {
            var n = new Chunk[9];
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    n[dx + 1 + (dz + 1) * 3] = new Chunk(dx, dz) { State = ChunkState.Generated };
            return n;
        }

        static void Finish(Chunk[] n) { foreach (var c in n) c.RecomputeHeights(); }

        static MeshBuild Mesh(Chunk[] n)
        {
            var b = new MeshBuild();
            new ChunkMesher().Build(n, b);
            return b;
        }

        static float Area(MeshStream s, int nx, int ny, int nz, out int quads)
        {
            float a = 0; quads = 0;
            for (int q = 0; q < s.VCount / 4; q++)
            {
                var v = s.V[q * 4];
                if (Math.Sign(v.NX) != nx || Math.Sign(v.NY) != ny || Math.Sign(v.NZ) != nz) continue;
                var p0 = s.V[q * 4].Pos; var p1 = s.V[q * 4 + 1].Pos; var p3 = s.V[q * 4 + 3].Pos;
                a += Vector3.Cross(p1 - p0, p3 - p0).magnitude;
                quads++;
            }
            return a;
        }

        static void AssertWinding(MeshStream s, string ctx)
        {
            for (int t = 0; t < s.ICount; t += 3)
            {
                var a = s.V[s.I[t]]; var b = s.V[s.I[t + 1]]; var c = s.V[s.I[t + 2]];
                var geo = Vector3.Cross(b.Pos - a.Pos, c.Pos - a.Pos);
                var decl = new Vector3(a.NX, a.NY, a.NZ);
                Assert.Greater(Vector3.Dot(geo, decl), 0f, $"{ctx}: triangle {t / 3} faces away from its normal {decl} (geo {geo})");
            }
        }

        [Test]
        public void SingleBlock_SixFaces_24Verts_CorrectWinding()
        {
            var n = Area();
            n[4].Blocks[Chunk.Index(8, 100, 8)] = B.Stone;
            Finish(n);
            var m = Mesh(n);
            Assert.AreEqual(24, m.Opaque.VCount);
            Assert.AreEqual(36, m.Opaque.ICount);
            Assert.AreEqual(0, m.Cutout.VCount + m.Water.VCount);
            AssertWinding(m.Opaque, "single stone");
            // Every face sits on the block's boundary.
            for (int i = 0; i < m.Opaque.VCount; i++)
            {
                var p = m.Opaque.V[i].Pos;
                Assert.IsTrue(p.x >= 8 && p.x <= 9 && p.y >= 100 && p.y <= 101 && p.z >= 8 && p.z <= 9, $"vertex {p} outside the block");
            }
        }

        [Test]
        public void AdjacentBlocks_HideSharedFace()
        {
            var n = Area();
            n[4].Blocks[Chunk.Index(8, 100, 8)] = B.Stone;
            n[4].Blocks[Chunk.Index(9, 100, 8)] = B.Dirt;
            Finish(n);
            var m = Mesh(n);
            float total = 0;
            foreach (var d in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                total += Area(m.Opaque, d.Item1, d.Item2, d.Item3, out _);
            Assert.AreEqual(10f, total, 1e-3f, "two cubes expose 10 unit faces");
            for (int i = 0; i < m.Opaque.VCount; i++)
                if (m.Opaque.V[i].NX != 0) Assert.AreNotEqual(9f, m.Opaque.V[i].Pos.x, 1e-4f, "a face was emitted on the shared x = 9 plane");
            AssertWinding(m.Opaque, "two blocks");
        }

        [Test]
        public void ChunkBorder_NeighbourCullsFace_AndAirNeighbourShowsIt()
        {
            var n = Area();
            n[4].Blocks[Chunk.Index(15, 70, 3)] = B.Stone;   // centre column, x = 15
            n[5].Blocks[Chunk.Index(0, 70, 3)] = B.Stone;    // +X neighbour, x = 0
            n[4].Blocks[Chunk.Index(0, 80, 5)] = B.Stone;    // west edge, open to the -X neighbour
            Finish(n);
            var m = Mesh(n);
            Area(m.Opaque, 1, 0, 0, out int px);
            Area(m.Opaque, -1, 0, 0, out int nx);
            Assert.AreEqual(1, px, "+X: only the west-edge block shows (the border one is covered by the neighbour)");
            Assert.AreEqual(2, nx, "-X: both blocks show");
            bool westFace = false;
            for (int i = 0; i < m.Opaque.VCount; i++) if (m.Opaque.V[i].NX < 0 && Mathf.Approximately(m.Opaque.V[i].Pos.x, 0f) && m.Opaque.V[i].Pos.y >= 80f) westFace = true;
            Assert.IsTrue(westFace, "face on the chunk's west border (x = 0) toward an air neighbour must exist");
        }

        [Test]
        public void FlatFloor_GreedyMergesIntoOneTopQuad()
        {
            var n = Area();
            foreach (var c in n)
                for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++) c.Blocks[Chunk.Index(x, 64, z)] = B.Stone;
            Finish(n);
            var m = Mesh(n);
            float top = Area(m.Opaque, 0, 1, 0, out int topQuads);
            Assert.AreEqual(1, topQuads, "uniformly lit 16x16 top should be one quad");
            Assert.AreEqual(256f, top, 1e-3f);
            float sides = Area(m.Opaque, 1, 0, 0, out _) + Area(m.Opaque, -1, 0, 0, out _) + Area(m.Opaque, 0, 0, 1, out _) + Area(m.Opaque, 0, 0, -1, out _);
            Assert.AreEqual(0f, sides, 1e-3f, "neighbours are full, no side faces");
            float bottom = Area(m.Opaque, 0, -1, 0, out int bq);
            Assert.AreEqual(256f, bottom, 1e-3f);
            TestContext.WriteLine($"bottom quads={bq} total verts={m.Opaque.VCount}");
            AssertWinding(m.Opaque, "floor");
        }

        [Test]
        public void BottomOfWorld_NotMeshed_AndPlantsAreCutout()
        {
            var n = Area();
            n[4].Blocks[Chunk.Index(4, 0, 4)] = B.Bedrock;
            n[4].Blocks[Chunk.Index(4, 1, 4)] = B.Grass;
            n[4].Blocks[Chunk.Index(4, 2, 4)] = B.TallGrass;
            n[4].Blocks[Chunk.Index(6, 1, 6)] = B.Torch;
            Finish(n);
            var m = Mesh(n);
            for (int i = 0; i < m.Opaque.VCount; i++)
                Assert.IsFalse(m.Opaque.V[i].NY < 0 && m.Opaque.V[i].Pos.y <= 0f, "bottom face at y = 0 drawn");
            Assert.AreEqual(32, m.Cutout.VCount, "two cross plants = 2 planes x 2 sides each = 8 quads");
        }

        [Test]
        public void Water_TopIsLowered_AndHidesWaterWaterFaces()
        {
            var n = Area();
            for (int x = 2; x < 5; x++) n[4].Blocks[Chunk.Index(x, 62, 2)] = B.Water;
            Finish(n);
            var m = Mesh(n);
            Assert.Greater(m.Water.VCount, 0);
            for (int i = 0; i < m.Water.VCount; i++)
            {
                var v = m.Water.V[i];
                if (v.NY > 0) Assert.AreEqual(62.875f, v.Pos.y, 1e-4f, "surface water top is drawn lowered");
                if (v.NX != 0) Assert.IsTrue(Mathf.Approximately(v.Pos.x, 2f) || Mathf.Approximately(v.Pos.x, 5f), "internal water face");
            }
            AssertWinding(m.Water, "water");
        }

        [Test]
        public void FancyLeavesOff_HidesInnerLeafFaces()
        {
            var n = Area();
            n[4].Blocks[Chunk.Index(5, 90, 5)] = B.LeavesOak;
            n[4].Blocks[Chunk.Index(6, 90, 5)] = B.LeavesOak;
            Finish(n);
            var fancy = new MeshBuild(); new ChunkMesher { FancyLeaves = true }.Build(n, fancy);
            var fast = new MeshBuild(); new ChunkMesher { FancyLeaves = false }.Build(n, fast);
            float fancyArea = 0, area = 0;
            foreach (var d in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                fancyArea += Area(fancy.Cutout, d.Item1, d.Item2, d.Item3, out _);
            Assert.AreEqual(12f, fancyArea, 1e-3f, "fancy: all 12 faces incl. the shared pair");
            foreach (var d in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                area += Area(fast.Cutout, d.Item1, d.Item2, d.Item3, out _);
            Assert.AreEqual(10f, area, 1e-3f, "fast: shared leaf face culled");
        }

        [Test]
        public void FurnaceFacing_UsesFrontLayerOnTheFacingSide()
        {
            var d = Blocks.Get(B.Furnace);
            int lf = d.LFront, ls = d.LSide;
            try
            {
                d.LFront = 7; d.LSide = 3; // resolved layers are only filled when textures load
                foreach (var (meta, nx, nz) in new[] { (0, 0, 1), (1, 1, 0), (2, 0, -1), (3, -1, 0) })
                {
                    var n = Area();
                    int idx = Chunk.Index(8, 70, 8);
                    n[4].Blocks[idx] = B.Furnace;
                    n[4].Meta[idx] = (byte)meta;
                    Finish(n);
                    var m = Mesh(n);
                    int fronts = 0;
                    for (int q = 0; q < m.Opaque.VCount / 4; q++)
                    {
                        var v = m.Opaque.V[q * 4];
                        if ((int)v.UV.z != 7 || v.NY != 0) continue;
                        fronts++;
                        Assert.AreEqual(nx, Math.Sign(v.NX), $"meta {meta}");
                        Assert.AreEqual(nz, Math.Sign(v.NZ), $"meta {meta}");
                    }
                    Assert.AreEqual(1, fronts, $"meta {meta}: exactly one front face");
                }
            }
            finally { d.LFront = lf; d.LSide = ls; }
        }
    }

    /// <summary>QA: voxel collision and ray queries against a hand-built world.</summary>
    public class QaPhysicsTests
    {
        GameObject mGo;
        VoxelWorld mW;
        Dictionary<long, Chunk> mChunks;
        Func<Vector3, Vector3, bool> mExtra;

        [SetUp]
        public void SetUp()
        {
            mExtra = VoxelPhysics.ExtraSolid;
            VoxelPhysics.ExtraSolid = null;
            mGo = new GameObject("QA VoxelWorld");
            mW = mGo.AddComponent<VoxelWorld>();
            var f = typeof(VoxelWorld).GetField("mChunks", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(f, "VoxelWorld.mChunks not found (test needs updating)");
            mChunks = (Dictionary<long, Chunk>)f.GetValue(mW);
            for (int cz = -2; cz <= 2; cz++)
                for (int cx = -2; cx <= 2; cx++)
                {
                    var c = new Chunk(cx, cz) { State = ChunkState.Generated };
                    for (int z = 0; z < 16; z++)
                        for (int x = 0; x < 16; x++)
                        {
                            c.Blocks[Chunk.Index(x, 0, z)] = B.Bedrock;
                            for (int y = 1; y <= 64; y++) c.Blocks[Chunk.Index(x, y, z)] = B.Stone;
                        }
                    c.RecomputeHeights();
                    mChunks[Chunk.Key(cx, cz)] = c;
                }
        }

        [TearDown]
        public void TearDown()
        {
            mChunks.Clear();
            Object.DestroyImmediate(mGo);
            VoxelPhysics.ExtraSolid = mExtra;
        }

        void Set(int x, int y, int z, byte b) => mW.ChunkAt(x, z).Blocks[Chunk.Index(x & 15, y, z & 15)] = b;

        [Test]
        public void GetBlock_Bounds()
        {
            Assert.AreEqual(B.Stone, mW.GetBlock(3, 64, -7));
            Assert.AreEqual(B.Air, mW.GetBlock(3, 65, -7));
            Assert.AreEqual(B.Bedrock, mW.GetBlock(3, -1, -7));
            Assert.AreEqual(B.Air, mW.GetBlock(3, 256, -7));
            Assert.AreEqual(64, mW.SurfaceY(-20, 20));
            Assert.AreEqual(-1, mW.SurfaceY(500, 500), "unloaded column");
        }

        [Test]
        public void Raycast_Down_HitsTopFace()
        {
            var h = VoxelPhysics.Raycast(mW, new Vector3(0.5f, 67.62f, 0.5f), Vector3.down, 5f);
            Assert.IsTrue(h.Hit);
            Assert.AreEqual(new Vector3Int(0, 64, 0), h.Block);
            Assert.AreEqual(Vector3Int.up, h.Normal);
            Assert.AreEqual(new Vector3Int(0, 65, 0), h.Adjacent);
            Assert.AreEqual(2.62f, h.Distance, 1e-3f);
        }

        [Test]
        public void Raycast_RespectsReach()
        {
            Assert.IsFalse(VoxelPhysics.Raycast(mW, new Vector3(0.5f, 75f, 0.5f), Vector3.down, 5f).Hit);
            Assert.IsTrue(VoxelPhysics.Raycast(mW, new Vector3(0.5f, 69.9f, 0.5f), Vector3.down, 5f).Hit);
        }

        [Test]
        public void Raycast_WaterSkippedUnlessRequested_PlantsHit()
        {
            Set(2, 65, 2, B.Water);
            var o = new Vector3(2.5f, 68f, 2.5f);
            Assert.AreEqual(new Vector3Int(2, 64, 2), VoxelPhysics.Raycast(mW, o, Vector3.down, 6f).Block);
            Assert.AreEqual(new Vector3Int(2, 65, 2), VoxelPhysics.Raycast(mW, o, Vector3.down, 6f, true).Block);
            Set(3, 65, 3, B.TallGrass);
            var h = VoxelPhysics.Raycast(mW, new Vector3(3.5f, 68f, 3.5f), Vector3.down, 6f);
            Assert.AreEqual(B.TallGrass, h.Id);
        }

        [Test]
        public void Raycast_MatchesBruteForce()
        {
            var rng = new System.Random(9);
            for (int i = 0; i < 40; i++) Set(rng.Next(-20, 20), 65 + rng.Next(0, 6), rng.Next(-20, 20), B.Planks);
            for (int k = 0; k < 400; k++)
            {
                var o = new Vector3((float)rng.NextDouble() * 30 - 15, 66f + (float)rng.NextDouble() * 6, (float)rng.NextDouble() * 30 - 15);
                var d = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.7f, (float)rng.NextDouble() - 0.5f).normalized;
                var h = VoxelPhysics.Raycast(mW, o, d, 8f);
                // Brute force: march in tiny steps.
                Vector3Int? first = null;
                float tFirst = 0;
                for (float t = 0; t <= 8f; t += 0.002f)
                {
                    var p = o + d * t;
                    var c = new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
                    if (mW.GetBlock(c.x, c.y, c.z) != B.Air) { first = c; tFirst = t; break; }
                }
                if (first == null) { Assert.IsFalse(h.Hit && h.Distance < 7.95f, $"ray {k}: phantom hit {h.Block} at {h.Distance}"); continue; }
                if (!h.Hit) { Assert.Greater(tFirst, 7.95f, $"ray {k}: missed {first.Value} at {tFirst}"); continue; }
                Assert.AreEqual(first.Value, h.Block, $"ray {k} o={o} d={d}");
                Assert.AreEqual(tFirst, h.Distance, 0.01f, $"ray {k}: hit distance");
                if (h.Distance == 0f) continue; // origin inside a block: no face
                Assert.AreEqual(1, Mathf.Abs(h.Normal.x) + Mathf.Abs(h.Normal.y) + Mathf.Abs(h.Normal.z), "unit face normal");
                Assert.AreEqual(B.Air, mW.GetBlock(h.Adjacent.x, h.Adjacent.y, h.Adjacent.z), $"ray {k}: adjacent cell not empty");
            }
        }

        [Test]
        public void Move_FallLandsFlush()
        {
            var pos = new Vector3(0.5f, 70f, 0.5f);
            var b = VoxelPhysics.Move(mW, ref pos, new Vector3(0, -10f, 0), 0.3f, 1.8f);
            Assert.AreEqual(-1, b.y);
            Assert.AreEqual(65f, pos.y, 0.01f);
            Assert.IsTrue(VoxelPhysics.Grounded(mW, pos, 0.3f));
        }

        [Test]
        public void Move_NoTunnellingAtHighSpeed()
        {
            // A one-block floor over a cavity; a 55 m/s fall in one 50 ms frame must still stop on it.
            for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) for (int y = 40; y <= 64; y++) Set(x, y, z, B.Air);
            for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) Set(x, 50, z, B.Stone);
            var pos = new Vector3(0.5f, 52.5f, 0.5f);
            VoxelPhysics.Move(mW, ref pos, new Vector3(0, -55f * 0.05f, 0), 0.3f, 1.8f);
            Assert.AreEqual(51f, pos.y, 0.01f);
            var p2 = new Vector3(10.5f, 70f, 10.5f);
            VoxelPhysics.Move(mW, ref p2, new Vector3(0, -40f, 0), 0.3f, 1.8f);
            Assert.AreEqual(65f, p2.y, 0.01f, "40 m in one call");
        }

        [Test]
        public void Move_WallStopsFlush_AndSlides()
        {
            for (int y = 65; y < 68; y++) for (int z = -3; z <= 3; z++) Set(2, y, z, B.Cobblestone);
            var pos = new Vector3(0.5f, 65.001f, 0.5f);
            var b = VoxelPhysics.Move(mW, ref pos, new Vector3(3f, 0, 1f), 0.3f, 1.8f);
            Assert.AreEqual(1, b.x);
            Assert.AreEqual(0, b.z);
            Assert.LessOrEqual(pos.x + 0.3f, 2f + 1e-4f);
            Assert.Greater(pos.x + 0.3f, 1.99f);
            Assert.AreEqual(1.5f, pos.z, 1e-3f, "slides along the wall");
        }

        [Test]
        public void Move_HeadBump()
        {
            Set(0, 67, 0, B.Stone);
            var pos = new Vector3(0.5f, 65.001f, 0.5f);
            var b = VoxelPhysics.Move(mW, ref pos, new Vector3(0, 1f, 0), 0.3f, 1.8f);
            Assert.AreEqual(1, b.y);
            Assert.LessOrEqual(pos.y + 1.8f, 67f + 1e-4f);
        }

        [Test]
        public void NonSolidBlocks_DoNotCollide()
        {
            Set(0, 65, 0, B.TallGrass); Set(0, 66, 0, B.Water); Set(1, 65, 0, B.Torch);
            Assert.IsFalse(VoxelPhysics.Overlaps(mW, new Vector3(0.1f, 65.01f, 0.1f), new Vector3(1.9f, 66.9f, 0.9f)));
            Set(1, 66, 0, B.LeavesOak);
            Assert.IsTrue(VoxelPhysics.Overlaps(mW, new Vector3(0.1f, 65.01f, 0.1f), new Vector3(1.9f, 66.9f, 0.9f)), "leaves are solid");
        }

        [Test]
        public void Grounded_OverhangingEdge()
        {
            for (int y = 1; y <= 64; y++) Set(1, y, 0, B.Air); // a pit at x = 1
            // Centre over the pit but still overlapping x < 1 by 0.1 m: grounded.
            Assert.IsTrue(VoxelPhysics.Grounded(mW, new Vector3(1.2f, 65f, 0.5f), 0.3f));
            Assert.IsFalse(VoxelPhysics.Grounded(mW, new Vector3(1.5f, 65f, 0.5f), 0.3f));
            Assert.IsFalse(VoxelPhysics.Grounded(mW, new Vector3(0.5f, 66f, 0.5f), 0.3f), "in the air");
        }

        [Test]
        public void Submersion_SamplesFeetWaistEyes()
        {
            Set(0, 65, 0, B.Water);
            Assert.AreEqual(0.67f, VoxelPhysics.Submersion(mW, new Vector3(0.5f, 65f, 0.5f), 1.8f), 1e-3f);
            Set(0, 66, 0, B.Water);
            Assert.AreEqual(1f, VoxelPhysics.Submersion(mW, new Vector3(0.5f, 65f, 0.5f), 1.8f), 1e-3f);
            Assert.AreEqual(0f, VoxelPhysics.Submersion(mW, new Vector3(5.5f, 65f, 5.5f), 1.8f), 1e-3f);
        }
    }
}
