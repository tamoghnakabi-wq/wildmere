using System;
using System.IO;
using System.Threading;

namespace Wildmere.World.Gen
{
    /// <summary>
    /// Top-down world maps for inspecting the generator (pure C#, no Unity API): generates every chunk of an
    /// area in parallel and renders a biome map (biome colours + hillshade + water) and a surface map
    /// (top-block colours + hillshade). Used by the editor batch method and the standalone harness.
    /// </summary>
    public static class WorldMap
    {
        public sealed class Result
        {
            public int Width, Height;
            public byte[] BiomeRgb, SurfaceRgb;
            public double MsPerChunk;
            public int Chunks;
        }

        static readonly byte[][] kBiome =
        {
            new byte[] { 28, 62, 128 },   // Ocean
            new byte[] { 222, 208, 150 }, // Beach
            new byte[] { 150, 196, 92 },  // Plains
            new byte[] { 62, 136, 56 },   // TemperateForest
            new byte[] { 30, 78, 38 },    // DarkForest
            new byte[] { 226, 196, 112 }, // Desert
            new byte[] { 236, 240, 246 }, // SnowyMountains
            new byte[] { 64, 128, 214 },  // River
            new byte[] { 210, 226, 230 }, // SnowyPlains
            new byte[] { 140, 140, 140 }, // Mountains
        };

        static byte[] BlockColour(byte b)
        {
            switch (b)
            {
                case B.Grass: return new byte[] { 104, 158, 64 };
                case B.Dirt: return new byte[] { 120, 86, 60 };
                case B.Stone: return new byte[] { 128, 128, 124 };
                case B.Sand: return new byte[] { 222, 204, 150 };
                case B.Sandstone: return new byte[] { 206, 178, 126 };
                case B.Gravel: return new byte[] { 130, 122, 112 };
                case B.Clay: return new byte[] { 150, 158, 170 };
                case B.Snow: return new byte[] { 244, 248, 252 };
                case B.SnowyGrass: return new byte[] { 226, 234, 240 };
                case B.Ice: return new byte[] { 168, 206, 236 };
                case B.Water: return new byte[] { 40, 90, 170 };
                case B.Podzol: return new byte[] { 96, 70, 42 };
                case B.LeavesOak: return new byte[] { 52, 110, 40 };
                case B.LeavesBirch: return new byte[] { 110, 150, 60 };
                case B.LeavesSpruce: return new byte[] { 40, 80, 56 };
                case B.LeavesDark: return new byte[] { 26, 62, 26 };
                case B.Cactus: return new byte[] { 70, 130, 50 };
                case B.LogOak: case B.LogBirch: case B.LogSpruce: case B.LogDark: return new byte[] { 90, 66, 40 };
                default: return new byte[] { 255, 0, 255 };
            }
        }

