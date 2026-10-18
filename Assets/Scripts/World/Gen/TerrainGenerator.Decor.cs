using System;
using static Wildmere.World.Gen.GenMath;

namespace Wildmere.World.Gen
{
    public sealed partial class TerrainGenerator
    {
        const int PropCell = 5;

        // Per-climate prop tables: kind, probability per 5x5 cell.
        static readonly byte[] kPropPlains = { PropCatalog.BoulderA, PropCatalog.BoulderB, PropCatalog.RocksSmallA, PropCatalog.RocksSmallB, PropCatalog.BushA, PropCatalog.BushB };
        static readonly double[] kPropPlainsP = { 0.020, 0.010, 0.030, 0.020, 0.050, 0.045 };
        static readonly byte[] kPropForest = { PropCatalog.FallenLogOak, PropCatalog.FallenLogBirch, PropCatalog.Stump, PropCatalog.MushroomCluster, PropCatalog.BoulderC, PropCatalog.BoulderA, PropCatalog.BushA, PropCatalog.BushB, PropCatalog.RocksSmallA };
        static readonly double[] kPropForestP = { 0.030, 0.025, 0.035, 0.060, 0.025, 0.015, 0.060, 0.035, 0.020 };
        static readonly byte[] kPropDark = { PropCatalog.GiantMushroom, PropCatalog.GlowcapCluster, PropCatalog.MushroomCluster, PropCatalog.FallenLogOak, PropCatalog.Stump, PropCatalog.BoulderC };
        static readonly double[] kPropDarkP = { 0.050, 0.075, 0.070, 0.035, 0.035, 0.025 };
        static readonly byte[] kPropDesert = { PropCatalog.SandstonePillar, PropCatalog.DeadTree, PropCatalog.RocksSmallB };
        static readonly double[] kPropDesertP = { 0.020, 0.020, 0.035 };
        static readonly byte[] kPropSnow = { PropCatalog.IceSpike, PropCatalog.DeadTree, PropCatalog.BoulderB, PropCatalog.BoulderA, PropCatalog.RocksSmallA, PropCatalog.Stump };
        static readonly double[] kPropSnowP = { 0.040, 0.012, 0.025, 0.020, 0.020, 0.012 };
        static readonly byte[] kPropBeach = { PropCatalog.RocksSmallB };
        static readonly double[] kPropBeachP = { 0.03 };

        static bool Solidish(byte b) => Blocks.IsSolid[b] && !(b >= B.LeavesOak && b <= B.LeavesDark) && !(b >= B.LogOak && b <= B.LogDark) && b != B.Cactus;

