using System.Collections.Generic;
using UnityEngine;
using Wildmere.Core;
using Wildmere.Player;
using Wildmere.World;

namespace Wildmere.Wildlife
{
    /// <summary>
    /// Spawns and simulates wildlife around the player: biome-appropriate species in a ring
    /// 24–70 m away, capped counts, despawn when far, full-rate AI near the player and a cheap
    /// low-rate tick (and LOD1 meshes) further out. Registers animals as obstacles for block placement.
    /// </summary>
    public sealed class WildlifeManager : MonoBehaviour
    {
        public static WildlifeManager Instance { get; private set; }
        public static bool Enabled = true;

        public int MaxDeer = 5, MaxRabbits = 8, MaxBirds = 10;
        public float SpawnMin = 24f, SpawnMax = 70f, Despawn = 95f, NearRadius = 48f;

        readonly List<Animal> mAnimals = new List<Animal>();
        readonly Dictionary<Species, GameObject> mModels = new Dictionary<Species, GameObject>();
        float mSpawnTimer;
        float mFarTimer;
        int mSeedCounter;
        System.Random mRng;

        public int Count => mAnimals.Count;
        public IReadOnlyList<Animal> Animals => mAnimals;

        void Awake()
        {
            Instance = this;
            mRng = new System.Random(12345);
            BlockInteraction.EntityBlocks = Occupies;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (BlockInteraction.EntityBlocks == Occupies) BlockInteraction.EntityBlocks = null;
            Clear();
        }

        public void Clear()
        {
            foreach (var a in mAnimals) if (a != null) Destroy(a.gameObject);
            mAnimals.Clear();
        }

        bool Occupies(Vector3 min, Vector3 max)
        {
            var box = new Bounds((min + max) * 0.5f, max - min);
            foreach (var a in mAnimals) if (a != null && a.Box.Intersects(box)) return true;
            return false;
        }

        GameObject Model(Species s)
        {
            if (mModels.TryGetValue(s, out var m)) return m;
            string name = s == Species.Songbird ? (mRng.NextDouble() < 0.5 ? "Songbird_Blue" : "Songbird_Red") : s.ToString();
            m = Resources.Load<GameObject>("Models/Wildlife/" + name);
            if (s != Species.Songbird) mModels[s] = m;
            return m;
        }

        void Update()
        {
            var root = GameRoot.Instance;
            var p = root != null ? root.Player : null;
            var w = VoxelWorld.Instance;
            if (!Enabled || p == null || w == null || root.State != GameState.Playing) return;
            float dt = Time.deltaTime;
            var ppos = p.transform.position;
            bool sneak = p.Crouching;

            // Simulate: near animals every frame, far ones at ~5 Hz with the accumulated delta.
            mFarTimer += dt;
            bool farTick = mFarTimer >= 0.2f;
            for (int i = mAnimals.Count - 1; i >= 0; i--)
            {
                var a = mAnimals[i];
                if (a == null) { mAnimals.RemoveAt(i); continue; }
                float d = Vector3.Distance(a.transform.position, ppos);
                if (d > Despawn || !w.IsLoaded(Mathf.FloorToInt(a.transform.position.x), Mathf.FloorToInt(a.transform.position.z)))
                {
                    Destroy(a.gameObject); mAnimals.RemoveAt(i); continue;
                }
                a.Far = d > NearRadius;
                a.UpdateLod(d);
                if (!a.Far) a.Tick(dt, ppos, sneak);
                else if (farTick) a.Tick(mFarTimer, ppos, sneak);
            }
            if (farTick) mFarTimer = 0;

            // Spawn a little at a time.
            mSpawnTimer -= dt;
            if (mSpawnTimer <= 0)
            {
                mSpawnTimer = 1.2f;
                TrySpawn(w, ppos);
            }
        }

        int CountOf(Species s)
        {
            int n = 0;
            foreach (var a in mAnimals) if (a != null && (a.Kind == s || (s == Species.Deer && a.Kind == Species.Doe))) n++;
            return n;
        }

