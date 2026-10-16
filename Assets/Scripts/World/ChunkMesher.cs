using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Wildmere.World
{
    /// <summary>GPU vertex for every voxel mesh (36 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VoxelVertex
    {
        public Vector3 Pos;
        public sbyte NX, NY, NZ, NW;      // normal (SNorm8x4); NW unused
        public Vector4 UV;                // u, v (block units), texture layer, flags
        public byte Sky, Blk, Ao, Rnd;    // UNorm8x4: sky light, block light, ambient occlusion, per-block random

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.UNorm8, 4),
        };
    }

    /// <summary>Shader flags packed into uv.w (keep in sync with VoxelCommon.hlsl).</summary>
    public static class VFlags
    {
        public const int Emissive = 1, Wind = 2, Plant = 4, TintGrass = 8, TintFoliage = 16, Water = 32;
    }

    /// <summary>Growable vertex/index buffer pair for one submesh.</summary>
    public sealed class MeshStream
    {
        public VoxelVertex[] V = new VoxelVertex[4096];
        public int[] I = new int[6144];
        public int VCount, ICount;

        public void Clear() { VCount = 0; ICount = 0; }

        public void Quad(in VoxelVertex a, in VoxelVertex b, in VoxelVertex c, in VoxelVertex d, bool flip)
        {
            if (VCount + 4 > V.Length) Array.Resize(ref V, V.Length * 2);
            if (ICount + 6 > I.Length) Array.Resize(ref I, I.Length * 2);
            int v0 = VCount;
            V[v0] = a; V[v0 + 1] = b; V[v0 + 2] = c; V[v0 + 3] = d;
            VCount += 4;
            // Vertices arrive as BL, BR, TR, TL seen from the front; Unity front faces are clockwise.
            if (!flip)
            {
                I[ICount++] = v0; I[ICount++] = v0 + 3; I[ICount++] = v0 + 2;
                I[ICount++] = v0; I[ICount++] = v0 + 2; I[ICount++] = v0 + 1;
            }
            else
            {
                I[ICount++] = v0 + 1; I[ICount++] = v0; I[ICount++] = v0 + 3;
                I[ICount++] = v0 + 1; I[ICount++] = v0 + 3; I[ICount++] = v0 + 2;
            }
        }
    }

    /// <summary>Result of meshing one column: three submeshes (opaque, cutout, water).</summary>
    public sealed class MeshBuild
    {
        public readonly MeshStream Opaque = new MeshStream(), Cutout = new MeshStream(), Water = new MeshStream();
        public int MinY, MaxY;
        public void Clear() { Opaque.Clear(); Cutout.Clear(); Water.Clear(); }
    }

    /// <summary>
    /// Builds the render mesh of one column from a 3 x 3 neighbourhood. Thread-safe: one instance per
    /// worker thread (it owns large scratch buffers).
    ///
    /// Lighting: sky and block light are flood-filled over the padded 48 x 48 region at meshing time, so
    /// no light state is stored or kept in sync across chunks — an edit just remeshes the columns within
    /// light range. Vertex light/AO use Minecraft-style smooth lighting; faces are greedy-merged only when
    /// their four corners are uniform (so merging never changes the shading).
    /// </summary>
    public sealed class ChunkMesher
    {
        const int P = 48;           // padded region width (3 chunks)
        const int O = 16;           // offset of the centre chunk inside the region
        const int Layer = P * P;    // stride of one y layer

        int mH;                     // region height actually filled (exclusive)
        readonly byte[] mB = new byte[Layer * Chunk.SY];
        readonly byte[] mSky = new byte[Layer * Chunk.SY];
        readonly byte[] mBlk = new byte[Layer * Chunk.SY];
        int[] mQueue = new int[1 << 16];

        // Greedy masks (one slice): packed face key per cell, 0 = no face.
        readonly ulong[] mMask = new ulong[16 * Chunk.SY];

        public bool FancyLeaves = true;

        static int R(int x, int y, int z) => x + z * P + y * Layer;

        /// <summary>
        /// n[3x3] are the centre column and its neighbours, indexed [dx+1 + (dz+1)*3]; all must be Generated.
        /// </summary>
        public void Build(Chunk[] n, MeshBuild outp)
        {
            outp.Clear();
            Gather(n);
            ComputeLight();
            Mesh(n[4], outp);
        }

        void Gather(Chunk[] n)
        {
            int top = 0;
            for (int i = 0; i < 9; i++) top = Math.Max(top, n[i].MaxY);
            mH = Math.Min(Chunk.SY, top + 2);
            for (int cz = 0; cz < 3; cz++)
                for (int cx = 0; cx < 3; cx++)
                {
                    var src = n[cx + cz * 3].Blocks;
                    for (int y = 0; y < mH; y++)
                        for (int z = 0; z < 16; z++)
                            Buffer.BlockCopy(src, (z << 4) + (y << 8), mB, R(cx * 16, y, cz * 16 + z), 16);
                }
            if (mH < Chunk.SY) Array.Clear(mB, mH * Layer, Layer); // one air layer above the top
        }

        void Push(int idx)
        {
            if (mQHead == mQueue.Length) Array.Resize(ref mQueue, mQueue.Length * 2);
            mQueue[mQHead++] = idx;
        }

        int mQHead;

        void ComputeLight()
        {
            var opaque = Blocks.IsOpaque;
            var absorb = Blocks.Absorb;
            var emit = Blocks.Emit;
            int h = mH;

            // Vertical sky pass + block-light sources.
            Array.Clear(mBlk, 0, h * Layer);
            mQHead = 0;
            int maxDark = 0; // highest y where any column stops being fully sky-lit
            for (int z = 0; z < P; z++)
                for (int x = 0; x < P; x++)
                {
                    int L = 15;
                    for (int y = h - 1; y >= 0; y--)
                    {
                        int i = R(x, y, z);
                        byte b = mB[i];
                        if (opaque[b]) L = 0;
                        else if (absorb[b] > 0) L = Math.Max(0, L - absorb[b]);
                        if (L < 15 && y > maxDark) maxDark = y;
                        mSky[i] = (byte)L;
                        if (emit[b] > 0) mBlk[i] = emit[b];
                    }
                }

            // Sky seeds: lit cells beside a darker transparent cell (nothing above maxDark+1 can qualify).
            int seedTop = Math.Min(h - 1, maxDark + 1);
            for (int z = 1; z < P - 1; z++)
                for (int x = 1; x < P - 1; x++)
                    for (int y = seedTop; y >= 1; y--)
                    {
                        int i = R(x, y, z);
                        int L = mSky[i];
                        if (L <= 1) continue;
                        if ((mSky[i + 1] < L - 1 && !opaque[mB[i + 1]]) || (mSky[i - 1] < L - 1 && !opaque[mB[i - 1]]) ||
                            (mSky[i + P] < L - 1 && !opaque[mB[i + P]]) || (mSky[i - P] < L - 1 && !opaque[mB[i - P]]) ||
                            (mSky[i - Layer] < L - 1 && !opaque[mB[i - Layer]]))
                            Push(i);
                    }
            Flood(mSky);

            mQHead = 0;
            for (int i = 0; i < h * Layer; i++) if (mBlk[i] > 1) Push(i);
            Flood(mBlk);
        }

        void Flood(byte[] light)
        {
            var opaque = Blocks.IsOpaque;
            var absorb = Blocks.Absorb;
            int h = mH;
            int head = 0;
            while (head < mQHead)
            {
                int i = mQueue[head++];
                int L = light[i];
                if (L <= 1) continue;
                int y = i / Layer, rem = i - y * Layer, z = rem / P, x = rem - z * P;
                if (x > 0) Spread(light, i - 1, L, opaque, absorb);
                if (x < P - 1) Spread(light, i + 1, L, opaque, absorb);
                if (z > 0) Spread(light, i - P, L, opaque, absorb);
                if (z < P - 1) Spread(light, i + P, L, opaque, absorb);
                if (y > 0) Spread(light, i - Layer, L, opaque, absorb);
                if (y < h - 1) Spread(light, i + Layer, L, opaque, absorb);
            }
            mQHead = 0;
        }

        void Spread(byte[] light, int j, int L, bool[] opaque, byte[] absorb)
        {
            byte b = mB[j];
            if (opaque[b]) return;
            int nl = L - 1 - absorb[b];
            if (nl > light[j]) { light[j] = (byte)nl; Push(j); }
        }

        // ---------------------------------------------------------------- meshing

        // Face directions: 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z
        static readonly int[] kDX = { 1, -1, 0, 0, 0, 0 };
        static readonly int[] kDY = { 0, 0, 1, -1, 0, 0 };
        static readonly int[] kDZ = { 0, 0, 0, 0, 1, -1 };

        bool FaceVisible(byte self, byte nb)
        {
            var s = Blocks.ShapeOf[self];
            if (Blocks.IsOpaque[nb]) return false;
            switch (s)
            {
                case RenderShape.Cube: return true;
                case RenderShape.Leaves: return !Blocks.IsLeaves(nb) || FancyLeaves;
                case RenderShape.Glass: return nb != self;
                case RenderShape.Liquid: return nb != self && Blocks.ShapeOf[nb] != RenderShape.Liquid && nb != B.Ice;
                default: return false;
            }
        }

        int LayerFor(BlockDef d, int dir, int idx, Chunk c)
        {
            if (dir == 2) return d.LTop;
            if (dir == 3) return d.LBottom;
            byte facing = 0;
            bool has;
            if (d.LFront == d.LSide) has = false;
            else lock (c.Meta) has = c.Meta.TryGetValue(idx, out facing);
            if (has)
            {
                // facing 0 +Z, 1 +X, 2 -Z, 3 -X
                int fdir = facing == 0 ? 4 : facing == 1 ? 0 : facing == 2 ? 5 : 1;
                if (fdir == dir) return d.LFront;
            }
            return d.LSide;
        }

        static int FlagsFor(BlockDef d)
        {
            int f = 0;
            if (d.Emissive) f |= VFlags.Emissive;
            if (d.Wind) f |= d.Shape == RenderShape.Cross ? VFlags.Plant | VFlags.Wind : VFlags.Wind;
            if (d.Tint == TintKind.Grass) f |= VFlags.TintGrass;
            if (d.Tint == TintKind.Foliage) f |= VFlags.TintFoliage;
            if (d.Shape == RenderShape.Liquid) f |= VFlags.Water;
            return f;
        }

        void Mesh(Chunk c, MeshBuild outp)
        {
            int maxY = Math.Min(c.MaxY + 1, mH - 1);
            outp.MinY = 0; outp.MaxY = maxY + 1;

            // Cubes, leaves, glass, liquids: greedy per direction and slice.
            for (int dir = 0; dir < 6; dir++)
            {
                int axis = dir < 2 ? 0 : dir < 4 ? 1 : 2;
                if (axis == 1)
                {
                    for (int y = 0; y <= maxY; y++) Slice(c, outp, dir, y, 16, 16);
                }
                else
                {
                    for (int s = 0; s < 16; s++) Slice(c, outp, dir, s, 16, maxY + 1);
                }
            }

            // Cross-shaped plants and torches.
            for (int y = 1; y <= maxY; y++)
                for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++)
                    {
                        int ri = R(x + O, y, z + O);
                        byte b = mB[ri];
                        if (Blocks.ShapeOf[b] != RenderShape.Cross) continue;
                        Cross(c, outp, x, y, z, b, ri);
                    }
        }

        /// <summary>
        /// One greedy slice in "plane" coordinates: for X faces (u = z, v = y), Y faces (u = x, v = z),
        /// Z faces (u = x, v = y). w and h are the mask dimensions.
        /// </summary>
        void Slice(Chunk c, MeshBuild outp, int dir, int s, int w, int h)
        {
            int dx = kDX[dir], dy = kDY[dir], dz = kDZ[dir];
            int frontOff = dx + dz * P + dy * Layer;
            int n = 0;
            for (int v = 0; v < h; v++)
                for (int u = 0; u < w; u++, n++)
                {
                    mMask[n] = 0;
                    PlaneToLocal(dir, s, u, v, out int x, out int y, out int z);
                    int ri = R(x + O, y, z + O);
                    byte b = mB[ri];
                    if (b == B.Air) continue;
                    var shape = Blocks.ShapeOf[b];
                    if (shape == RenderShape.Cross || shape == RenderShape.None) continue;
                    int ny = y + dy;
                    byte nb = ny < 0 ? B.Bedrock : ny >= mH ? B.Air : mB[ri + frontOff];
                    if (!FaceVisible(b, nb)) continue;

                    var d = Blocks.Defs[b];
                    int layer = LayerFor(d, dir, Chunk.Index(x, y, z), c);
                    int flags = FlagsFor(d);
                    // Water under air is drawn lowered; such faces never merge with full-height ones.
                    bool lowered = shape == RenderShape.Liquid && dir != 3 && (y + 1 >= mH || mB[ri + Layer] != B.Water);
                    int kind = shape == RenderShape.Liquid ? 2 : shape == RenderShape.Cube ? 0 : 1;

                    ulong corner0 = CornerKey(ri + frontOff, dir, out bool uniform);
                    ulong key = 1UL | ((ulong)kind << 1) | ((ulong)(lowered ? 1 : 0) << 3) | ((ulong)flags << 4) |
                                ((ulong)layer << 12) | (corner0 << 24);
                    if (!uniform) key |= 1UL << 63; // faces with a light/AO gradient are emitted alone
                    mMask[n] = key;
                }

            n = 0;
            for (int v = 0; v < h; v++)
                for (int u = 0; u < w;)
                {
                    ulong key = mMask[n];
                    if (key == 0) { u++; n++; continue; }
                    int rw = 1, rh = 1;
                    if ((key >> 63) == 0)
                    {
                        while (u + rw < w && mMask[n + rw] == key) rw++;
                        bool done = false;
                        while (v + rh < h && !done)
                        {
                            for (int k = 0; k < rw; k++)
                                if (mMask[n + k + rh * w] != key) { done = true; break; }
                            if (!done) rh++;
                        }
                    }
                    EmitFace(outp, dir, s, u, v, rw, rh, key);
                    for (int j = 0; j < rh; j++)
                        for (int k = 0; k < rw; k++) mMask[n + k + j * w] = 0;
                    u += rw; n += rw;
                }
        }

        static void PlaneToLocal(int dir, int s, int u, int v, out int x, out int y, out int z)
        {
            if (dir < 2) { x = s; z = u; y = v; }
            else if (dir < 4) { x = u; z = v; y = s; }
            else { x = u; z = s; y = v; }
        }

        static void PlaneAxes(int dir, out int ua, out int va)
        {
            if (dir < 2) { ua = P; va = Layer; }       // u = z, v = y
            else if (dir < 4) { ua = 1; va = P; }      // u = x, v = z
            else { ua = 1; va = Layer; }               // u = x, v = y
        }

        static uint SplitHash(int x, int y, int z)
        {
            uint hsh = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791);
            hsh ^= hsh >> 13; hsh *= 0x5bd1e995; hsh ^= hsh >> 15;
            return hsh;
        }

        /// <summary>Packed (ao, sky, block) of corner 0 plus whether all four corners agree.</summary>
        ulong CornerKey(int front, int dir, out bool uniform)
        {
            Corner(front, dir, 0, 0, out byte s0, out byte b0, out byte a0);
            uniform = true;
            for (int k = 1; k < 4 && uniform; k++)
            {
                Corner(front, dir, k == 1 || k == 2 ? 1 : 0, k >= 2 ? 1 : 0, out byte s, out byte b, out byte a);
                if (s != s0 || b != b0 || a != a0) uniform = false;
            }
            return (ulong)a0 | ((ulong)s0 << 8) | ((ulong)b0 << 16); // 24 bits
        }

        bool Op(int i) => i >= 0 && i < mH * Layer && Blocks.IsOpaque[mB[i]];
        int Sky(int i) => i < 0 ? 0 : i >= mH * Layer ? 15 : mSky[i];
        int Blk(int i) => i >= 0 && i < mH * Layer ? mBlk[i] : 0;

        /// <summary>
        /// Smooth light + AO for one corner of a face whose front cell is <paramref name="front"/>.
        /// (iu, iv) pick the corner in plane space: 0 = towards -u / -v, 1 = towards +u / +v.
        /// </summary>
        void Corner(int front, int dir, int iu, int iv, out byte sky, out byte blk, out byte ao)
        {
            PlaneAxes(dir, out int ua, out int va);
            int su = iu == 1 ? ua : -ua, sv = iv == 1 ? va : -va;
            int e1 = front + su, e2 = front + sv, dg = front + su + sv;
            bool o1 = Op(e1), o2 = Op(e2), od = Op(dg);
            int a = (o1 && o2) ? 0 : 3 - ((o1 ? 1 : 0) + (o2 ? 1 : 0) + (od ? 1 : 0));
            int s = Sky(front), b = Blk(front), cnt = 1;
            if (!o1) { s += Sky(e1); b += Blk(e1); cnt++; }
            if (!o2) { s += Sky(e2); b += Blk(e2); cnt++; }
            if (!od && !(o1 && o2)) { s += Sky(dg); b += Blk(dg); cnt++; }
            sky = (byte)Math.Min(255, s * 17 / cnt);
            blk = (byte)Math.Min(255, b * 17 / cnt);
            ao = (byte)(a * 85);
        }

        // Emission order of plane corners (iu, iv) per direction, giving face-space BL, BR, TR, TL as seen
        // from outside the block (u = viewer right, v = viewer up). See VoxelCommon.hlsl tangent rules.
        static readonly int[][] kOrder =
        {
            new[] { 0,0, 1,0, 1,1, 0,1 },   // +X  (face u = +z)
            new[] { 1,0, 0,0, 0,1, 1,1 },   // -X  (face u = -z)
            new[] { 0,0, 1,0, 1,1, 0,1 },   // +Y  (face u = +x, v = +z)
            new[] { 0,1, 1,1, 1,0, 0,0 },   // -Y  (face u = +x, v = -z)
            new[] { 1,0, 0,0, 0,1, 1,1 },   // +Z  (face u = -x)
            new[] { 0,0, 1,0, 1,1, 0,1 },   // -Z  (face u = +x)
        };

        readonly VoxelVertex[] mQuad = new VoxelVertex[4];

        void EmitFace(MeshBuild outp, int dir, int s, int u, int v, int rw, int rh, ulong key)
        {
            int kind = (int)((key >> 1) & 3);
            bool lowered = ((key >> 3) & 1) != 0;
            int flags = (int)((key >> 4) & 0xFF);
            int layer = (int)((key >> 12) & 0xFFF);
            float top = lowered ? 0.875f : 1f;
            int dx = kDX[dir], dy = kDY[dir], dz = kDZ[dir];
            int frontOff = dx + dz * P + dy * Layer;
            var stream = kind == 0 ? outp.Opaque : kind == 1 ? outp.Cutout : outp.Water;
            sbyte nx = (sbyte)(dx * 127), ny = (sbyte)(dy * 127), nz = (sbyte)(dz * 127);

            PlaneToLocal(dir, s, u, v, out int bx0, out int by0, out int bz0);
            byte rnd = (byte)(SplitHash(bx0, by0, bz0) & 0xFF);
            var order = kOrder[dir];
            for (int k = 0; k < 4; k++)
            {
                int iu = order[k * 2], iv = order[k * 2 + 1];
                // Own cell of this corner (inside the rectangle) and its lighting.
                PlaneToLocal(dir, s, u + iu * (rw - 1), v + iv * (rh - 1), out int cx, out int cy, out int cz);
                int front = R(cx + O, cy, cz + O) + frontOff;
                Corner(front, dir, iu, iv, out byte sky, out byte blk, out byte ao);

                // Corner position (local to the chunk).
                float pu = u + iu * rw, pv = v + iv * rh;
                Vector3 p;
                if (dir < 2) p = new Vector3(s + (dir == 0 ? 1 : 0), iv == 1 ? v + rh - 1 + top : v, pu);
                else if (dir < 4) p = new Vector3(pu, s + (dir == 2 ? top : 0f), pv);
                else p = new Vector3(pu, iv == 1 ? v + rh - 1 + top : v, s + (dir == 4 ? 1 : 0));

                // UVs in block units (tangent rules): +X u=z, -X u=-z, +Z u=-x, -Z u=x, ±Y u=x;
                // sides v=y, top v=z, bottom v=-z.
                float tu, tv;
                switch (dir)
                {
                    case 0: tu = p.z; tv = p.y; break;
                    case 1: tu = -p.z; tv = p.y; break;
                    case 2: tu = p.x; tv = p.z; break;
                    case 3: tu = p.x; tv = -p.z; break;
                    case 4: tu = -p.x; tv = p.y; break;
                    default: tu = p.x; tv = p.y; break;
                }
                mQuad[k] = new VoxelVertex
                {
                    Pos = p, NX = nx, NY = ny, NZ = nz, NW = 0,
                    UV = new Vector4(tu, tv, layer, flags),
                    Sky = sky, Blk = blk, Ao = ao, Rnd = rnd,
                };
            }
            // Use the diagonal between the darker pair so AO interpolates without the classic crease.
            bool flip = mQuad[0].Ao + mQuad[2].Ao > mQuad[1].Ao + mQuad[3].Ao;
            stream.Quad(mQuad[0], mQuad[1], mQuad[2], mQuad[3], flip);
        }

        void Cross(Chunk c, MeshBuild outp, int x, int y, int z, byte b, int ri)
        {
            var d = Blocks.Defs[b];
            int flags = FlagsFor(d);
            int layer = d.LSide;
            uint hsh = SplitHash(c.CX * 16 + x, y, c.CZ * 16 + z);
            // Random offset/size so fields of grass don't look gridded (torches stay centred).
            float ox = 0, oz = 0, hgt = 1f;
            if (b != B.Torch)
            {
                ox = ((hsh & 0xFF) / 255f - 0.5f) * 0.36f;
                oz = (((hsh >> 8) & 0xFF) / 255f - 0.5f) * 0.36f;
                hgt = 0.8f + ((hsh >> 16) & 0xFF) / 255f * 0.35f;
            }
            byte sky = (byte)Math.Min(255, mSky[ri] * 17), blk = (byte)Math.Min(255, mBlk[ri] * 17);
            byte rnd = (byte)(hsh >> 24);
            float cx = x + 0.5f + ox, cz = z + 0.5f + oz;
            const float r = 0.45f;
            float s = b == B.Torch ? 0.5f : 1f;
            for (int k = 0; k < 2; k++)
            {
                float ax = k == 0 ? r : r, az = k == 0 ? r : -r;
                var a0 = new Vector3(cx - ax * s, y, cz - az * s);
                var a1 = new Vector3(cx + ax * s, y, cz + az * s);
                var a2 = new Vector3(cx + ax * s, y + hgt * (b == B.Torch ? 0.75f : 1f), cz + az * s);
                var a3 = new Vector3(cx - ax * s, y + hgt * (b == B.Torch ? 0.75f : 1f), cz - az * s);
                VoxelVertex V(Vector3 p, float u, float v) => new VoxelVertex
                {
                    Pos = p, NX = 0, NY = 127, NZ = 0, UV = new Vector4(u, v, layer, flags),
                    Sky = sky, Blk = blk, Ao = 255, Rnd = rnd,
                };
                // Both sides (no cull-off material needed).
                outp.Cutout.Quad(V(a0, 0, 0), V(a3, 0, 1), V(a2, 1, 1), V(a1, 1, 0), false);
                outp.Cutout.Quad(V(a1, 1, 0), V(a2, 1, 1), V(a3, 0, 1), V(a0, 0, 0), false);
            }
        }
    }
}
