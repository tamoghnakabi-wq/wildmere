using System.Collections.Generic;
using UnityEngine;
using Wildmere.Core;
using Wildmere.World;

namespace Wildmere.Wildlife
{
    public enum Species { Deer, Doe, Rabbit, Songbird }

    /// <summary>
    /// One animal: voxel-aware wandering/grazing/fleeing (birds: perch, hop, take off, fly, land) and
    /// procedural animation of the Blender part hierarchy (legs, head, ears, tail, wings).
    /// Simulation detail drops with distance (see <see cref="WildlifeManager"/>).
    /// </summary>
    public sealed class Animal : MonoBehaviour
    {
        enum State { Idle, Graze, Walk, Flee, Look, Fly, Perch }

        public Species Kind;
        public float HalfWidth = 0.3f, Height = 1.2f;
        public bool Far;                 // low-detail simulation
        public Vector3 Velocity;

        State mState;
        float mStateTime, mStateDur;
        Vector3 mTarget;
        float mYaw, mYawVel;
        float mPhase;
        float mHop;
        float mHeadDown;
        float mNextSound;
        float mFlyHeight;
        System.Random mRng;

        // Rig
        Transform mBody, mNeck, mHead, mEarL, mEarR, mTail, mWingL, mWingR;
        readonly Transform[] mLegs = new Transform[4];
        Quaternion mNeckRest, mHeadRest, mEarLRest, mEarRRest, mTailRest, mWingLRest, mWingRRest;
        readonly Quaternion[] mLegRest = new Quaternion[4];
        Vector3 mBodyRest;
        readonly List<Renderer> mLod0 = new List<Renderer>(), mLod1 = new List<Renderer>();
        bool mLodFar;

        float WalkSpeed => Kind == Species.Rabbit ? 1.2f : Kind == Species.Songbird ? 0.6f : 1.4f;
        float RunSpeed => Kind == Species.Rabbit ? 6.5f : Kind == Species.Songbird ? 7f : 8.5f;
        float FleeRadius => Kind == Species.Rabbit ? 6f : Kind == Species.Songbird ? 5f : 10f;
        float LookRadius => Kind == Species.Rabbit ? 9f : Kind == Species.Songbird ? 0f : 18f;