        /// <summary>Render chunks [cx0, cx0+n) x [cz0, cz0+n); one pixel per <paramref name="step"/> blocks.</summary>
        public static Result Render(TerrainGenerator gen, int cx0, int cz0, int n, int step, int threads)
        {
            int size = n * 16, w = size / step;
            var top = new short[size * size];
            var topBlock = new byte[size * size];
            var water = new short[size * size];   // water/ice surface y or -1
            var floorY = new short[size * size];
            var biome = new byte[size * size];
            int next = -1;
            long ticks = 0;
            var workers = new Thread[Math.Max(1, threads)];
            for (int t = 0; t < workers.Length; t++)
            {
                workers[t] = new Thread(() =>
                {
                    var sw = new System.Diagnostics.Stopwatch();
                    while (true)
                    {
                        int k = Interlocked.Increment(ref next);
                        if (k >= n * n) break;
                        int cx = cx0 + k % n, cz = cz0 + k / n;
                        var c = new Chunk(cx, cz);
                        sw.Restart();
                        gen.Generate(c);
                        Interlocked.Add(ref ticks, sw.ElapsedTicks);
                        for (int z = 0; z < 16; z++)
                            for (int x = 0; x < 16; x++)
                            {
                                int gx = (cx - cx0) * 16 + x, gz = (cz - cz0) * 16 + z, gi = gx + gz * size;
                                short wy = -1, ty = 0, fy = 0; byte tb = 0;
                                for (int y = c.MaxY; y > 0; y--)
                                {
                                    byte b = c.Blocks[x + (z << 4) + (y << 8)];
                                    if (b == B.Air || Blocks.IsPlant(b)) continue;
                                    if (b == B.Water || b == B.Ice)
                                    {
                                        if (wy < 0) { wy = (short)y; if (tb == 0) { tb = b; ty = (short)y; } }
                                        continue;
                                    }
                                    if (tb == 0 || tb == B.Water || tb == B.Ice) { if (tb == 0) { tb = b; ty = (short)y; } }
                                    fy = (short)y;
                                    break;
                                }
                                top[gi] = ty; topBlock[gi] = tb; water[gi] = wy; floorY[gi] = fy;
                                biome[gi] = c.Biome[x + z * 16];
                            }
                    }
                });
                workers[t].Start();
            }
            foreach (var t in workers) t.Join();

            var res = new Result { Width = w, Height = w, BiomeRgb = new byte[w * w * 3], SurfaceRgb = new byte[w * w * 3], Chunks = n * n };
            res.MsPerChunk = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / (n * n);
            for (int pz = 0; pz < w; pz++)
                for (int px = 0; px < w; px++)
                {
                    int gx = px * step, gz = pz * step, gi = gx + gz * size;
                    int gxl = Math.Max(0, gx - step), gxr = Math.Min(size - 1, gx + step);
                    int gzu = Math.Max(0, gz - step), gzd = Math.Min(size - 1, gz + step);
                    double hl = Math.Max(floorY[gxl + gz * size], water[gxl + gz * size]), hr = Math.Max(floorY[gxr + gz * size], water[gxr + gz * size]);
                    double hu = Math.Max(floorY[gx + gzu * size], water[gx + gzu * size]), hd = Math.Max(floorY[gx + gzd * size], water[gx + gzd * size]);
                    double shade = 1.0 + ((hl - hr) + (hu - hd)) / (2.0 * step) * 0.35;
                    shade = Math.Max(0.45, Math.Min(1.5, shade));
                    bool wet = water[gi] > floorY[gi];
                    double depth = wet ? water[gi] - floorY[gi] : 0;

                    byte bi = biome[gi];
                    var bc = kBiome[bi < kBiome.Length ? bi : 2];
                    double r = bc[0], g = bc[1], b = bc[2];
                    if (wet)
                    {
                        double k = Math.Max(0.35, 1.0 - depth / 30.0);
                        r *= k; g *= k; b = b * (0.6 + 0.4 * k);
                        if (topBlock[gi] == B.Ice) { r = 190; g = 220; b = 240; }
                    }
                    else
                    {
                        // altitude tint: lighter with height so mountains read as relief
                        double alt = Math.Max(0, Math.Min(1, (floorY[gi] - 64) / 170.0));
                        r = r * shade + alt * 25; g = g * shade + alt * 25; b = b * shade + alt * 25;
                    }
                    int o = (px + pz * w) * 3;
                    res.BiomeRgb[o] = Clamp(r); res.BiomeRgb[o + 1] = Clamp(g); res.BiomeRgb[o + 2] = Clamp(b);

                    var sc = BlockColour(topBlock[gi]);
                    double sr = sc[0], sg = sc[1], sb = sc[2];
                    if (topBlock[gi] == B.Water)
                    {
                        double k = Math.Max(0.35, 1.0 - depth / 30.0);
                        sr *= k; sg *= k; sb *= 0.6 + 0.4 * k;
                    }
                    else { sr *= shade; sg *= shade; sb *= shade; }
                    res.SurfaceRgb[o] = Clamp(sr); res.SurfaceRgb[o + 1] = Clamp(sg); res.SurfaceRgb[o + 2] = Clamp(sb);
                }
            return res;
        }

        static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        // ------------------------------------------------------------------ PNG (RGB8) writer

        public static void WritePng(string path, int w, int h, byte[] rgb)
        {
            using (var fs = File.Create(path))
            {
                fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Chunk(fs, "IHDR", ihdr);
                var raw = new byte[(w * 3 + 1) * h];
                for (int y = 0; y < h; y++)
                {
                    raw[y * (w * 3 + 1)] = 0;
                    Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
                }
                byte[] z;
                using (var ms = new MemoryStream())
                {
                    ms.WriteByte(0x78); ms.WriteByte(0x01);
                    // Stored (uncompressed) deflate blocks: no native zlib needed (Mono's DeflateStream lacks it outside Unity).
                    for (int o = 0; o < raw.Length || o == 0; o += 65535)
                    {
                        int len = Math.Min(65535, raw.Length - o);
                        ms.WriteByte((byte)(o + len >= raw.Length ? 1 : 0));
                        ms.WriteByte((byte)len); ms.WriteByte((byte)(len >> 8));
                        ms.WriteByte((byte)~len); ms.WriteByte((byte)(~len >> 8));
                        ms.Write(raw, o, len);
                        if (len == 0) break;
                    }
                    uint a = Adler32(raw);
                    ms.WriteByte((byte)(a >> 24)); ms.WriteByte((byte)(a >> 16)); ms.WriteByte((byte)(a >> 8)); ms.WriteByte((byte)a);
                    z = ms.ToArray();
                }
                Chunk(fs, "IDAT", z);
                Chunk(fs, "IEND", new byte[0]);
            }
        }

        static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be(len, 0, data.Length); s.Write(len, 0, 4);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4); s.Write(data, 0, data.Length);
            uint crc = Crc(Crc(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
            var cb = new byte[4]; Be(cb, 0, (int)crc); s.Write(cb, 0, 4);
        }

        static uint[] sCrc;
        static uint Crc(uint c, byte[] d)
        {
            if (sCrc == null)
            {
                var tab = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint v = n;
                    for (int k = 0; k < 8; k++) v = (v & 1) != 0 ? 0xEDB88320u ^ (v >> 1) : v >> 1;
                    tab[n] = v;
                }
                sCrc = tab;
            }
            foreach (byte b in d) c = sCrc[(c ^ b) & 0xFF] ^ (c >> 8);
            return c;
        }

        static uint Adler32(byte[] d)
        {
            uint a = 1, b = 0;
            foreach (byte x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }
    }
}
