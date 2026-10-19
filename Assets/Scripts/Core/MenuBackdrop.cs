using UnityEngine;
using Wildmere.World;
using Wildmere.World.Gen;

namespace Wildmere.Core
{
    /// <summary>
    /// The living main-menu background: a real generated world (fixed scenic seed, never saved) with a
    /// slow cinematic camera drift at golden hour. Created when the game enters the main menu and torn
    /// down before a playable world loads.
    /// </summary>
    public sealed class MenuBackdrop : MonoBehaviour
    {
        public static MenuBackdrop Instance { get; private set; }
        public const long Seed = 7_202_610_17;

        VoxelWorld mWorld;
        Camera mCam;
        Vector3 mCenter;
        float mT;
        float mHeight;

        public static void Show()
        {
            if (Instance != null || Harness.WantsAutoStart) return;
            Instance = new GameObject("MenuBackdrop").AddComponent<MenuBackdrop>();
        }

        /// <summary>Show even when a harness flag is present (tests that start from the menu).</summary>
        public static void ShowForced()
        {
            if (Instance != null) return;
            Instance = new GameObject("MenuBackdrop").AddComponent<MenuBackdrop>();
        }

        public static void Hide()
        {
            if (Instance == null) return;
            Destroy(Instance.gameObject);
            Instance = null;
        }

        void Start()
        {
            var gen = new TerrainGenerator(Seed);
            mWorld = VoxelWorld.Create(gen, null, Mathf.Clamp(SystemInfo.processorCount - 4, 2, 4));
            mWorld.transform.SetParent(transform, false);
            mWorld.ViewDistance = Mathf.Min(GameSettings.Current.ViewDistance, 8);
            var s = gen.FindSpawn();
            mCenter = new Vector3(s.x, s.y, s.z);
            mHeight = s.y + 24f;
            mWorld.Focus = mCenter;

            var go = new GameObject("BackdropCamera") { tag = "MainCamera" };
            go.transform.SetParent(transform, false);
            mCam = go.AddComponent<Camera>();
            mCam.fieldOfView = 62f;
            mCam.nearClipPlane = 0.1f;
            mCam.farClipPlane = 600f;
            GraphicsApplier.Apply();

            var atm = Rendering.Atmosphere.Instance;
            if (atm != null) { atm.TimeOfDay = 0.69f; atm.Paused = false; atm.ViewDistanceMeters = mWorld.ViewDistance * 16f; }
        }

        void Update()
        {
            if (mWorld == null) return;
            mT += Time.unscaledDeltaTime;
            // A wide, slow orbit with a gentle bob; the look target leads the motion slightly.
            float a = mT * 0.018f;
            var pos = mCenter + new Vector3(Mathf.Cos(a) * 70f, 0, Mathf.Sin(a) * 70f);
            int ground = mWorld.SurfaceY(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z));
            float target = Mathf.Max(mHeight, ground + 14f);
            pos.y = Mathf.Lerp(mCam.transform.position.y == 0 ? target : mCam.transform.position.y, target, Time.unscaledDeltaTime * 0.4f)
                    + Mathf.Sin(mT * 0.21f) * 0.02f;
            var look = mCenter + new Vector3(Mathf.Cos(a + 0.9f) * 30f, -6f, Mathf.Sin(a + 0.9f) * 30f);
            mCam.transform.SetPositionAndRotation(pos, Quaternion.Slerp(mCam.transform.rotation,
                Quaternion.LookRotation(look - pos), Time.unscaledDeltaTime * 2f));
            mWorld.Focus = pos;
            mWorld.FocusDir = mCam.transform.forward;

            var atm = Rendering.Atmosphere.Instance;
            if (atm != null) { atm.TimeOfDay = Mathf.Repeat(0.69f + mT * 0.0004f, 1f); atm.Underwater = false; }
        }

        void OnDestroy()
        {
            if (mWorld != null) Destroy(mWorld.gameObject);
            if (Instance == this) Instance = null;
        }
    }
}
