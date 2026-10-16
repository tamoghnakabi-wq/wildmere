using System;
using System.Collections.Generic;
using Wildmere.Core;

namespace Wildmere.Items
{
    [Serializable]
    public struct ItemStack
    {
        public int Id;
        public int Count;
        public int Durability;   // remaining uses for tools (0 for non-tools)

        public bool IsEmpty => Id == 0 || Count <= 0;
        public static readonly ItemStack Empty = default;
        public ItemDef Def => ItemDb.Get(Id);

        public static ItemStack Of(int id, int count)
        {
            var d = ItemDb.Get(id);
            return new ItemStack { Id = id, Count = count, Durability = d != null ? d.Durability : 0 };
        }
    }

    /// <summary>
    /// 36-slot player inventory; slots 0..8 are the hotbar. All mutation goes through these methods so
    /// UI and save code can rely on <see cref="Changed"/>.
    /// </summary>
    public sealed class Inventory
    {
        public const int Size = 36, Hotbar = 9;
        public readonly ItemStack[] Slots = new ItemStack[Size];
        public int Selected;
        public event System.Action Changed;

        public ItemStack SelectedStack => Slots[Selected];

        public void Notify() => Changed?.Invoke();

        public static int MaxStack(int id) => ItemDb.Get(id)?.MaxStack ?? 64;

        /// <summary>Add items, filling existing stacks first (hotbar first), then empty slots. Returns the leftover count.</summary>
        public int Add(int id, int count, int durability = -1)
        {
            if (id == 0 || count <= 0) return 0;
            int max = MaxStack(id);
            if (max > 1)
                for (int i = 0; i < Size && count > 0; i++)
                {
                    ref var s = ref Slots[i];
                    if (s.Id != id || s.Count >= max) continue;
                    int n = Math.Min(max - s.Count, count);
                    s.Count += n; count -= n;
                }
            for (int i = 0; i < Size && count > 0; i++)
            {
                ref var s = ref Slots[i];
                if (!s.IsEmpty) continue;
                int n = Math.Min(max, count);
                s = ItemStack.Of(id, n);
                if (durability >= 0) s.Durability = durability;
                count -= n;
            }
            Notify();
            return count;
        }

        public bool CanFit(int id, int count)
        {
            int max = MaxStack(id), room = 0;
            foreach (var s in Slots)
            {
                if (s.IsEmpty) room += max;
                else if (s.Id == id && max > 1) room += max - s.Count;
                if (room >= count) return true;
            }
            return room >= count;
        }

        public int Count(int id)
        {
            int n = 0;
            foreach (var s in Slots) if (s.Id == id) n += s.Count;
            return n;
        }

        /// <summary>Remove up to count items of id (from the back of the inventory first). Returns how many were removed.</summary>
        public int Remove(int id, int count)
        {
            int removed = 0;
            for (int i = Size - 1; i >= 0 && removed < count; i--)
            {
                ref var s = ref Slots[i];
                if (s.Id != id) continue;
                int n = Math.Min(s.Count, count - removed);
                s.Count -= n; removed += n;
                if (s.Count <= 0) s = ItemStack.Empty;
            }
            if (removed > 0) Notify();
            return removed;
        }

        /// <summary>Consume one of the selected item (placing a block). Returns false if empty.</summary>
        public bool ConsumeSelected()
        {
            ref var s = ref Slots[Selected];
            if (s.IsEmpty) return false;
            s.Count--;
            if (s.Count <= 0) s = ItemStack.Empty;
            Notify();
            return true;
        }

        /// <summary>Wear the selected tool by one use. Returns true if it broke.</summary>
        public bool DamageSelected()
        {
            ref var s = ref Slots[Selected];
            var d = s.Def;
            if (s.IsEmpty || d == null || d.Durability <= 0) return false;
            s.Durability--;
            bool broke = s.Durability <= 0;
            if (broke) s = ItemStack.Empty;
            Notify();
            return broke;
        }

        public void Swap(int a, int b)
        {
            (Slots[a], Slots[b]) = (Slots[b], Slots[a]);
            Notify();
        }

        public void Clear()
        {
            for (int i = 0; i < Size; i++) Slots[i] = ItemStack.Empty;
            Notify();
        }

        public List<object> ToJson()
        {
            var l = new List<object>();
            for (int i = 0; i < Size; i++)
            {
                var s = Slots[i];
                if (s.IsEmpty) continue;
                l.Add(new Dictionary<string, object> { { "slot", i }, { "id", s.Id }, { "count", s.Count }, { "dur", s.Durability } });
            }
            return l;
        }

        public void FromJson(List<object> l)
        {
            for (int i = 0; i < Size; i++) Slots[i] = ItemStack.Empty;
            if (l != null)
                foreach (var o in l)
                {
                    int slot = (int)Json.Num(o, "slot", -1), id = (int)Json.Num(o, "id");
                    if (slot < 0 || slot >= Size || !ItemDb.Exists(id)) continue;
                    Slots[slot] = new ItemStack { Id = id, Count = (int)Json.Num(o, "count", 1), Durability = (int)Json.Num(o, "dur") };
                }
            Notify();
        }
    }
}