        void TrySpawn(VoxelWorld w, Vector3 ppos)
        {
            float ang = (float)mRng.NextDouble() * Mathf.PI * 2f;
            float dist = SpawnMin + (float)mRng.NextDouble() * (SpawnMax - SpawnMin);
            int x = Mathf.FloorToInt(ppos.x + Mathf.Cos(ang) * dist), z = Mathf.FloorToInt(ppos.z + Mathf.Sin(ang) * dist);
            if (!w.IsMeshed(x, z)) return;
            int y = w.SurfaceY(x, z);
            if (y < 1) return;
            byte ground = w.GetBlock(x, y, z);
            if (ground == B.Water || World.Blocks.IsLeaves(ground)) return;
            var biome = w.Gen.SampleBiome(x, z);
            bool night = Rendering.Atmosphere.Instance != null && Mathf.Abs(Rendering.Atmosphere.Instance.TimeOfDay - 0.5f) > 0.27f;

            Species? pick = null;
            double r = mRng.NextDouble();
            bool grassy = ground == B.Grass || ground == B.Podzol || ground == B.SnowyGrass || ground == B.Snow || ground == B.Dirt;
            switch (biome.Biome)
            {
                case Biome.TemperateForest:
                case Biome.Plains:
                    if (r < 0.35 && grassy) pick = Species.Deer;
                    else if (r < 0.75 && grassy) pick = Species.Rabbit;
                    else if (!night) pick = Species.Songbird;
                    break;
                case Biome.DarkForest:
                    if (r < 0.2 && grassy) pick = Species.Deer; else if (r < 0.45 && grassy) pick = Species.Rabbit; else if (!night && r < 0.7) pick = Species.Songbird;
                    break;
                case Biome.SnowyMountains: case Biome.SnowyPlains: case Biome.Mountains:
                    if (r < 0.5 && grassy) pick = Species.Rabbit; else if (r < 0.65 && grassy && y < 120) pick = Species.Deer;
                    break;
                case Biome.Desert:
                    if (r < 0.25 && ground == B.Sand) pick = Species.Rabbit;
                    break;
                case Biome.Beach: case Biome.River:
                    if (!night && r < 0.4) pick = Species.Songbird;
                    break;
            }
            if (pick == null) return;
            var s = pick.Value;
            if (s == Species.Deer && CountOf(Species.Deer) >= MaxDeer) return;
            if (s == Species.Rabbit && CountOf(Species.Rabbit) >= MaxRabbits) return;
            if (s == Species.Songbird && CountOf(Species.Songbird) >= MaxBirds) return;
            if (s == Species.Deer && mRng.NextDouble() < 0.55) s = Species.Doe;

            int group = s == Species.Songbird ? 1 + mRng.Next(3) : s == Species.Rabbit ? 1 + mRng.Next(2) : 1 + mRng.Next(3);
            for (int g = 0; g < group; g++)
            {
                int gx = x + mRng.Next(-3, 4), gz = z + mRng.Next(-3, 4);
                int gy = w.SurfaceY(gx, gz);
                if (gy < 1 || w.GetBlock(gx, gy, gz) == B.Water) continue;
                var model = Model(s == Species.Doe ? Species.Doe : s);
                if (model == null) { Debug.LogWarning($"WM_WILDLIFE missing model for {s}"); return; }
                var go = new GameObject(s.ToString());
                go.transform.position = new Vector3(gx + 0.5f, gy + 1f, gz + 0.5f);
                var a = go.AddComponent<Animal>();
                a.Init(s, model, ++mSeedCounter * 7919 + gx * 31 + gz);
                mAnimals.Add(a);
                if (s == Species.Deer || s == Species.Doe) s = mRng.NextDouble() < 0.5 ? Species.Deer : Species.Doe; // mixed herds
            }
        }

        /// <summary>Spawn one animal at a position (harness/debug).</summary>
        public Animal SpawnAt(Species s, Vector3 pos)
        {
            var model = Model(s);
            if (model == null) return null;
            var go = new GameObject(s.ToString());
            go.transform.position = pos;
            var a = go.AddComponent<Animal>();
            a.Init(s, model, ++mSeedCounter * 7919);
            mAnimals.Add(a);
            return a;
        }
    }
}