        void PlaceProps(Chunk ch, Scratch s, int bx, int bz)
        {
            var blocks = ch.Blocks;
            // 1. Land props on a global 5-block jittered grid (anchors and footprints stay inside this chunk).
            int ci0 = FloorDiv(bx, PropCell), ci1 = FloorDiv(bx + 15, PropCell);
            int cj0 = FloorDiv(bz, PropCell), cj1 = FloorDiv(bz + 15, PropCell);
            for (int cj = cj0; cj <= cj1; cj++)
                for (int ci = ci0; ci <= ci1; ci++)
                {
                    ulong h = SplitMix.Hash(Seed, ci, cj, 301);
                    int x = ci * PropCell + (int)(h % PropCell), z = cj * PropCell + (int)((h >> 8) % PropCell);
                    int lx = x - bx, lz = z - bz;
                    if ((uint)lx > 15 || (uint)lz > 15) continue;
                    Col c = GetCol(s, x, z);
                    if (c.Water > c.H) continue;
                    double roll = ((h >> 16) & 0xFFFFF) / 1048576.0;
                    byte[] kinds; double[] probs;
                    if ((c.Flags & FBeach) != 0) { kinds = kPropBeach; probs = kPropBeachP; }
                    else switch ((Biome)c.Climate)
                    {
                        case Biome.Plains: kinds = kPropPlains; probs = kPropPlainsP; break;
                        case Biome.TemperateForest: kinds = kPropForest; probs = kPropForestP; break;
                        case Biome.DarkForest: kinds = kPropDark; probs = kPropDarkP; break;
                        case Biome.Desert: kinds = kPropDesert; probs = kPropDesertP; break;
                        case Biome.SnowyMountains: kinds = kPropSnow; probs = kPropSnowP; break;
                        default: continue;
                    }
                    int kind = -1; double acc = 0;
                    for (int k = 0; k < kinds.Length; k++) { acc += probs[k]; if (roll < acc) { kind = kinds[k]; break; } }
                    if (kind < 0) continue;

                    int ay = c.H + 1;
                    if (ay + 6 >= Chunk.SY) continue;
                    byte ground = blocks[lx + (lz << 4) + (c.H << 8)];
                    switch (kind)
                    {
                        case PropCatalog.IceSpike: if (ground != B.Snow) continue; break;
                        case PropCatalog.SandstonePillar: if (ground != B.Sand && ground != B.Sandstone) continue; break;
                        case PropCatalog.DeadTree: if (ground != B.Sand && ground != B.SnowyGrass && ground != B.Snow) continue; break;
                        case PropCatalog.Stump: case PropCatalog.FallenLogOak: case PropCatalog.FallenLogBirch:
                        case PropCatalog.BushA: case PropCatalog.BushB: case PropCatalog.MushroomCluster:
                        case PropCatalog.GiantMushroom: case PropCatalog.GlowcapCluster:
                            if (!TreeGround(ground)) continue; break;
                    }
                    int rad = PropCatalog.Radius[kind], hgt = PropCatalog.Height[kind];
                    if (lx < rad || lx > 15 - rad || lz < rad || lz > 15 - rad) continue;
                    if (!FootprintClear(ch, s, lx, ay, lz, rad, hgt)) continue;

                    for (int dz = -rad; dz <= rad; dz++)
                        for (int dx = -rad; dx <= rad; dx++) s.Reserved[lx + dx + (lz + dz) * 16] = true;
                    ch.Props.Add(new PropPlacement { X = x, Y = ay, Z = z, Kind = (byte)kind, Yaw = (float)(((h >> 40) & 0xFFF) / 4096.0 * 360.0), Scale = PropScale(kind, h) });
                }

            // 2. Reeds along dry shores, lily pads on still fresh water.
            for (int lz = 0; lz < 16; lz++)
                for (int lx = 0; lx < 16; lx++)
                {
                    int i = lx + lz * 16;
                    if (s.Reserved[i]) continue;
                    int x = bx + lx, z = bz + lz;
                    Col c = GetCol(s, x, z);
                    var clim = (Biome)c.Climate;
                    if (clim == Biome.Desert || c.T < 0.2f) continue;
                    ulong h = SplitMix.Hash(Seed, x, z, 311);
                    double r = (h & 0xFFFFF) / 1048576.0;
                    double cluster = SS(-0.1, 0.5, nFlower.Sample(x / 15.0, z / 15.0) / SigPerlin * 0.4);
                    int col = lx + (lz << 4);
                    if (c.Water > c.H)
                    {
                        if ((c.Flags & (FLake | FRiver)) == 0 || (c.Flags & FFrozen) != 0 || c.Water >= 250) continue;
                        if (r >= 0.006 + 0.11 * cluster) continue;
                        if (blocks[col + (c.Water << 8)] != B.Water || blocks[col + ((c.Water + 1) << 8)] != B.Air) continue;
                        if (Near(s, lx, lz)) continue;
                        s.Reserved[i] = true;
                        ch.Props.Add(new PropPlacement { X = x, Y = c.Water + 1, Z = z, Kind = PropCatalog.LilyPad, Yaw = (float)(((h >> 24) & 0xFFF) / 4096.0 * 360.0), Scale = PropScale(PropCatalog.LilyPad, h) });
                    }
                    else if (s.NWater[i] > 0 && c.H <= s.NWater[i] + 1 && c.H < 250)
                    {
                        if (r >= 0.03 + 0.30 * cluster) continue;
                        byte g = blocks[col + (c.H << 8)];
                        if (g != B.Sand && g != B.Grass && g != B.Dirt && g != B.Clay && g != B.Podzol) continue;
                        if (blocks[col + ((c.H + 1) << 8)] != B.Air || blocks[col + ((c.H + 2) << 8)] != B.Air) continue;
                        s.Reserved[i] = true;
                        ch.Props.Add(new PropPlacement { X = x, Y = c.H + 1, Z = z, Kind = PropCatalog.Reeds, Yaw = (float)(((h >> 24) & 0xFFF) / 4096.0 * 360.0), Scale = PropScale(PropCatalog.Reeds, h) });
                    }
                }

            // 3. Glowing fungi on cave floors.
            var rng = new SplitMix(Seed ^ unchecked(Chunk.Key(ch.CX, ch.CZ) * 0x2545F4914F6CDD1DL));
            int firstCave = ch.Props.Count;
            for (int k = 0; k < 10; k++)
            {
                int lx = rng.Range(0, 16), lz = rng.Range(0, 16);
                Col c = GetCol(s, bx + lx, bz + lz);
                int top = Math.Min(c.H - 12, 120);
                int y = top > 12 ? rng.Range(10, top) : 0;
                double pick = rng.NextDouble();
                if (y == 0) continue;
                int col = lx + (lz << 4);
                if (blocks[col + (y << 8)] != B.Air) continue;
                while (y > 5 && blocks[col + ((y - 1) << 8)] == B.Air) y--;
                byte floor = blocks[col + ((y - 1) << 8)];
                if (!Solidish(floor) || floor == B.Water) continue;
                bool dark = c.WDk > 0.3f;
                if (pick > (dark ? 0.85 : 0.45)) continue;
                if (CaveAnchorTaken(ch, firstCave, bx + lx, y, bz + lz)) continue;
                ch.Props.Add(new PropPlacement { X = bx + lx, Y = y, Z = bz + lz, Kind = pick < 0.3 ? PropCatalog.GlowcapCluster : PropCatalog.MushroomCluster, Yaw = (float)(pick * 3600 % 360), Scale = 0.8f + (float)(pick * 0.5) });
                // scatter a few glowcap blocks around (light underground)
                for (int g = 0; g < 4; g++)
                {
                    int gx = lx + rng.Range(-2, 3), gz = lz + rng.Range(-2, 3);
                    if ((uint)gx > 15 || (uint)gz > 15 || (gx == lx && gz == lz)) continue;
                    int gc = gx + (gz << 4);
                    for (int gy = y + 2; gy >= y - 2 && gy > 5; gy--)
                    {
                        if (blocks[gc + (gy << 8)] == B.Air && Solidish(blocks[gc + ((gy - 1) << 8)]) && !CaveAnchorTaken(ch, firstCave, bx + gx, gy, bz + gz))
                        {
                            blocks[gc + (gy << 8)] = rng.NextDouble() < 0.7 ? B.GlowCap : B.MushroomBrown;
                            break;
                        }
                    }
                }
            }
        }

