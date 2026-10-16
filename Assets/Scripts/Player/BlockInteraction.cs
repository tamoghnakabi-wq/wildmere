using System;
using UnityEngine;
using Wildmere.Core;
using Wildmere.Items;
using Wildmere.World;

namespace Wildmere.Player
{
    /// <summary>
    /// Mining and building: targeting raycast, selection outline, timed mining with crack overlay,
    /// break particles and drops, collision-safe placement, hotbar selection and item pickup.
    /// Raises events the audio/UI modules subscribe to.
    /// </summary>
    public sealed class BlockInteraction : MonoBehaviour
    {
        public const float Reach = 5f;

        public PlayerController Player;
        public VoxelHit Target { get; private set; }
        public float MineProgress { get; private set; }   // 0..1 on the current target
        public bool InputEnabled = true;

        /// <summary>Harness overrides (null = use the mouse).</summary>
        public bool? ScriptedAttack, ScriptedUse;

        public event Action<BlockDef, Vector3Int> MineHit;          // each periodic hit while mining
        public event Action<BlockDef, Vector3Int> BlockBroken;
        public event Action<BlockDef, Vector3Int> BlockPlaced;
        public event Action<Station> StationUsed;                   // right-click on workbench/furnace
        public event Action Swing;
        public event Action<ItemDef> ToolBroke;

        GameObject mOutline, mCrack;
        Material mCrackMat;
        Vector3Int mMiningBlock;
        float mHitTimer;
        float mPlaceCooldown;
        bool mUseLatch;
        readonly System.Random mRng = new System.Random();
        BreakParticles mParticles;
        Viewmodel mView;

        void Start()
        {
            mOutline = BuildOutline();
            mCrack = BuildCrack();
            mParticles = new GameObject("BreakParticles").AddComponent<BreakParticles>();
            mView = Player.Cam.gameObject.AddComponent<Viewmodel>();
            mView.Player = Player;
            Player.Inventory.Changed += () => mView.Refresh();
            mView.Refresh();
        }

        void OnDestroy()
        {
            if (mOutline) Destroy(mOutline);
            if (mCrack) Destroy(mCrack);
            if (mParticles) Destroy(mParticles.gameObject);
        }

        void Update()
        {
            var w = VoxelWorld.Instance;
            if (w == null || Player == null) return;
            var inv = Player.Inventory;
            float dt = Time.deltaTime;
            bool active = InputEnabled && Player.InputEnabled && !Player.Dead;

            // Hotbar selection.
            if (active && ScriptedAttack == null)
            {
                float wheel = Input.mouseScrollDelta.y;
                if (wheel > 0.01f) Select((inv.Selected + Inventory.Hotbar - 1) % Inventory.Hotbar);
                else if (wheel < -0.01f) Select((inv.Selected + 1) % Inventory.Hotbar);
                for (int i = 0; i < Inventory.Hotbar; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i)) Select(i);
                if (GameSettings.Pressed(Control.Drop)) DropSelected(Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.LeftControl));
                if (GameSettings.Pressed(Control.PickBlock) && Target.Hit) PickBlock(Target.Id);
            }

            // Target.
            Target = active ? VoxelPhysics.Raycast(w, Player.EyePosition, Player.LookDir, Reach) : default;
            UpdateOutline();

            bool attack = active && (ScriptedAttack ?? GameSettings.Held(Control.Attack));
            bool use = active && (ScriptedUse ?? GameSettings.Held(Control.Use));

            // Mining.
            if (attack && Target.Hit)
            {
                var def = Blocks.Get(Target.Id);
                if (Target.Block != mMiningBlock) { mMiningBlock = Target.Block; MineProgress = 0; mHitTimer = 0; }
                float time = ItemDb.BreakTime(def, inv.SelectedStack.Def);
                if (!float.IsInfinity(time))
                {
                    MineProgress += dt / Mathf.Max(0.05f, time);
                    mHitTimer -= dt;
                    if (mHitTimer <= 0) { mHitTimer = 0.24f; MineHit?.Invoke(def, Target.Block); Swing?.Invoke(); mView.Swing(); mParticles.Burst(Target, def, 3, 0.6f); }
                    if (MineProgress >= 1f) Break(w, Target, def);
                }
            }
            else
            {
                MineProgress = 0;
                mMiningBlock = new Vector3Int(int.MinValue, 0, 0);
                if (attack && ScriptedAttack == null && Input.GetKeyDown(GameSettings.Current.Key(Control.Attack))) { Swing?.Invoke(); mView.Swing(); }
            }
            UpdateCrack();

            // Use / place (repeat while held, like building a wall).
            mPlaceCooldown -= dt;
            if (use && mPlaceCooldown <= 0f && Target.Hit)
            {
                if (TryUse(w, inv)) mPlaceCooldown = 0.22f;
                else mPlaceCooldown = 0.1f;
            }
            if (!use) mPlaceCooldown = Mathf.Min(mPlaceCooldown, 0f);

