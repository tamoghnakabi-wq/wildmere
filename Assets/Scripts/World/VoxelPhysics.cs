using UnityEngine;

namespace Wildmere.World
{
    public struct VoxelHit
    {
        public bool Hit;
        public Vector3Int Block;     // the block that was hit
        public Vector3Int Normal;    // face normal (points out of the hit block)
        public Vector3 Point;
        public float Distance;
        public byte Id;
        public Vector3Int Adjacent => Block + Normal;
    }

    /// <summary>
    /// Voxel-native collision and ray queries. Entities are axis-aligned boxes moved with swept,
    /// axis-separated resolution against solid blocks — no PhysX colliders are needed for terrain.
    /// </summary>
    public static class VoxelPhysics
    {
        const float kSkin = 0.001f;

        /// <summary>Is the block at (x,y,z) solid for entities?</summary>
        public static bool Solid(VoxelWorld w, int x, int y, int z) => Blocks.IsSolid[w.GetBlock(x, y, z)];

        /// <summary>Any solid block intersecting the box (min..max)?</summary>
        public static bool Overlaps(VoxelWorld w, Vector3 min, Vector3 max)
        {
            int x0 = Mathf.FloorToInt(min.x + kSkin), x1 = Mathf.FloorToInt(max.x - kSkin);
            int y0 = Mathf.FloorToInt(min.y + kSkin), y1 = Mathf.FloorToInt(max.y - kSkin);
            int z0 = Mathf.FloorToInt(min.z + kSkin), z1 = Mathf.FloorToInt(max.z - kSkin);
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                        if (Solid(w, x, y, z)) return true;
            return false;
        }

        /// <summary>
        /// Move a box (feet-centred: position is the bottom centre, size = (width, height)) by delta,
        /// resolving Y first, then X and Z. Returns which axes were blocked.
        /// </summary>
        public static Vector3Int Move(VoxelWorld w, ref Vector3 pos, Vector3 delta, float halfWidth, float height)
        {
            var blocked = Vector3Int.zero;
            // Split big moves so we never tunnel through a block.
            int steps = Mathf.Max(1, Mathf.CeilToInt(delta.magnitude / 0.45f));
            Vector3 step = delta / steps;
            for (int i = 0; i < steps; i++)
            {
                if (MoveAxis(w, ref pos, step.y, 1, halfWidth, height)) { blocked.y = step.y < 0 ? -1 : 1; step.y = 0; }
                if (MoveAxis(w, ref pos, step.x, 0, halfWidth, height)) { blocked.x = 1; step.x = 0; }
                if (MoveAxis(w, ref pos, step.z, 2, halfWidth, height)) { blocked.z = 1; step.z = 0; }
            }
            return blocked;
        }

        static bool MoveAxis(VoxelWorld w, ref Vector3 pos, float d, int axis, float hw, float h)
        {
            if (d == 0f) return false;
            Vector3 np = pos;
            np[axis] += d;
            var min = new Vector3(np.x - hw, np.y, np.z - hw);
            var max = new Vector3(np.x + hw, np.y + h, np.z + hw);
            if (!Overlaps(w, min, max)) { pos = np; return false; }

            // Snap flush against the obstacle.
            if (axis == 1)
            {
                if (d < 0) pos.y = Mathf.Floor(np.y) + 1f + kSkin;
                else pos.y = Mathf.Ceil(np.y + h) - 1f - h - kSkin;
                // Make sure the snap itself is clear (it is unless we started inside something).
                if (Overlaps(w, new Vector3(pos.x - hw, pos.y, pos.z - hw), new Vector3(pos.x + hw, pos.y + h, pos.z + hw)))
                    pos.y = np.y - d;
            }
            else
            {
                float c = pos[axis];
                if (d < 0) c = Mathf.Floor(np[axis] - hw) + 1f + hw + kSkin;
                else c = Mathf.Ceil(np[axis] + hw) - 1f - hw - kSkin;
                Vector3 sp = pos; sp[axis] = c;
                if (!Overlaps(w, new Vector3(sp.x - hw, sp.y, sp.z - hw), new Vector3(sp.x + hw, sp.y + h, sp.z + hw)))
                    pos = sp;
            }
            return true;
        }

