using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Wildmere.World
{
    /// <summary>
    /// Small standalone meshes of single blocks in the voxel vertex format (drops, held blocks, icons).
    /// Cube blocks become a cube centred on the origin with the given size; plants become crossed quads.
    /// Cached per block id.
    /// </summary>
    public static class BlockMeshes
    {
        static readonly Dictionary<int, Mesh> sCache = new Dictionary<int, Mesh>();

        public static bool IsCutout(byte id)
        {
            var s = Blocks.ShapeOf[id];
            return s == RenderShape.Cross || s == RenderShape.Leaves || s == RenderShape.Glass;
        }

        public static Material MaterialFor(byte id) => IsCutout(id) ? VoxelMaterials.Cutout : id == B.Water ? VoxelMaterials.Water : VoxelMaterials.Opaque;

        /// <summary>Unit-size mesh centred on the origin (scale the transform for drops/viewmodels).</summary>
        public static Mesh Get(byte id)
        {
            if (sCache.TryGetValue(id, out var m) && m != null) return m;
            var d = Blocks.Get(id);
            var verts = new List<VoxelVertex>();
            var idx = new List<int>();
            int flags = 0;
            if (d.Emissive) flags |= VFlags.Emissive;
            if (d.Tint == TintKind.Grass) flags |= VFlags.TintGrass;
            if (d.Tint == TintKind.Foliage) flags |= VFlags.TintFoliage;

            void Quad(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Vector3 n, int layer)
            {
                int b = verts.Count;
                VoxelVertex V(Vector3 p, float u, float v) => new VoxelVertex
                {
                    Pos = p, NX = (sbyte)(n.x * 127), NY = (sbyte)(n.y * 127), NZ = (sbyte)(n.z * 127),
                    UV = new Vector4(u, v, layer, flags), Sky = 255, Blk = 0, Ao = 255, Rnd = 128,
                };
                verts.Add(V(bl, 0, 0)); verts.Add(V(br, 1, 0)); verts.Add(V(tr, 1, 1)); verts.Add(V(tl, 0, 1));
                idx.Add(b); idx.Add(b + 3); idx.Add(b + 2); idx.Add(b); idx.Add(b + 2); idx.Add(b + 1);
            }

            const float h = 0.5f;
            if (d.Shape == RenderShape.Cross)
            {
                var a0 = new Vector3(-h, -h, -h); var a1 = new Vector3(h, -h, h);
                var b0 = new Vector3(-h, -h, h); var b1 = new Vector3(h, -h, -h);
                var up = Vector3.up;
                Quad(a0, a1, a1 + Vector3.up, a0 + Vector3.up, up, d.LSide);
                Quad(a1, a0, a0 + Vector3.up, a1 + Vector3.up, up, d.LSide);
                Quad(b0, b1, b1 + Vector3.up, b0 + Vector3.up, up, d.LSide);
                Quad(b1, b0, b0 + Vector3.up, b1 + Vector3.up, up, d.LSide);
            }
            else
            {
                // Faces seen from outside: BL, BR, TR, TL with u = viewer right, v = viewer up.
                Quad(new Vector3(h, -h, -h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(h, h, -h), Vector3.right, d.LSide);
                Quad(new Vector3(-h, -h, h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(-h, h, h), Vector3.left, d.LSide);
                Quad(new Vector3(-h, h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), new Vector3(-h, h, h), Vector3.up, d.LTop);
                Quad(new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, -h, -h), new Vector3(-h, -h, -h), Vector3.down, d.LBottom);
                Quad(new Vector3(h, -h, h), new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(h, h, h), Vector3.forward, d.LSide);
                Quad(new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(-h, h, -h), Vector3.back, d.LFront);
            }

            m = new Mesh { name = "block-" + d.Key };
            m.SetVertexBufferParams(verts.Count, VoxelVertex.Layout);
            m.SetVertexBufferData(verts, 0, 0, verts.Count);
            m.SetIndexBufferParams(idx.Count, IndexFormat.UInt32);
            m.SetIndexBufferData(idx, 0, 0, idx.Count);
            m.subMeshCount = 1;
            m.SetSubMesh(0, new SubMeshDescriptor(0, idx.Count));
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1.5f);
            sCache[id] = m;
            return m;
        }
    }
}
