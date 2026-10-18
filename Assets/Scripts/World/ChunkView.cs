using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Wildmere.World
{
    /// <summary>Render objects for one column: one GameObject + Mesh with up to three submeshes.</summary>
    public sealed class ChunkView
    {
        public GameObject Go;
        public MeshFilter Filter;
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public int Vertices, Triangles;

        static readonly Stack<ChunkView> sPool = new Stack<ChunkView>();
        static Transform sRoot;
        static readonly List<Material> sMats = new List<Material>(3);

        const MeshUpdateFlags kFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices |
                                       MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;

        public static ChunkView Rent(Chunk c)
        {
            if (sRoot == null) sRoot = new GameObject("Chunks").transform;
            ChunkView v = null;
            while (sPool.Count > 0 && (v == null || v.Go == null)) v = sPool.Pop();
            if (v == null || v.Go == null) v = Create();
            v.Go.name = $"Chunk {c.CX},{c.CZ}";
            v.Go.transform.position = new Vector3(c.CX * 16, 0, c.CZ * 16);
            v.Go.SetActive(true);
            return v;
        }

        static ChunkView Create()
        {
            var go = new GameObject("Chunk");
            go.transform.SetParent(sRoot, false);
            go.layer = Layers.World;
            var v = new ChunkView { Go = go, Filter = go.AddComponent<MeshFilter>(), Renderer = go.AddComponent<MeshRenderer>() };
            v.Mesh = new Mesh { name = "chunk" };
            v.Mesh.MarkDynamic();
            v.Filter.sharedMesh = v.Mesh;
            v.Renderer.shadowCastingMode = ShadowCastingMode.On;
            v.Renderer.receiveShadows = true;
            v.Renderer.lightProbeUsage = LightProbeUsage.Off;
            v.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            v.Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            v.Renderer.allowOcclusionWhenDynamic = false;
            return v;
        }

        public static void Return(ChunkView v)
        {
            if (v == null || v.Go == null) return; // already destroyed (application quit)
            v.Mesh.Clear();
            v.Vertices = 0; v.Triangles = 0;
            v.Go.SetActive(false);
            sPool.Push(v);
        }

        public void Apply(MeshBuild b)
        {
            var streams = sStreams;
            streams.Clear();
            sMats.Clear();
            if (b.Opaque.ICount > 0) { streams.Add(b.Opaque); sMats.Add(VoxelMaterials.Opaque); }
            if (b.Cutout.ICount > 0) { streams.Add(b.Cutout); sMats.Add(VoxelMaterials.Cutout); }
            if (b.Water.ICount > 0) { streams.Add(b.Water); sMats.Add(VoxelMaterials.Water); }

            int vc = 0, ic = 0;
            foreach (var s in streams) { vc += s.VCount; ic += s.ICount; }
            Vertices = vc; Triangles = ic / 3;

            Mesh.Clear();
            if (vc == 0) { Renderer.enabled = false; return; }
            Renderer.enabled = true;

            Mesh.SetVertexBufferParams(vc, VoxelVertex.Layout);
            Mesh.SetIndexBufferParams(ic, IndexFormat.UInt32);
            int vo = 0, io = 0;
            Mesh.subMeshCount = streams.Count;
            var bounds = new Bounds(new Vector3(8, (b.MinY + b.MaxY) * 0.5f, 8), new Vector3(16, b.MaxY - b.MinY + 2, 16));
            for (int i = 0; i < streams.Count; i++)
            {
                var s = streams[i];
                Mesh.SetVertexBufferData(s.V, 0, vo, s.VCount, 0, kFlags);
                // Indices are local to each stream; baseVertex offsets them.
                Mesh.SetIndexBufferData(s.I, 0, io, s.ICount, kFlags);
                Mesh.SetSubMesh(i, new SubMeshDescriptor(io, s.ICount) { baseVertex = vo, bounds = bounds, firstVertex = vo, vertexCount = s.VCount }, kFlags);
                vo += s.VCount; io += s.ICount;
            }
            Mesh.bounds = bounds;
            Renderer.sharedMaterials = sMats.ToArray();
        }

        static readonly List<MeshStream> sStreams = new List<MeshStream>(3);
    }
}
