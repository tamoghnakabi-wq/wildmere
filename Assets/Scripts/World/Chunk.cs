using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Wildmere.World
{
    public enum ChunkState { Queued, Generating, Generated }

    /// <summary>
    /// One 16 x 256 x 16 column of voxels. Block data is written by the generator thread before the
    /// column is published (State = Generated) and afterwards only by the main thread (edits).
    /// Mesher threads read it; a racing edit is harmless because every edit bumps <see cref="Version"/>
    /// and re-queues a mesh, and stale mesh results are discarded on apply.
    /// </summary>
    public sealed class Chunk
    {
        public const int SX = 16, SY = 256, SZ = 16, Sections = SY / 16;
        public const int Volume = SX * SY * SZ;

        public readonly int CX, CZ;
        public readonly byte[] Blocks = new byte[Volume];
        /// <summary>Per column (x + z*16): biome id and tints from the generator.</summary>
        public readonly byte[] Biome = new byte[256];
        public readonly Color32[] GrassTint = new Color32[256];
        public readonly Color32[] FoliageTint = new Color32[256];
        /// <summary>Highest y whose block blocks sky light, per column; -1 if none.</summary>
        public readonly short[] SkyTop = new short[256];
        /// <summary>Highest non-air y in the whole column (mesh bounds).</summary>
        public int MaxY;

        /// <summary>Sparse per-block metadata (e.g. furnace facing 0..3). Key = block index.</summary>
        public readonly Dictionary<int, byte> Meta = new Dictionary<int, byte>();
        /// <summary>Decorative props from the generator (Blender models).</summary>
        public readonly List<PropPlacement> Props = new List<PropPlacement>();

        public volatile ChunkState State;
        public int Version;              // bumped on every edit (main thread)
        public int MeshedVersion = -1;   // version + neighbour-sum the current mesh was built from
        public int MeshRequested = -1;
        public bool MeshQueued;
        public bool Dirty;               // has unsaved player modifications
        public float LastSeen;

        public ChunkView View;           // render objects (main thread)

        public Chunk(int cx, int cz) { CX = cx; CZ = cz; }

        public static int Index(int x, int y, int z) => x + (z << 4) + (y << 8);

        public byte Get(int x, int y, int z) => (uint)y < SY ? Blocks[x + (z << 4) + (y << 8)] : (byte)0;

        public void RecomputeColumn(int x, int z)
        {
            int top = -1;
            for (int y = SY - 1; y >= 0; y--)
            {
                byte b = Blocks[x + (z << 4) + (y << 8)];
                if (World.Blocks.IsOpaque[b] || World.Blocks.Absorb[b] > 0) { top = y; break; }
            }
            SkyTop[x + (z << 4)] = (short)top;
        }

        public void RecomputeHeights()
        {
            int maxY = 0;
            for (int z = 0; z < SZ; z++)
                for (int x = 0; x < SX; x++)
                {
                    RecomputeColumn(x, z);
                    for (int y = SY - 1; y > maxY; y--)
                        if (Blocks[x + (z << 4) + (y << 8)] != 0) { maxY = y; break; }
                }
            MaxY = maxY;
        }

        public static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
    }

    /// <summary>A decoration model instance anchored to a block cell.</summary>
    public struct PropPlacement
    {
        public int X, Y, Z;       // world block coords of the anchor cell (model sits on the block below)
        public byte Kind;         // index into PropCatalog
        public float Yaw;         // degrees
        public float Scale;
    }
}
