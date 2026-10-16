using System;
using UnityEngine;
using Wildmere.Core;
using Wildmere.Items;
using Wildmere.World;

namespace Wildmere.Player
{
    /// <summary>
    /// First-person survival controller on voxel physics: walk/sprint/crouch (edge-safe), jump, swim,
    /// fall damage, health + air, head bob, FOV kick and camera shake. Input can be disabled by menus
    /// and driven by the self-test harness through <see cref="ScriptedInput"/>.
    /// </summary>
    public sealed class PlayerController : MonoBehaviour
    {
        public static PlayerController Instance { get; private set; }

        public const float HalfWidth = 0.3f, Height = 1.8f, CrouchHeight = 1.5f, Eye = 1.62f, CrouchEye = 1.3f;
        public const float WalkSpeed = 4.3f, SprintSpeed = 5.9f, CrouchSpeed = 1.6f, SwimSpeed = 2.4f;
        public const float Gravity = 28f, JumpVelocity = 8.6f, MaxFall = 55f;
        public const float MaxHealth = 20f, MaxAir = 10f;

        public Camera Cam { get; private set; }
        public Transform CamPivot { get; private set; }
        public Inventory Inventory { get; } = new Inventory();

        public Vector3 Velocity;
        public float Yaw, Pitch;
        public bool InputEnabled = true;
        public bool Flying;
        public bool Grounded { get; private set; }
        public bool Crouching { get; private set; }
        public bool Sprinting { get; private set; }
        public float Submerged { get; private set; }
        public bool HeadUnderwater { get; private set; }
        public float Health = MaxHealth;
        public float Air = MaxAir;
        public bool Dead { get; private set; }
        public Vector3 SpawnPoint;

        /// <summary>Set by the harness: overrides keyboard movement (x = strafe, y = forward) and jump.</summary>
        public Vector2? ScriptedMove;
        public bool ScriptedJump, ScriptedSprint, ScriptedCrouch;

        public event Action<float, string> Damaged;     // amount, cause
        public event Action Died;
        public event Action<float> Landed;              // fall distance
        public event Action<SoundMat> Footstep;
        public event Action<bool> WaterTransition;       // true = entered

        float mFallStartY;
        bool mWasGrounded = true;
        float mStepDistance;
        float mBobPhase;
        float mShake;
        float mFovKick;
        float mLastHurt = -999f;
        float mRegenTimer;
        float mDrownTimer;
        float mJumpBuffer, mCoyote;
        float mEyeHeight = Eye;
        bool mWasSubmerged;

        public float CurrentHeight => Crouching ? CrouchHeight : Height;
        public Vector3 EyePosition => transform.position + Vector3.up * mEyeHeight;
        public Vector3 LookDir => CamPivot.forward;

        public static PlayerController Create(Vector3 feet, float yaw)
        {
            var go = new GameObject("Player");
            go.layer = Layers.Player;
            var p = go.AddComponent<PlayerController>();
            p.transform.position = feet;
            p.Yaw = yaw;
            p.SpawnPoint = feet;
            return p;
        }

        void Awake()
        {
            Instance = this;
            CamPivot = new GameObject("CameraPivot").transform;
            CamPivot.SetParent(transform, false);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(CamPivot, false);
            Cam = camGo.AddComponent<Camera>();
            Cam.nearClipPlane = 0.05f;
            Cam.farClipPlane = 1200f;
            Cam.clearFlags = CameraClearFlags.Skybox;
            camGo.AddComponent<AudioListener>();
            mFallStartY = transform.position.y;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        public void Shake(float amount)
        {
            if (GameSettings.Current.CameraShake) mShake = Mathf.Max(mShake, amount);
        }

        public void Hurt(float amount, string cause)
        {
            if (Dead || amount <= 0 || Flying) return;
            Health = Mathf.Max(0, Health - amount);
            mLastHurt = Time.time;
            Shake(Mathf.Clamp(amount * 0.08f, 0.15f, 0.6f));
            Damaged?.Invoke(amount, cause);
            if (Health <= 0) { Dead = true; InputEnabled = false; Died?.Invoke(); }
        }

        public void Respawn()
        {
            var w = VoxelWorld.Instance;
            Vector3 p = SpawnPoint;
            if (w != null)
            {
                int sy = w.SurfaceY(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z));
                if (sy > 0) p.y = sy + 1.01f;
            }
            transform.position = p;
            Velocity = Vector3.zero;
            Health = MaxHealth; Air = MaxAir; Dead = false; InputEnabled = true;
            mFallStartY = p.y;
        }