            // Pick up nearby drops.
            var center = Player.transform.position + Vector3.up * 0.9f;
            foreach (var d in ItemDrop.All.ToArray())
            {
                if (d == null) continue;
                var to = center - d.transform.position;
                float dist = to.magnitude;
                if (dist < 2.4f && d.Age > d.PickupDelay && inv.CanFit(d.Stack.Id, 1)) d.Velocity = Vector3.Lerp(d.Velocity, to.normalized * 9f, dt * 8f);
                if (dist < 1.1f) d.TryCollect(inv);
            }
        }

        public void Select(int slot)
        {
            Player.Inventory.Selected = Mathf.Clamp(slot, 0, Inventory.Hotbar - 1);
            Player.Inventory.Notify();
        }

        void PickBlock(byte id)
        {
            var inv = Player.Inventory;
            for (int i = 0; i < Inventory.Hotbar; i++)
                if (inv.Slots[i].Id == id) { Select(i); return; }
        }

        public void DropSelected(bool wholeStack)
        {
            var inv = Player.Inventory;
            var s = inv.SelectedStack;
            if (s.IsEmpty) return;
            int n = wholeStack ? s.Count : 1;
            var drop = s; drop.Count = n;
            inv.Slots[inv.Selected].Count -= n;
            if (inv.Slots[inv.Selected].Count <= 0) inv.Slots[inv.Selected] = ItemStack.Empty;
            inv.Notify();
            ItemDrop.Spawn(drop, Player.EyePosition + Player.LookDir * 0.6f - Vector3.up * 0.3f, Player.LookDir * 5f + Vector3.up * 1.5f, 1.5f);
        }

        void Break(VoxelWorld w, VoxelHit hit, BlockDef def)
        {
            var inv = Player.Inventory;
            var held = inv.SelectedStack.Def;
            int drop = ItemDb.DropOf(def, held, mRng);
            byte meta = w.GetMeta(hit.Block.x, hit.Block.y, hit.Block.z);
            if (!w.SetBlock(hit.Block.x, hit.Block.y, hit.Block.z, B.Air)) return;
            _ = meta;
            MineProgress = 0;
            mParticles.Burst(hit, def, 18, 1f);
            BlockBroken?.Invoke(def, hit.Block);
            if (drop != 0)
            {
                var p = hit.Block + new Vector3(0.5f, 0.4f, 0.5f);
                var v = new Vector3((float)mRng.NextDouble() - 0.5f, 2.5f, (float)mRng.NextDouble() - 0.5f) * 1.2f;
                ItemDrop.Spawn(ItemStack.Of(drop, 1), p, v);
            }
            if (held != null && held.Durability > 0 && def.Hardness > 0f)
            {
                var heldDef = held;
                if (inv.DamageSelected()) ToolBroke?.Invoke(heldDef);
            }
        }

        bool TryUse(VoxelWorld w, Inventory inv)
        {
            var target = Blocks.Get(Target.Id);
            bool sneaking = Player.Crouching;
            if (!sneaking && (Target.Id == B.Workbench || Target.Id == B.Furnace))
            {
                StationUsed?.Invoke(Target.Id == B.Workbench ? Station.Workbench : Station.Furnace);
                return true;
            }

            var stack = inv.SelectedStack;
            var item = stack.Def;
            if (item == null || item.PlacesBlock == 0) return false;
            byte id = item.PlacesBlock;

            // Clicking a replaceable block (tall grass) replaces it; otherwise place against the face.
            Vector3Int p = target.Replaceable && Target.Id != B.Water ? Target.Block : Target.Adjacent;
            if (p.y < 1 || p.y >= Chunk.SY - 1) return false;
            byte existing = w.GetBlock(p.x, p.y, p.z);
            if (!Blocks.Get(existing).Replaceable) return false;

            var def = Blocks.Get(id);
            if (def.NeedsSupport)
            {
                byte below = w.GetBlock(p.x, p.y - 1, p.z);
                if (!Blocks.IsSolid[below] || Blocks.IsLeaves(below)) return false;
                if (id != B.Torch && below != B.Grass && below != B.Dirt && below != B.Podzol && below != B.Sand && below != B.SnowyGrass) return false;
            }

            // Never place a solid block inside the player or another entity.
            if (def.Solid)
            {
                var bmin = (Vector3)p; var bmax = bmin + Vector3.one;
                var pp = Player.transform.position;
                var pmin = new Vector3(pp.x - PlayerController.HalfWidth, pp.y, pp.z - PlayerController.HalfWidth);
                var pmax = new Vector3(pp.x + PlayerController.HalfWidth, pp.y + Player.CurrentHeight, pp.z + PlayerController.HalfWidth);
                if (pmin.x < bmax.x - 0.001f && pmax.x > bmin.x + 0.001f && pmin.y < bmax.y - 0.001f && pmax.y > bmin.y + 0.001f &&
                    pmin.z < bmax.z - 0.001f && pmax.z > bmin.z + 0.001f) return false;
                if (EntityBlocks != null && EntityBlocks(bmin, bmax)) return false;
            }

            byte meta = 0;
            if (def.LFront != def.LSide)
            {
                // Face the player: 0 +Z, 1 +X, 2 -Z, 3 -X (front points back toward the player).
                var f = -Player.LookDir; f.y = 0;
                meta = Mathf.Abs(f.x) > Mathf.Abs(f.z) ? (byte)(f.x > 0 ? 1 : 3) : (byte)(f.z > 0 ? 0 : 2);
            }
            if (!w.SetBlock(p.x, p.y, p.z, id, meta)) return false;
            inv.ConsumeSelected();
            Swing?.Invoke();
            mView.Swing();
            BlockPlaced?.Invoke(def, p);
            return true;
        }

        /// <summary>Wildlife/other entities register a box test here so blocks are never placed inside them.</summary>
        public static Func<Vector3, Vector3, bool> EntityBlocks;

        // ------------------------------------------------------------------ visuals

        void UpdateOutline()
        {
            if (!Target.Hit) { mOutline.SetActive(false); return; }
            mOutline.SetActive(true);
            var shape = Blocks.ShapeOf[Target.Id];
            if (shape == RenderShape.Cross)
            {
                mOutline.transform.position = Target.Block + new Vector3(0.2f, 0, 0.2f);
                mOutline.transform.localScale = new Vector3(0.6f, 0.8f, 0.6f);
            }
            else
            {
                mOutline.transform.position = Target.Block;
                mOutline.transform.localScale = Vector3.one;
            }
        }

        void UpdateCrack()
        {
            bool show = MineProgress > 0.02f && Target.Hit && Blocks.ShapeOf[Target.Id] != RenderShape.Cross;
            mCrack.SetActive(show);
            if (!show) return;
            mCrack.transform.position = Target.Block + new Vector3(0.5f, 0.5f, 0.5f);
            mCrackMat.SetFloat("_Stage", Mathf.Clamp(Mathf.FloorToInt(MineProgress * 8f), 0, 7));
        }

        static GameObject BuildOutline()
        {
            var go = new GameObject("BlockOutline");
            var mesh = new Mesh { name = "outline" };
            const float e = 0.006f, t = 0.012f; // expand, edge thickness
            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            void Box(Vector3 a, Vector3 b)
            {
                int o = verts.Count;
                for (int i = 0; i < 8; i++) verts.Add(new Vector3((i & 1) == 0 ? a.x : b.x, (i & 2) == 0 ? a.y : b.y, (i & 4) == 0 ? a.z : b.z));
                int[] f = { 0,2,3,0,3,1, 4,5,7,4,7,6, 0,1,5,0,5,4, 2,6,7,2,7,3, 0,4,6,0,6,2, 1,3,7,1,7,5 };
                foreach (var i in f) tris.Add(o + i);
            }
            float lo = -e, hi = 1 + e;
            for (int i = 0; i < 4; i++)
            {
                float x = (i & 1) == 0 ? lo : hi, z = (i & 2) == 0 ? lo : hi;
                Box(new Vector3(x - t, lo, z - t), new Vector3(x + t, hi, z + t));           // vertical edges
                float y = (i & 1) == 0 ? lo : hi, zz = (i & 2) == 0 ? lo : hi;
                Box(new Vector3(lo, y - t, zz - t), new Vector3(hi, y + t, zz + t));          // edges along X
                float xx = (i & 1) == 0 ? lo : hi, yy = (i & 2) == 0 ? lo : hi;
                Box(new Vector3(xx - t, yy - t, lo), new Vector3(xx + t, yy + t, hi));        // edges along Z
            }
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            var sh = Shader.Find("Wildmere/Outline");
            r.sharedMaterial = new Material(sh != null ? sh : Shader.Find("Universal Render Pipeline/Unlit")) { name = "Outline" };
            r.sharedMaterial.SetColor("_Color", new Color(0.02f, 0.02f, 0.03f, 0.6f));
            go.SetActive(false);
            return go;
        }

        GameObject BuildCrack()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "CrackOverlay";
            Destroy(go.GetComponent<Collider>());
            go.transform.localScale = Vector3.one * 1.004f;
            var r = go.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var sh = Shader.Find("Wildmere/Crack");
            mCrackMat = new Material(sh != null ? sh : Shader.Find("Universal Render Pipeline/Unlit")) { name = "Crack" };
            mCrackMat.SetTexture("_CrackTex", BlockTextures.Crack);
            r.sharedMaterial = mCrackMat;
            go.SetActive(false);
            return go;
        }
    }
}
