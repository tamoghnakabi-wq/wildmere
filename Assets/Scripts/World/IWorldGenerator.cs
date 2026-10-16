using UnityEngine;

namespace Wildmere.World
{
    /// <summary>Biome ids (stored per column in chunks; part of the save-free generated state).</summary>
    public enum Biome : byte
    {
        Ocean = 0, Beach = 1, Plains = 2, TemperateForest = 3, DarkForest = 4, Desert = 5,
        SnowyMountains = 6, River = 7, SnowyPlains = 8, Mountains = 9,
    }

    /// <summary>What other systems (weather, wildlife, ambience, atmosphere) want to know about a spot.</summary>
    public struct BiomeSample
    {
        public Biome Biome;          // dominant biome
        public float Temperature;    // 0 cold .. 1 hot
        public float Humidity;       // 0 dry .. 1 wet
        public float Forest;         // 0..1 tree density
        public float Darkness;       // 0..1 dark-forest-ness (fog/ambience)
        public bool Snowy;           // precipitation falls as snow here
        public int Height;           // approximate surface height
    }

    /// <summary>
    /// World generator contract. Implementations must be deterministic for (seed, coordinates) and
    /// thread-safe: Generate is called concurrently from worker threads for different columns.
    /// </summary>
    public interface IWorldGenerator
    {
        long Seed { get; }

        /// <summary>
        /// Fill <paramref name="c"/>.Blocks (index x + z*16 + y*256) plus per-column Biome, GrassTint and
        /// FoliageTint, and append decoration props to c.Props. Must not touch any other chunk.
        /// Features that cross column borders (trees) must be generated identically from every column
        /// they touch (decide them from world coordinates, not from generation order).
        /// </summary>
        void Generate(Chunk c);

        BiomeSample SampleBiome(int x, int z);

        /// <summary>A good spawn column near the origin (on dry land, not in a tree). Returns block coords of the feet.</summary>
        Vector3Int FindSpawn();
    }
}