        public void Teleport(Vector3 feet)
        {
            transform.position = feet;
            Velocity = Vector3.zero;
            mFallStartY = feet.y;
        }

        void Update()
        {
            var w = VoxelWorld.Instance;
            if (w == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f) return;
            var s = GameSettings.Current;

            // ---- look
            if (InputEnabled && Cursor.lockState == CursorLockMode.Locked)
            {
                float sens = 2.0f * s.MouseSensitivity;
                Yaw += Input.GetAxisRaw("Mouse X") * sens;
                Pitch -= Input.GetAxisRaw("Mouse Y") * sens * (s.InvertY ? -1f : 1f);
                Pitch = Mathf.Clamp(Pitch, -89.5f, 89.5f);
            }

            // ---- intent
            Vector2 move = Vector2.zero;
            bool jump = false, sprint = false, crouch = false;
            if (ScriptedMove.HasValue)
            {
                move = ScriptedMove.Value; jump = ScriptedJump; sprint = ScriptedSprint; crouch = ScriptedCrouch;
            }
            else if (InputEnabled)
            {
                if (GameSettings.Held(Control.Forward)) move.y += 1;
                if (GameSettings.Held(Control.Back)) move.y -= 1;
                if (GameSettings.Held(Control.Right)) move.x += 1;
                if (GameSettings.Held(Control.Left)) move.x -= 1;
                jump = GameSettings.Held(Control.Jump);
                sprint = GameSettings.Held(Control.Sprint);
                crouch = GameSettings.Held(Control.Crouch);
            }
            if (move.sqrMagnitude > 1f) move.Normalize();

            Vector3 fwd = Quaternion.Euler(0, Yaw, 0) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0, Yaw, 0) * Vector3.right;
            Vector3 wish = fwd * move.y + right * move.x;

            Submerged = VoxelPhysics.Submersion(w, transform.position, CurrentHeight);
            bool swimming = Submerged > 0.3f && !Flying;

            // Crouch (can't stand up under a ceiling).
            bool wantCrouch = crouch && !Flying && !swimming;
            if (!wantCrouch && Crouching)
            {
                var p = transform.position;
                if (VoxelPhysics.Overlaps(w, new Vector3(p.x - HalfWidth, p.y, p.z - HalfWidth), new Vector3(p.x + HalfWidth, p.y + Height, p.z + HalfWidth)))
                    wantCrouch = true;
            }
            Crouching = wantCrouch;
            Sprinting = sprint && move.y > 0.5f && !Crouching && !swimming && Health > 4f;

            float speed = Flying ? (sprint ? 22f : 11f) : swimming ? SwimSpeed : Crouching ? CrouchSpeed : Sprinting ? SprintSpeed : WalkSpeed;