        public void Init(Species kind, GameObject model, int seed)
        {
            Kind = kind;
            mRng = new System.Random(seed);
            mYaw = (float)mRng.NextDouble() * 360f;
            var inst = Instantiate(model, transform, false);
            inst.name = "Model";
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = PropMaterials.ForModel(mats[i]);
                r.sharedMaterials = mats;
                r.shadowCastingMode = kind == Species.Songbird ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
                (r.name.EndsWith("_LOD1") ? mLod1 : mLod0).Add(r);
                r.gameObject.layer = Layers.Entity;
            }
            mBody = Find(inst.transform, "Body"); mNeck = Find(inst.transform, "Neck"); mHead = Find(inst.transform, "Head");
            mEarL = Find(inst.transform, "EarL"); mEarR = Find(inst.transform, "EarR"); mTail = Find(inst.transform, "Tail");
            mWingL = Find(inst.transform, "WingL"); mWingR = Find(inst.transform, "WingR");
            string[] legs = { "LegFL", "LegFR", "LegBL", "LegBR" };
            for (int i = 0; i < 4; i++) { mLegs[i] = Find(inst.transform, legs[i]); if (mLegs[i]) mLegRest[i] = mLegs[i].localRotation; }
            if (mBody) mBodyRest = mBody.localPosition;
            if (mNeck) mNeckRest = mNeck.localRotation;
            if (mHead) mHeadRest = mHead.localRotation;
            if (mEarL) mEarLRest = mEarL.localRotation;
            if (mEarR) mEarRRest = mEarR.localRotation;
            if (mTail) mTailRest = mTail.localRotation;
            if (mWingL) mWingLRest = mWingL.localRotation;
            if (mWingR) mWingRRest = mWingR.localRotation;
            SetLod(false);
            switch (kind)
            {
                case Species.Deer: case Species.Doe: HalfWidth = 0.35f; Height = 1.4f; break;
                case Species.Rabbit: HalfWidth = 0.15f; Height = 0.35f; break;
                case Species.Songbird: HalfWidth = 0.06f; Height = 0.12f; mState = State.Perch; break;
            }
            Enter(kind == Species.Songbird ? State.Perch : State.Idle);
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var f = Find(c, name); if (f != null) return f; }
            return null;
        }

        void SetLod(bool far)
        {
            mLodFar = far;
            foreach (var r in mLod0) r.enabled = !far;
            foreach (var r in mLod1) r.enabled = far;
        }

        public void SetVisible(bool v)
        {
            foreach (var r in mLod0) r.enabled = v && !mLodFar;
            foreach (var r in mLod1) r.enabled = v && mLodFar;
        }

        void Enter(State s)
        {
            mState = s;
            mStateTime = 0;
            float r = (float)mRng.NextDouble();
            switch (s)
            {
                case State.Idle: mStateDur = 2f + r * 4f; break;
                case State.Graze: mStateDur = 4f + r * 8f; break;
                case State.Look: mStateDur = 1.5f + r * 2.5f; break;
                case State.Walk: mStateDur = 3f + r * 6f; PickTarget(10f); break;
                case State.Flee: mStateDur = 3.5f + r * 2f; break;
                case State.Perch: mStateDur = 3f + r * 9f; break;
                case State.Fly: mStateDur = 4f + r * 6f; mFlyHeight = 4f + r * 9f; PickTarget(28f); break;
            }
        }

        void PickTarget(float radius)
        {
            float a = (float)mRng.NextDouble() * Mathf.PI * 2f, d = radius * (0.4f + 0.6f * (float)mRng.NextDouble());
            mTarget = transform.position + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
        }

        /// <summary>Called by the manager every frame (near) or at a low rate (far).</summary>
        public void Tick(float dt, Vector3 player, bool playerSneaking)
        {
            var w = VoxelWorld.Instance;
            if (w == null) return;
            mStateTime += dt;
            var pos = transform.position;
            var toPlayer = player - pos; toPlayer.y = 0;
            float pd = toPlayer.magnitude;
            float flee = FleeRadius * (playerSneaking ? 0.55f : 1f);

            // ---- awareness
            if (Kind == Species.Songbird)
            {
                if (mState == State.Perch && pd < flee) { Enter(State.Fly); SoundHub.At("bird/chirp", pos, 0.7f); }
            }
            else if (mState != State.Flee && pd < flee) { Enter(State.Flee); if (Kind != Species.Rabbit) SoundHub.At("deer/call", pos, 0.5f); }
            else if (mState != State.Flee && mState != State.Look && pd < LookRadius && mRng.NextDouble() < dt * 0.4) Enter(State.Look);

            if (mStateTime > mStateDur)
            {
                switch (mState)
                {
                    case State.Flee: Enter(State.Idle); break;
                    case State.Fly: Enter(State.Perch); break;
                    case State.Perch: Enter(mRng.NextDouble() < 0.35 ? State.Fly : State.Perch); break;
                    default:
                        double r = mRng.NextDouble();
                        Enter(r < 0.35 ? State.Graze : r < 0.75 ? State.Walk : State.Idle);
                        break;
                }
            }

            // ---- desired motion
            Vector3 desired = Vector3.zero;
            float speed = 0;
            switch (mState)
            {
                case State.Walk:
                {
                    var d = mTarget - pos; d.y = 0;
                    if (d.magnitude < 0.6f) Enter(State.Idle);
                    else { desired = d.normalized; speed = WalkSpeed; }
                    break;
                }
                case State.Flee:
                    desired = pd > 0.01f ? -toPlayer / pd : transform.forward;
                    // Zig a little so it reads as panic, not a laser.
                    desired = Quaternion.Euler(0, Mathf.Sin(mStateTime * 2.3f + mPhase) * 25f, 0) * desired;
                    speed = RunSpeed;
                    break;
                case State.Look:
                    if (pd > 0.01f) TurnTowards(toPlayer, dt, 120f);
                    break;
                case State.Fly:
                {
                    var d = mTarget - pos; d.y = 0;
                    desired = d.sqrMagnitude > 1f ? d.normalized : transform.forward;
                    speed = RunSpeed;
                    break;
                }
            }

            if (Kind == Species.Songbird) MoveBird(w, dt, desired, speed);
            else MoveWalker(w, dt, desired, speed);

            if (Time.time > mNextSound)
            {
                mNextSound = Time.time + 6f + (float)mRng.NextDouble() * 14f;
                if (Kind == Species.Songbird && mState == State.Perch && !Far) SoundHub.At("bird/chirp", pos, 0.6f);
            }

            if (!Far) Animate(dt, speed);
        }

        void TurnTowards(Vector3 dir, float dt, float degPerSec)
        {
            float target = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            mYaw = Mathf.SmoothDampAngle(mYaw, target, ref mYawVel, 0.25f, degPerSec, dt);
        }

        bool Walkable(VoxelWorld w, int x, int y, int z)
        {
            byte below = w.GetBlock(x, y - 1, z);
            if (!Blocks.IsSolid[below] || below == B.Water) return false;
            for (int k = 0; k < Mathf.CeilToInt(Height); k++)
            {
                byte b = w.GetBlock(x, y + k, z);
                if (Blocks.IsSolid[b] || b == B.Water) return false;
            }
            return true;
        }

        bool FootprintClear(VoxelWorld w, Vector3 p, int y)
        {
            float r = HalfWidth * 0.9f;
            for (int i = 0; i < 4; i++)
            {
                int x = Mathf.FloorToInt(p.x + ((i & 1) == 0 ? -r : r)), z = Mathf.FloorToInt(p.z + ((i & 2) == 0 ? -r : r));
                for (int k = 0; k < Mathf.CeilToInt(Height); k++)
                {
                    byte b = w.GetBlock(x, y + k, z);
                    if (Blocks.IsSolid[b] || b == B.Water) return false;
                }
            }
            return true;
        }

        void MoveWalker(VoxelWorld w, float dt, Vector3 desired, float speed)
        {
            var pos = transform.position;
            if (speed > 0 && desired.sqrMagnitude > 0.01f) TurnTowards(desired, dt, speed > 3f ? 360f : 140f);
            var fwd = Quaternion.Euler(0, mYaw, 0) * Vector3.forward;
            float targetSpeed = speed;
            if (Kind == Species.Rabbit && speed > 0)
            {
                // Rabbits move in hops.
                mHop += dt * (speed > 3f ? 4.5f : 2.2f);
                if (mHop >= 1f) { mHop -= 1f; if (!Far) SoundHub.At("rabbit/hop", pos, 0.25f); }
                targetSpeed *= Mathf.Sin(mHop * Mathf.PI) > 0.2f ? 1.6f : 0.15f;
            }
            Velocity = Vector3.MoveTowards(Velocity, fwd * targetSpeed, dt * 14f);

            // Step: look one step ahead; climb up to 1 block, never walk off drops > 2 or into water.
            var next = pos + Velocity * dt;
            int nx = Mathf.FloorToInt(next.x), nz = Mathf.FloorToInt(next.z), y = Mathf.FloorToInt(pos.y + 0.01f);
            int ny = -999;
            if (Walkable(w, nx, y, nz) && FootprintClear(w, next, y)) ny = y;
            else if (Walkable(w, nx, y + 1, nz) && FootprintClear(w, next, y + 1)) ny = y + 1;
            else if (Walkable(w, nx, y - 1, nz) && FootprintClear(w, next, y - 1)) ny = y - 1;
            else if (Walkable(w, nx, y - 2, nz) && FootprintClear(w, next, y - 2)) ny = y - 2;
            if (ny == -999)
            {
                // Blocked: turn away and pick a new goal.
                Velocity = Vector3.zero;
                mYaw += 100f + (float)mRng.NextDouble() * 160f;
                if (mState == State.Walk) PickTarget(10f);
                next = pos;
            }
            else
            {
                float groundY = ny;
                next.y = Mathf.MoveTowards(pos.y, groundY, dt * 6f);
            }
            // Gravity safety if the ground vanished (mined out).
            if (!Blocks.IsSolid[w.GetBlock(Mathf.FloorToInt(next.x), Mathf.FloorToInt(next.y - 0.05f), Mathf.FloorToInt(next.z))])
                next.y -= dt * 8f;
            transform.SetPositionAndRotation(next, Quaternion.Euler(0, mYaw, 0));
        }

        void MoveBird(VoxelWorld w, float dt, Vector3 desired, float speed)
        {
            var pos = transform.position;
            if (mState == State.Fly)
            {
                TurnTowards(desired, dt, 220f);
                var fwd = Quaternion.Euler(0, mYaw, 0) * Vector3.forward;
                int ground = w.SurfaceY(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z));
                float targetY = (ground > 0 ? ground : pos.y) + mFlyHeight;
                float vy = Mathf.Clamp((targetY - pos.y) * 1.5f, -4f, 5f);
                Velocity = Vector3.Lerp(Velocity, fwd * speed + Vector3.up * vy, dt * 3f);
                pos += Velocity * dt;
                // Near the end of the flight, descend to land.
                if (mStateTime > mStateDur - 1.5f) mFlyHeight = Mathf.MoveTowards(mFlyHeight, 0.3f, dt * 6f);
            }
            else
            {
                // Perched / hopping on the ground or a leaf top.
                Velocity = Vector3.zero;
                int x = Mathf.FloorToInt(pos.x), z = Mathf.FloorToInt(pos.z);
                int top = w.SurfaceY(x, z);
                var leafTop = top;
                if (top > 0) pos.y = Mathf.MoveTowards(pos.y, top + 1f, dt * 4f);
                _ = leafTop;
                if (mRng.NextDouble() < dt * 0.6) { mYaw += (float)(mRng.NextDouble() - 0.5) * 140f; }
            }
            transform.SetPositionAndRotation(pos, Quaternion.Euler(mState == State.Fly ? Mathf.Clamp(-Velocity.y * 6f, -25f, 25f) : 0, mYaw, 0));
        }

        void Animate(float dt, float speed)
        {
            float v = new Vector2(Velocity.x, Velocity.z).magnitude;
            mPhase += dt * (Kind == Species.Rabbit ? 0 : v * (Kind == Species.Songbird ? 0 : 3.4f));
            float t = Time.time;

            // Legs: alternating swing proportional to speed (diagonal pairs for a trot).
            float swing = Mathf.Clamp01(v / 2f) * (v > 4f ? 38f : 24f);
            for (int i = 0; i < 4; i++)
            {
                if (mLegs[i] == null) continue;
                float ph = mPhase + (i == 0 || i == 3 ? 0f : Mathf.PI);
                float a = Kind == Species.Rabbit ? (mState == State.Flee || mState == State.Walk ? Mathf.Sin(mHop * Mathf.PI * 2f) * 30f : 0f) : Mathf.Sin(ph) * swing;
                mLegs[i].localRotation = mLegRest[i] * Quaternion.Euler(a, 0, 0);
            }

            // Body: breathing + gait bob; rabbit hop arc.
            if (mBody)
            {
                float bob = Kind == Species.Rabbit && v > 0.2f ? Mathf.Abs(Mathf.Sin(mHop * Mathf.PI)) * 0.12f : Mathf.Abs(Mathf.Sin(mPhase)) * 0.03f * Mathf.Clamp01(v / 2f);
                float breathe = Mathf.Sin(t * 1.6f + mPhase) * 0.004f;
                mBody.localPosition = mBodyRest + new Vector3(0, bob + breathe, 0);
            }

            // Neck/head: graze (head down), look (head up/turn), alert ears.
            float down = mState == State.Graze ? 1f : 0f;
            mHeadDown = Mathf.MoveTowards(mHeadDown, down, dt * 1.2f);
            if (mNeck) mNeck.localRotation = mNeckRest * Quaternion.Euler(mHeadDown * 55f + Mathf.Sin(t * 0.7f) * 2f, 0, 0);
            if (mHead)
            {
                float nibble = mState == State.Graze ? Mathf.Sin(t * 6f) * 4f : 0f;
                float turn = mState == State.Look ? Mathf.Sin(t * 0.8f) * 12f : Mathf.Sin(t * 0.33f + mPhase) * 4f;
                float pitch = Kind == Species.Rabbit ? mHeadDown * 25f : 0f;
                mHead.localRotation = mHeadRest * Quaternion.Euler(nibble + pitch, turn, 0);
            }
            float twitch = Mathf.PerlinNoise(t * 0.9f, mPhase) > 0.78f ? Mathf.Sin(t * 30f) * 18f : 0f;
            if (mEarL) mEarL.localRotation = mEarLRest * Quaternion.Euler(mState == State.Look ? -12f : 0f, twitch, 0);
            if (mEarR) mEarR.localRotation = mEarRRest * Quaternion.Euler(mState == State.Look ? -12f : 0f, -twitch * 0.7f, 0);
            if (mTail) mTail.localRotation = mTailRest * Quaternion.Euler(mState == State.Flee ? -35f : Mathf.Sin(t * 3f) * 6f, 0, 0);

            // Wings: flap in flight, folded with occasional flutter when perched.
            if (mWingL && mWingR)
            {
                float flap = mState == State.Fly ? Mathf.Sin(t * 26f) * 55f : (Mathf.PerlinNoise(t * 2f, mPhase) > 0.85f ? Mathf.Sin(t * 30f) * 20f : 0f);
                mWingL.localRotation = mWingLRest * Quaternion.Euler(0, 0, flap);
                mWingR.localRotation = mWingRRest * Quaternion.Euler(0, 0, -flap);
            }
        }

        public void UpdateLod(float distance)
        {
            bool far = distance > 40f;
            if (far != mLodFar) SetLod(far);
        }

        /// <summary>Axis-aligned box for placement checks.</summary>
        public Bounds Box => new Bounds(transform.position + Vector3.up * Height * 0.5f, new Vector3(HalfWidth * 2f, Height, HalfWidth * 2f));
    }
}
