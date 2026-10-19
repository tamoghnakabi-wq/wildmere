using UnityEngine;
using Wildmere.Items;
using Wildmere.World;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Light from a torch (or any light-emitting block) held in the hand. Placed torches light the world
    /// through the block light baked into chunk meshes; a held one moves every frame, so the voxel shaders add
    /// it instead (VoxelLighting.hlsl WMHandLight): the same warm colour and level falloff (emit - distance) as
    /// block light, limited to the air the hand can reach by a short flood fill, so it does not shine through
    /// walls or floors. The flood fill is stored in a small 3D mask around the hand and recomputed when the hand
    /// enters another block, a block nearby changes, or once a second (chunks streaming in).
    /// </summary>
    public sealed class HandLight : MonoBehaviour
    {
        public Player.PlayerController Player;
        public static HandLight Instance { get; private set; }

        const int N = 32;          // mask volume edge in blocks, centred on the hand
        const int Budget = 24;     // flood steps (6-neighbour): reaches the light's ~13 m radius diagonally too
        const int Fade = 4;        // the last steps fade out so the reach limit has no hard edge
        static readonly Vector3 kHandOffset = new Vector3(0.3f, -0.12f, 0.55f);   // the held item's flame, camera space

        static readonly int kHand = Shader.PropertyToID("_WM_HandLight"),
                            kVol = Shader.PropertyToID("_WM_HandLightVol"),
                            kMask = Shader.PropertyToID("_WM_HandLightMask");
        static Texture3D sEmpty;

        Texture3D mTex;
        readonly bool[] mSolid = new bool[N * N * N];
        readonly byte[] mSteps = new byte[N * N * N];
        readonly byte[] mMask = new byte[N * N * N];
        readonly int[] mQueue = new int[N * N * N];
        Vector3Int mSeed = new Vector3Int(int.MinValue, 0, 0), mOrigin;
        VoxelWorld mWorld;
        bool mDirty = true;
        float mLevel, mRefreshAt;

        /// <summary>Bind an empty mask and switch the light off (shaders always declare the texture).</summary>
        public static void EnsureDefault()
        {
            if (sEmpty == null)
            {
                sEmpty = new Texture3D(1, 1, 1, TextureFormat.R8, false) { name = "HandLightEmpty" };
                sEmpty.SetPixelData(new byte[] { 0 }, 0);
                sEmpty.Apply(false, true);
            }
            Shader.SetGlobalTexture(kMask, sEmpty);
            Shader.SetGlobalVector(kHand, Vector4.zero);
            Shader.SetGlobalVector(kVol, new Vector4(0, 0, 0, 1));
        }

        /// <summary>Reach mask (0..255) at a world block, or -1 outside the volume (harness checks).</summary>
        public int ReachAt(Vector3Int p)
        {
            var l = p - mOrigin;
            if (l.x < 0 || l.y < 0 || l.z < 0 || l.x >= N || l.y >= N || l.z >= N) return -1;
            return mMask[l.x + (l.y + l.z * N) * N];
        }

        void OnEnable()
        {
            Instance = this;
            mTex = new Texture3D(N, N, N, TextureFormat.R8, false)
            {
                name = "HandLightMask", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            if (mWorld != null) mWorld.BlockChanged -= OnBlockChanged;
            mWorld = null;
            EnsureDefault();
            if (mTex != null) Destroy(mTex);
            mTex = null;
        }

        void OnBlockChanged(int x, int y, int z, byte was, byte now)
        {
            if (x >= mOrigin.x && y >= mOrigin.y && z >= mOrigin.z && x < mOrigin.x + N && y < mOrigin.y + N && z < mOrigin.z + N)
                mDirty = true;
        }

        /// <summary>Light level (0..15) of the selected item, 0 for anything that does not glow.</summary>
        int HeldEmit()
        {
            if (Player == null || Player.Dead) return 0;
            var s = Player.Inventory.SelectedStack;
            if (s.IsEmpty) return 0;
            byte b = ItemDb.Get(s.Id).PlacesBlock;
            return b == 0 ? 0 : Blocks.Emit[b];
        }

        void LateUpdate()
        {
            var w = VoxelWorld.Instance;
            if (w != mWorld)
            {
                if (mWorld != null) mWorld.BlockChanged -= OnBlockChanged;
                mWorld = w;
                if (mWorld != null) mWorld.BlockChanged += OnBlockChanged;
                mDirty = true;
            }
            int emit = mWorld != null && Player != null && Player.Cam != null ? HeldEmit() : 0;
            mLevel = Mathf.MoveTowards(mLevel, emit, Time.deltaTime * 60f);   // ~0.25 s to light up or go out
            if (mLevel <= 0.01f) { Shader.SetGlobalVector(kHand, Vector4.zero); return; }

            var cam = Player.Cam.transform;
            Vector3 hand = cam.TransformPoint(kHandOffset);
            var seed = Vector3Int.FloorToInt(hand);
            // Pressed against a wall the flame can poke into a block: light from the eye's cell instead.
            if (Blocks.IsOpaque[mWorld.GetBlock(seed.x, seed.y, seed.z)]) seed = Vector3Int.FloorToInt(cam.position);
            if (seed != mSeed || mDirty || Time.unscaledTime >= mRefreshAt) Rebuild(seed);

            Shader.SetGlobalVector(kHand, new Vector4(hand.x, hand.y, hand.z, mLevel));
            Shader.SetGlobalVector(kVol, new Vector4(mOrigin.x, mOrigin.y, mOrigin.z, N));
            Shader.SetGlobalTexture(kMask, mTex);
        }

        /// <summary>Milliseconds the last mask rebuild took (harness reporting).</summary>
        public static double LastRebuildMs;

        void Rebuild(Vector3Int seed)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            mSeed = seed;
            mDirty = false;
            mRefreshAt = Time.unscaledTime + 1f;
            mOrigin = seed - new Vector3Int(N / 2, N / 2, N / 2);

            // Solidity, one chunk lookup per column.
            var opaque = Blocks.IsOpaque;
            for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    int wx = mOrigin.x + x, wz = mOrigin.z + z;
                    var c = mWorld.ChunkAt(wx, wz);
                    bool loaded = c != null && c.State == ChunkState.Generated;
                    int col = (wx & 15) + ((wz & 15) << 4);
                    for (int y = 0; y < N; y++)
                    {
                        int wy = mOrigin.y + y, i = x + (y + z * N) * N;   // Texture3D order: x, then y, then z
                        if (wy < 0) mSolid[i] = true;
                        else if (wy >= Chunk.SY) mSolid[i] = false;
                        else mSolid[i] = loaded ? opaque[c.Blocks[col + (wy << 8)]] : wy <= 62;
                    }
                }

            // Flood fill through non-opaque cells from the hand.
            System.Array.Fill(mSteps, (byte)255);
            System.Array.Clear(mMask, 0, mMask.Length);
            const int SY = N, SZ = N * N;
            int s0 = N / 2 + N / 2 * SY + N / 2 * SZ;
            int head = 0, tail = 0;
            mSteps[s0] = 0;
            mQueue[tail++] = s0;
            while (head < tail)
            {
                int i = mQueue[head++];
                int d = mSteps[i];
                mMask[i] = (byte)(d <= Budget - Fade ? 255 : (Budget - d) * 255 / Fade);
                if (d >= Budget) continue;
                int x = i % N, y = i / SY % N, z = i / SZ;
                byte nd = (byte)(d + 1);
                if (x > 0 && !mSolid[i - 1] && mSteps[i - 1] == 255) { mSteps[i - 1] = nd; mQueue[tail++] = i - 1; }
                if (x < N - 1 && !mSolid[i + 1] && mSteps[i + 1] == 255) { mSteps[i + 1] = nd; mQueue[tail++] = i + 1; }
                if (y > 0 && !mSolid[i - SY] && mSteps[i - SY] == 255) { mSteps[i - SY] = nd; mQueue[tail++] = i - SY; }
                if (y < N - 1 && !mSolid[i + SY] && mSteps[i + SY] == 255) { mSteps[i + SY] = nd; mQueue[tail++] = i + SY; }
                if (z > 0 && !mSolid[i - SZ] && mSteps[i - SZ] == 255) { mSteps[i - SZ] = nd; mQueue[tail++] = i - SZ; }
                if (z < N - 1 && !mSolid[i + SZ] && mSteps[i + SZ] == 255) { mSteps[i + SZ] = nd; mQueue[tail++] = i + SZ; }
            }
            mTex.SetPixelData(mMask, 0);
            mTex.Apply(false);
            LastRebuildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }
}