            if (Flying)
            {
                Vector3 target = wish * speed;
                target.y = (jump ? speed : 0f) - (crouch ? speed : 0f);
                Velocity = Vector3.Lerp(Velocity, target, 1f - Mathf.Exp(-10f * dt));
            }
            else if (swimming)
            {
                Vector3 target = wish * speed;
                Velocity.x = Mathf.Lerp(Velocity.x, target.x, 1f - Mathf.Exp(-6f * dt));
                Velocity.z = Mathf.Lerp(Velocity.z, target.z, 1f - Mathf.Exp(-6f * dt));
                Velocity.y -= 5f * dt;
                if (jump) Velocity.y = Mathf.MoveTowards(Velocity.y, 3.6f, 22f * dt);
                Velocity.y *= 1f - Mathf.Min(1f, 2.2f * dt);
                Velocity.y = Mathf.Max(Velocity.y, -4f);
            }
            else
            {
                float accel = Grounded ? 60f : 14f;
                Vector3 target = wish * speed;
                Vector3 h = new Vector3(Velocity.x, 0, Velocity.z);
                h = Vector3.MoveTowards(h, target, accel * dt);
                if (Grounded && wish.sqrMagnitude < 0.01f) h *= Mathf.Exp(-14f * dt);
                Velocity.x = h.x; Velocity.z = h.z;
                Velocity.y = Mathf.Max(Velocity.y - Gravity * dt, -MaxFall);

                // Jump with a short buffer and coyote time for responsiveness.
                mJumpBuffer = jump ? 0.12f : mJumpBuffer - dt;
                mCoyote = Grounded ? 0.1f : mCoyote - dt;
                if (mJumpBuffer > 0 && mCoyote > 0)
                {
                    Velocity.y = JumpVelocity;
                    mJumpBuffer = 0; mCoyote = 0;
                    if (Sprinting) { Velocity.x += fwd.x * 1.2f; Velocity.z += fwd.z * 1.2f; }
                }
            }

            // ---- move with collision
            Vector3 pos = transform.position;
            Vector3 delta = Velocity * dt;

            // Crouch-walking never drops off an edge (like holding sneak at a cliff).
            if (Crouching && Grounded && !Flying)
            {
                Vector3 tx = pos + new Vector3(delta.x, 0, 0);
                if (!VoxelPhysics.Grounded(w, tx, HalfWidth - 0.05f)) { delta.x = 0; Velocity.x = 0; }
                Vector3 tz = pos + new Vector3(delta.x, 0, delta.z);
                if (!VoxelPhysics.Grounded(w, tz, HalfWidth - 0.05f)) { delta.z = 0; Velocity.z = 0; }
            }

            var blocked = VoxelPhysics.Move(w, ref pos, delta, HalfWidth, CurrentHeight);
            if (blocked.y != 0) Velocity.y = 0;
            if (blocked.x != 0) Velocity.x = 0;
            if (blocked.z != 0) Velocity.z = 0;

            // Climb out of water onto a ledge.
            if (swimming && jump && (blocked.x != 0 || blocked.z != 0)) Velocity.y = 6.5f;

            // Auto-jump (optional): step up one block when walking into it.
            if (s.AutoJump && Grounded && (blocked.x != 0 || blocked.z != 0) && wish.sqrMagnitude > 0.1f && !Crouching)
            {
                var up = pos + Vector3.up * 1.05f + wish.normalized * 0.4f;
                if (!VoxelPhysics.Overlaps(w, new Vector3(up.x - HalfWidth, up.y, up.z - HalfWidth), new Vector3(up.x + HalfWidth, up.y + CurrentHeight, up.z + HalfWidth)))
                    Velocity.y = JumpVelocity;
            }

            transform.position = pos;
            Grounded = !Flying && Velocity.y <= 0.01f && VoxelPhysics.Grounded(w, pos, HalfWidth);

            // ---- landing / fall damage
            if (Grounded && !mWasGrounded)
            {
                float fall = mFallStartY - pos.y;
                Landed?.Invoke(fall);
                if (fall > 3.4f && Submerged < 0.3f) Hurt(Mathf.Floor(fall - 3f), "fall");
                if (fall > 1.5f) Shake(Mathf.Clamp01((fall - 1.5f) * 0.08f));
            }
            if (Grounded || swimming || Flying) mFallStartY = pos.y;
            else mFallStartY = Mathf.Max(mFallStartY, pos.y);
            mWasGrounded = Grounded;

