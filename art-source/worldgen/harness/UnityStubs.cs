// Minimal stand-ins for the few UnityEngine / Wildmere types the generator sources touch, so
// Assets/Scripts/World/Gen/*.cs + Chunk.cs + Blocks.cs compile and run under plain Mono for fast
// iteration (maps, stats, timing) without the Unity editor. Never shipped.
using System;

namespace UnityEngine
{
    public struct Vector3Int
    {
        public int x, y, z;
        public Vector3Int(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() => $"({x}, {y}, {z})";
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public static class Mathf
    {
        public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        public static int FloorToInt(float v) => (int)Math.Floor(v);
    }
}

namespace Wildmere.Items
{
    public static class ItemIds
    {
        public const int Stick = 256, Coal = 257, RawIron = 258, IronIngot = 259, RawGold = 260, GoldIngot = 261, Flint = 262;
    }
}

namespace Wildmere.World
{
    public sealed class ChunkView { }
}
