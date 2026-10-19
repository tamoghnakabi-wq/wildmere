using UnityEngine;
using Wildmere.Items;
using Wildmere.World;

namespace Wildmere.Player
{
    /// <summary>
    /// The held item in first person: a mini block or the Blender tool model, with sway from mouse
    /// movement, walk bob and a swing animation for mining/placing.
    /// </summary>
    public sealed class Viewmodel : MonoBehaviour
    {
        public PlayerController Player;
        Transform mRoot, mHolder;
        int mShownId = -1;
        float mSwing = 1f;
        Vector2 mSway;
        float mEquip = 1f;

        static readonly Vector3 kRest = new Vector3(0.36f, -0.34f, 0.62f);

        void Awake()
        {
            mRoot = new GameObject("Viewmodel").transform;
            mRoot.SetParent(transform, false);
            mRoot.localPosition = kRest;
            mHolder = new GameObject("Held").transform;
            mHolder.SetParent(mRoot, false);
        }

        public void Swing() { mSwing = 0f; }

        public void Refresh()
        {
            if (Player == null) return;
            var s = Player.Inventory.SelectedStack;
            int id = s.IsEmpty ? 0 : s.Id;
            if (id == mShownId) return;
            mShownId = id;
            mEquip = 0f;
            for (int i = mHolder.childCount - 1; i >= 0; i--) Destroy(mHolder.GetChild(i).gameObject);
            if (id == 0) return;

            var def = ItemDb.Get(id);
            var go = new GameObject("Item");
            go.layer = Layers.Viewmodel;
            go.transform.SetParent(mHolder, false);
            if (def.PlacesBlock != 0)
            {
                byte b = def.PlacesBlock;
                go.AddComponent<MeshFilter>().sharedMesh = BlockMeshes.Get(b);
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = BlockMeshes.MaterialFor(b);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                // Plant textures fill the whole tile, so held plants need a smaller scale than cubes to read right.
                bool plant = Blocks.IsPlant(b) && b != B.Torch;
                go.transform.localScale = Vector3.one * (plant ? 0.15f : 0.26f);
                go.transform.localRotation = Quaternion.Euler(8f, 38f, 0f);
                if (plant) go.transform.localPosition = new Vector3(0.04f, -0.05f, 0.02f);
            }
            else
            {
                var model = ItemVisuals.Model(id);
                if (model != null)
                {
                    var inst = Instantiate(model, go.transform, false);
                    foreach (var r in inst.GetComponentsInChildren<Renderer>())
                    {
                        r.sharedMaterial = PropMaterials.ForModel(r.sharedMaterial);
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.gameObject.layer = Layers.Viewmodel;
                    }
                    // Grip at origin, long axis +Y: tilt it forward like a held tool.
                    go.transform.localScale = Vector3.one * 0.34f;
                    go.transform.localRotation = Quaternion.Euler(-28f, -18f, 10f);
                    go.transform.localPosition = new Vector3(0.06f, -0.04f, 0.02f);
                }
                else
                {
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = ItemVisuals.Icon(id);
                    sr.sharedMaterial = PropMaterials.Sprite;
                    go.transform.localScale = Vector3.one * 0.32f;
                    go.transform.localRotation = Quaternion.Euler(0, -25f, 0);
                }
            }
        }

        void LateUpdate()
        {
            if (Player == null) return;
            float dt = Time.deltaTime;
            if (mShownId != (Player.Inventory.SelectedStack.IsEmpty ? 0 : Player.Inventory.SelectedStack.Id)) Refresh();

            // Sway against mouse motion.
            var target = new Vector2(-Input.GetAxisRaw("Mouse X"), -Input.GetAxisRaw("Mouse Y")) * 0.012f;
            target = Vector2.ClampMagnitude(target, 0.05f);
            mSway = Vector2.Lerp(mSway, target, 1f - Mathf.Exp(-10f * dt));

            mSwing = Mathf.Min(1f, mSwing + dt / 0.26f);
            mEquip = Mathf.Min(1f, mEquip + dt / 0.22f);
            float sw = Mathf.Sin(mSwing * Mathf.PI);
            float hs = new Vector2(Player.Velocity.x, Player.Velocity.z).magnitude;
            float bob = Player.Grounded ? Mathf.Sin(Time.time * 9f) * Mathf.Clamp01(hs / 6f) * 0.012f : 0f;

            mRoot.localPosition = kRest + new Vector3(mSway.x - sw * 0.10f, mSway.y + bob - sw * 0.08f - (1f - mEquip) * 0.35f, sw * 0.16f);
            mRoot.localRotation = Quaternion.Euler(sw * 38f, -sw * 18f, sw * 12f);
        }
    }
}