            if (Submerged > 0.3f != mWasSubmerged) { mWasSubmerged = Submerged > 0.3f; WaterTransition?.Invoke(mWasSubmerged); }

            // ---- footsteps
            float hs = new Vector2(Velocity.x, Velocity.z).magnitude;
            if (Grounded && hs > 0.5f)
            {
                mStepDistance += hs * dt;
                float stride = Sprinting ? 2.1f : Crouching ? 1.2f : 1.7f;
                if (mStepDistance >= stride)
                {
                    mStepDistance = 0;
                    byte under = w.GetBlock(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y - 0.2f), Mathf.FloorToInt(pos.z));
                    byte at = w.GetBlock(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y + 0.1f), Mathf.FloorToInt(pos.z));
                    Footstep?.Invoke(at == B.Water ? SoundMat.Water : Blocks.Get(under).Sound);
                }
            }

            // ---- air / drowning / regeneration
            HeadUnderwater = w.GetBlock(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y + mEyeHeight), Mathf.FloorToInt(pos.z)) == B.Water;
            if (HeadUnderwater && !Flying)
            {
                Air = Mathf.Max(0, Air - dt);
                if (Air <= 0) { mDrownTimer += dt; if (mDrownTimer >= 1f) { mDrownTimer = 0; Hurt(2f, "drown"); } }
            }
            else Air = Mathf.Min(MaxAir, Air + dt * 3f);
            if (!Dead && Health < MaxHealth && Time.time - mLastHurt > 6f)
            {
                mRegenTimer += dt;
                if (mRegenTimer >= 3f) { mRegenTimer = 0; Health = Mathf.Min(MaxHealth, Health + 1f); }
            }
            if (pos.y < -40f && !Dead) Hurt(100f, "void");

            UpdateCamera(dt, hs);
        }

        void UpdateCamera(float dt, float hs)
        {
            var s = GameSettings.Current;
            float targetEye = Crouching ? CrouchEye : Eye;
            mEyeHeight = Mathf.Lerp(mEyeHeight, targetEye, 1f - Mathf.Exp(-14f * dt));

            Vector3 bob = Vector3.zero;
            if (s.HeadBob && Grounded && hs > 0.5f)
            {
                mBobPhase += hs * dt * 1.85f;
                float amp = Mathf.Clamp01(hs / SprintSpeed) * 0.045f;
                bob = new Vector3(Mathf.Cos(mBobPhase) * amp * 0.6f, Mathf.Abs(Mathf.Sin(mBobPhase)) * amp, 0);
            }
            else mBobPhase = Mathf.Lerp(mBobPhase, Mathf.Round(mBobPhase / Mathf.PI) * Mathf.PI, dt * 4f);

            Vector3 shake = Vector3.zero;
            float shakeRoll = 0;
            if (mShake > 0)
            {
                float t = Time.time * 32f;
                shake = new Vector3(Mathf.PerlinNoise(t, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, t) - 0.5f, 0) * mShake * 0.25f;
                shakeRoll = (Mathf.PerlinNoise(t, t) - 0.5f) * mShake * 4f;
                mShake = Mathf.MoveTowards(mShake, 0, dt * 1.6f);
            }

            CamPivot.localPosition = new Vector3(0, mEyeHeight, 0) + bob;
            CamPivot.rotation = Quaternion.Euler(Pitch, Yaw, shakeRoll);
            Cam.transform.localPosition = shake;

            float fovTarget = s.Fov + (Sprinting ? 7f : 0f) + (Flying && Velocity.magnitude > 12f ? 8f : 0f);
            mFovKick = Mathf.Lerp(mFovKick, fovTarget, 1f - Mathf.Exp(-8f * dt));
            Cam.fieldOfView = mFovKick;
        }
    }
}