        /// <summary>True if the box is standing on something solid.</summary>
        public static bool Grounded(VoxelWorld w, Vector3 pos, float hw)
        {
            var min = new Vector3(pos.x - hw, pos.y - 0.05f, pos.z - hw);
            var max = new Vector3(pos.x + hw, pos.y - kSkin * 2f, pos.z + hw);
            return Overlaps(w, min, max);
        }

        /// <summary>Fraction of the box submerged in water (sampled at feet, waist, eyes).</summary>
        public static float Submersion(VoxelWorld w, Vector3 pos, float h)
        {
            int x = Mathf.FloorToInt(pos.x), z = Mathf.FloorToInt(pos.z);
            float s = 0;
            if (w.GetBlock(x, Mathf.FloorToInt(pos.y + 0.1f), z) == B.Water) s += 0.34f;
            if (w.GetBlock(x, Mathf.FloorToInt(pos.y + h * 0.5f), z) == B.Water) s += 0.33f;
            if (w.GetBlock(x, Mathf.FloorToInt(pos.y + h * 0.95f), z) == B.Water) s += 0.33f;
            return s;
        }

        /// <summary>
        /// Amanatides–Woo voxel traversal. Hits any block that is not air (and, unless includeWater,
        /// not water). Plants count as hits so they can be broken.
        /// </summary>
        public static VoxelHit Raycast(VoxelWorld w, Vector3 origin, Vector3 dir, float maxDist, bool includeWater = false)
        {
            var hit = new VoxelHit();
            dir.Normalize();
            int x = Mathf.FloorToInt(origin.x), y = Mathf.FloorToInt(origin.y), z = Mathf.FloorToInt(origin.z);
            int sx = dir.x > 0 ? 1 : -1, sy = dir.y > 0 ? 1 : -1, sz = dir.z > 0 ? 1 : -1;
            float tdx = dir.x != 0 ? Mathf.Abs(1f / dir.x) : float.PositiveInfinity;
            float tdy = dir.y != 0 ? Mathf.Abs(1f / dir.y) : float.PositiveInfinity;
            float tdz = dir.z != 0 ? Mathf.Abs(1f / dir.z) : float.PositiveInfinity;
            float tmx = dir.x != 0 ? ((sx > 0 ? x + 1 - origin.x : origin.x - x) * tdx) : float.PositiveInfinity;
            float tmy = dir.y != 0 ? ((sy > 0 ? y + 1 - origin.y : origin.y - y) * tdy) : float.PositiveInfinity;
            float tmz = dir.z != 0 ? ((sz > 0 ? z + 1 - origin.z : origin.z - z) * tdz) : float.PositiveInfinity;
            var n = Vector3Int.zero;
            float t = 0;
            for (int i = 0; i < 512 && t <= maxDist; i++)
            {
                byte b = w.GetBlock(x, y, z);
                if (b != B.Air && (includeWater || b != B.Water))
                {
                    hit.Hit = true; hit.Block = new Vector3Int(x, y, z); hit.Normal = n; hit.Distance = t;
                    hit.Point = origin + dir * t; hit.Id = b;
                    return hit;
                }
                if (tmx < tmy && tmx < tmz) { x += sx; t = tmx; tmx += tdx; n = new Vector3Int(-sx, 0, 0); }
                else if (tmy < tmz) { y += sy; t = tmy; tmy += tdy; n = new Vector3Int(0, -sy, 0); }
                else { z += sz; t = tmz; tmz += tdz; n = new Vector3Int(0, 0, -sz); }
            }
            return hit;
        }
    }
}
