using System.Collections.Generic;
using UnityEngine;
using Wildmere.World;

namespace Wildmere.Items
{
    /// <summary>
    /// A dropped item stack floating in the world: falls with voxel collision, bobs and spins, merges
    /// with nearby identical drops and is pulled into the player's inventory when close.
    /// </summary>
    public sealed class ItemDrop : MonoBehaviour
    {
        public static readonly List<ItemDrop> All = new List<ItemDrop>();
        public static event System.Action<ItemDrop> PickedUp;

        public ItemStack Stack;
        public Vector3 Velocity;
        public float Age;
        public float PickupDelay = 0.6f;
        const float kHalf = 0.12f, kLifetime = 300f;

        Transform mVisual;
        float mSpinPhase;

        public static ItemDrop Spawn(ItemStack stack, Vector3 pos, Vector3 velocity, float pickupDelay = 0.6f)
        {
            if (stack.IsEmpty) return null;
            var go = new GameObject("Drop " + ItemDb.NameOf(stack.Id));
            go.layer = Layers.Entity;
            var d = go.AddComponent<ItemDrop>();
            d.Stack = stack;
            d.Velocity = velocity;
            d.PickupDelay = pickupDelay;
            go.transform.position = pos;
            d.mSpinPhase = Random.value * 360f;
            d.BuildVisual();
            All.Add(d);
            return d;
        }

        void OnDestroy() => All.Remove(this);

        void BuildVisual()
        {
            var v = new GameObject("Visual");
            v.layer = Layers.Entity;
            v.transform.SetParent(transform, false);
            mVisual = v.transform;
            var def = ItemDb.Get(Stack.Id);
            if (def != null && def.PlacesBlock != 0)
            {
                byte b = def.PlacesBlock;
                v.AddComponent<MeshFilter>().sharedMesh = BlockMeshes.Get(b);
                var r = v.AddComponent<MeshRenderer>();
                r.sharedMaterial = BlockMeshes.MaterialFor(b);
                v.transform.localScale = Vector3.one * (Blocks.IsPlant(b) ? 0.4f : 0.26f);
            }
            else
            {
                ItemVisuals.Attach(v, Stack.Id, 0.42f);
            }
        }

        void Update()
        {
            var w = VoxelWorld.Instance;
            if (w == null) return;
            float dt = Time.deltaTime;
            Age += dt;
            if (Age > kLifetime) { Destroy(gameObject); return; }

            var pos = transform.position;
            bool inWater = w.GetBlock(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y + 0.1f), Mathf.FloorToInt(pos.z)) == B.Water;
            Velocity.y -= (inWater ? 4f : 22f) * dt;
            if (inWater) Velocity *= 1f - Mathf.Min(1f, 2.5f * dt);
            var feet = pos - new Vector3(0, kHalf, 0);
            var blocked = VoxelPhysics.Move(w, ref feet, Velocity * dt, kHalf, kHalf * 2f);
            if (blocked.y != 0) { Velocity.y = 0; Velocity.x *= 1f - Mathf.Min(1f, 8f * dt); Velocity.z *= 1f - Mathf.Min(1f, 8f * dt); }
            if (blocked.x != 0) Velocity.x = 0;
            if (blocked.z != 0) Velocity.z = 0;
            transform.position = feet + new Vector3(0, kHalf, 0);

            // Stuck inside a block (e.g. a block was placed on it): pop upward.
            if (VoxelPhysics.Overlaps(w, feet - new Vector3(kHalf, 0, kHalf), feet + new Vector3(kHalf, kHalf * 2, kHalf)))
                transform.position += Vector3.up * 2f * dt;

            mVisual.localPosition = new Vector3(0, 0.12f + Mathf.Sin(Age * 2.4f + mSpinPhase) * 0.06f, 0);
            mVisual.localRotation = Quaternion.Euler(0, mSpinPhase + Age * 70f, 0);

            // Merge with neighbours of the same kind.
            if (Stack.Def != null && Stack.Def.MaxStack > 1 && Time.frameCount % 20 == (GetInstanceID() & 15))
                foreach (var o in All)
                {
                    if (o == this || o.Stack.Id != Stack.Id || o.Age > Age) continue;
                    if ((o.transform.position - transform.position).sqrMagnitude > 1.2f) continue;
                    int room = Stack.Def.MaxStack - Stack.Count;
                    if (room <= 0) break;
                    int n = Mathf.Min(room, o.Stack.Count);
                    Stack.Count += n; o.Stack.Count -= n;
                    if (o.Stack.Count <= 0) Destroy(o.gameObject);
                    break;
                }
        }

        /// <summary>Try to move this drop into an inventory (called by the player). Returns true when fully collected.</summary>
        public bool TryCollect(Inventory inv)
        {
            if (Age < PickupDelay) return false;
            int left = inv.Add(Stack.Id, Stack.Count, Stack.Def != null && Stack.Def.Durability > 0 ? Stack.Durability : -1);
            if (left == Stack.Count) return false;
            Stack.Count = left;
            PickedUp?.Invoke(this);
            if (left <= 0) { Destroy(gameObject); return true; }
            return false;
        }
    }

    /// <summary>Visuals for non-block items: Blender model if one exists, otherwise a camera-facing icon sprite.</summary>
    public static class ItemVisuals
    {
        static readonly Dictionary<int, GameObject> sModels = new Dictionary<int, GameObject>();
        static readonly Dictionary<int, Sprite> sIcons = new Dictionary<int, Sprite>();

        public static Sprite Icon(int id)
        {
            if (sIcons.TryGetValue(id, out var s)) return s;
            var def = ItemDb.Get(id);
            if (def?.Icon != null)
            {
                s = Resources.Load<Sprite>(def.Icon);
                if (s == null)
                {
                    var t = Resources.Load<Texture2D>(def.Icon);
                    if (t != null) s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width);
                }
            }
            sIcons[id] = s;
            return s;
        }

        public static GameObject Model(int id)
        {
            if (sModels.TryGetValue(id, out var m)) return m;
            var def = ItemDb.Get(id);
            m = def?.Model != null ? Resources.Load<GameObject>(def.Model) : null;
            sModels[id] = m;
            return m;
        }

        public static void Attach(GameObject parent, int id, float size)
        {
            var model = Model(id);
            if (model != null)
            {
                var inst = Object.Instantiate(model, parent.transform, false);
                inst.transform.localScale = Vector3.one * (size * 1.4f);
                inst.transform.localRotation = Quaternion.Euler(0, 0, 45f);
                foreach (var r in inst.GetComponentsInChildren<Renderer>()) r.sharedMaterial = PropMaterials.ForModel(r.sharedMaterial);
                SetLayer(inst, parent.layer);
                return;
            }
            var sr = parent.AddComponent<SpriteRenderer>();
            sr.sprite = Icon(id);
            sr.sharedMaterial = PropMaterials.Sprite;
            parent.transform.localScale = Vector3.one * size;
            parent.AddComponent<Billboard>();
        }

        static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayer(t.gameObject, layer);
        }
    }

    public sealed class Billboard : MonoBehaviour
    {
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, Vector3.up);
        }
    }
}