        static bool CaveAnchorTaken(Chunk ch, int first, int x, int y, int z)
        {
            for (int i = first; i < ch.Props.Count; i++)
            {
                var p = ch.Props[i];
                if (p.X == x && p.Z == z && Math.Abs(p.Y - y) <= 1) return true;
            }
            return false;
        }

        static bool Near(Scratch s, int lx, int lz)
        {
            for (int k = 0; k < 4; k++)
            {
                int nx = lx + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = lz + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx < 16 && (uint)nz < 16 && s.Reserved[nx + nz * 16]) return true;
            }
            return false;
        }

        static bool FootprintClear(Chunk ch, Scratch s, int lx, int ay, int lz, int rad, int hgt)
        {
            var blocks = ch.Blocks;
            for (int dz = -rad; dz <= rad; dz++)
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int px = lx + dx, pz = lz + dz;
                    if (s.Reserved[px + pz * 16]) return false;
                    int col = px + (pz << 4);
                    // footprint ground may step down by one block at the rim, never up
                    byte below = blocks[col + ((ay - 1) << 8)];
                    if (!Solidish(below))
                    {
                        if (dx == 0 && dz == 0) return false;
                        if (below != B.Air || !Solidish(blocks[col + ((ay - 2) << 8)])) return false;
                    }
                    for (int y = ay; y < ay + hgt; y++)
                    {
                        byte b = blocks[col + (y << 8)];
                        if (b != B.Air) return false;
                    }
                }
            return true;
        }

        static float PropScale(int kind, ulong h)
        {
            double r = ((h >> 52) & 0xFFF) / 4096.0;
            switch (kind)
            {
                case PropCatalog.BoulderA: case PropCatalog.BoulderB: case PropCatalog.BoulderC: return (float)(0.75 + 0.6 * r);
                case PropCatalog.GiantMushroom: return (float)(0.9 + 0.4 * r);
                case PropCatalog.IceSpike: return (float)(0.8 + 0.6 * r);
                case PropCatalog.SandstonePillar: return (float)(0.8 + 0.45 * r);
                case PropCatalog.FallenLogOak: case PropCatalog.FallenLogBirch: return (float)(0.85 + 0.3 * r);
                case PropCatalog.LilyPad: return (float)(0.7 + 0.4 * r);
                default: return (float)(0.8 + 0.4 * r);
            }
        }

        // ------------------------------------------------------------------ ground cover

        void PlacePlants(Chunk ch, Scratch s, int bx, int bz)
        {
            var blocks = ch.Blocks;
            for (int lz = 0; lz < 16; lz++)
                for (int lx = 0; lx < 16; lx++)
                {
                    int i = lx + lz * 16;
                    if (s.Reserved[i]) continue;
                    int x = bx + lx, z = bz + lz;
                    Col c = GetCol(s, x, z);
                    int y = c.H;
                    if (y >= Chunk.SY - 2 || c.Water > c.H) continue;
                    int col = lx + (lz << 4), idx = col + (y << 8);
                    byte g = blocks[idx];
                    if (blocks[idx + 256] != B.Air) continue;
                    ulong h = SplitMix.Hash(Seed, x, z, 31);
                    double r = (h & 0xFFFFF) / 1048576.0;
                    var clim = (Biome)c.Climate;
                    byte plant = 0;
                    switch (g)
                    {
                        case B.Grass:
                            switch (clim)
                            {
                                case Biome.Plains:
                                {
                                    double meadow = SS(0.0, 0.45, nFlower.Fbm(x / 55.0, z / 55.0, 2) / Sig2 * 0.3);
                                    double pf = 0.012 + 0.30 * meadow;
                                    if (r < pf) plant = Flower(x, z, h);
                                    else if (r < pf + 0.46 - 0.2 * meadow) plant = B.TallGrass;
                                    else if (r < pf + 0.47 - 0.2 * meadow) plant = B.Fern;
                                    break;
                                }
                                case Biome.TemperateForest:
                                {
                                    bool shade = Canopy(blocks, col, y);
                                    double pm = shade ? 0.035 : 0.006;
                                    double meadow = SS(0.15, 0.5, nFlower.Fbm(x / 55.0, z / 55.0, 2) / Sig2 * 0.3);
                                    double pf = 0.012 + 0.08 * meadow;
                                    if (r < pm) plant = ((h >> 24) & 1) == 0 ? B.MushroomRed : B.MushroomBrown;
                                    else if (r < pm + pf) plant = Flower(x, z, h);
                                    else if (r < pm + pf + 0.13) plant = B.Fern;
                                    else if (r < pm + pf + 0.43) plant = B.TallGrass;
                                    break;
                                }
                                case Biome.DarkForest:
                                    if (r < 0.04) plant = ((h >> 24) & 1) == 0 ? B.MushroomRed : B.MushroomBrown;
                                    else if (r < 0.052) plant = B.GlowCap;
                                    else if (r < 0.22) plant = B.Fern;
                                    else if (r < 0.34) plant = B.TallGrass;
                                    break;
                                case Biome.SnowyMountains:
                                    if (r < 0.014) plant = ((h >> 24) & 1) == 0 ? B.FlowerBlue : B.FlowerWhite;
                                    else if (r < 0.15) plant = B.Fern;
                                    else if (r < 0.32) plant = B.TallGrass;
                                    break;
                                default:
                                    if (r < 0.03) plant = B.DeadBush;
                                    else if (r < 0.2) plant = B.TallGrass;
                                    break;
                            }
                            break;
                        case B.Podzol:
                            if (r < 0.045) plant = ((h >> 24) & 1) == 0 ? B.MushroomRed : B.MushroomBrown;
                            else if (r < 0.06) plant = B.GlowCap;
                            else if (r < 0.22) plant = B.Fern;
                            else if (r < 0.30) plant = B.TallGrass;
                            break;
                        case B.SnowyGrass:
                            if (r < 0.025) plant = B.Fern;
                            break;
                        case B.Sand:
                            if (clim == Biome.Desert && (c.Flags & FBeach) == 0 && r < 0.014) plant = B.DeadBush;
                            break;
                        case B.Dirt:
                            if (r < 0.08) plant = B.TallGrass;
                            break;
                    }
                    if (plant != 0) blocks[idx + 256] = plant;
                }
        }

        static bool Canopy(byte[] blocks, int col, int y)
        {
            for (int k = 2; k <= 10 && y + k < Chunk.SY; k++)
            {
                byte b = blocks[col + ((y + k) << 8)];
                if (b >= B.LeavesOak && b <= B.LeavesDark) return true;
            }
            return false;
        }

        byte Flower(int x, int z, ulong h)
        {
            // Patches of one colour with a sprinkle of others.
            double n = nFlowerColor.Sample(x / 38.0, z / 38.0) / SigPerlin * 0.4;
            int k = n < -0.3 ? 0 : n < 0.0 ? 1 : n < 0.3 ? 2 : 3;
            if (((h >> 32) & 0xFF) < 46) k = (int)((h >> 40) & 3);
            return (byte)(B.FlowerRed + k);
        }
    }
}
