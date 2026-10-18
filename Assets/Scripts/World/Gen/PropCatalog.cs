namespace Wildmere.World.Gen
{
    /// <summary>Decoration model names; PropPlacement.Kind indexes this array (models in Resources/Models/Props).</summary>
    public static class PropCatalog
    {
        public static readonly string[] Names =
        {
            "Boulder_A", "Boulder_B", "Boulder_C", "Rocks_Small_A", "Rocks_Small_B", "FallenLog_Oak", "FallenLog_Birch",
            "Stump", "MushroomCluster", "GiantMushroom", "GlowcapCluster", "SandstonePillar", "DeadTree", "IceSpike",
            "Bush_A", "Bush_B", "Reeds", "LilyPad",
        };

        // Kind ids (indices into Names).
        public const byte BoulderA = 0, BoulderB = 1, BoulderC = 2, RocksSmallA = 3, RocksSmallB = 4, FallenLogOak = 5,
            FallenLogBirch = 6, Stump = 7, MushroomCluster = 8, GiantMushroom = 9, GlowcapCluster = 10, SandstonePillar = 11,
            DeadTree = 12, IceSpike = 13, BushA = 14, BushB = 15, Reeds = 16, LilyPad = 17;

        /// <summary>Generator footprint: half size in blocks around the anchor that must be clear ground (0 = anchor only).</summary>
        public static readonly byte[] Radius = { 1, 2, 1, 0, 0, 2, 1, 1, 0, 1, 0, 1, 1, 1, 0, 1, 0, 0 };

        /// <summary>Air cells needed above the ground, starting at the anchor.</summary>
        public static readonly byte[] Height = { 1, 2, 2, 1, 1, 1, 1, 1, 1, 4, 1, 5, 3, 5, 1, 1, 2, 1 };
    }
}
