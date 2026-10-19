using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using Wildmere.Core;

namespace Wildmere.World
{
    /// <summary>
    /// Versioned world persistence. A world folder holds:
    ///   world.json  — metadata, seed, settings, time, weather, player state, inventory (pretty JSON)
    ///   mods.bin    — gzip'd block modifications only (deltas from the deterministic generator)
    /// The generated terrain itself is never written: it is recreated from the seed.
    /// Writes go to *.tmp first and are swapped in, so a crash mid-save cannot corrupt a world.
    /// </summary>
    public sealed class WorldSave
    {
        public const int FormatVersion = 1;
        const uint kMagic = 0x444F4D57; // "WMOD"

        public readonly string Dir;
        public Dictionary<string, object> Meta = new Dictionary<string, object>();

        // chunk key -> (block index -> block | meta << 8)
        readonly Dictionary<long, Dictionary<int, ushort>> mMods = new Dictionary<long, Dictionary<int, ushort>>();

        public int ModifiedChunks => mMods.Count;
        public int ModifiedBlocks { get { int n = 0; foreach (var m in mMods.Values) n += m.Count; return n; } }

        public static string Root => Path.Combine(Application.persistentDataPath, "Worlds");

        public WorldSave(string dir) { Dir = dir; }

        public string Name { get => Json.Str(Meta, "name", "World"); set => Meta["name"] = value; }

        /// <summary>Chosen on the New World screen (settings.gameMode); worlds from before 0.1.6 are survival.</summary>
        public bool Creative => IsCreative(Meta);
        public static bool IsCreative(Dictionary<string, object> meta) =>
            string.Equals(Json.Str(Json.Obj(meta, "settings"), "gameMode", "survival"), "creative", StringComparison.OrdinalIgnoreCase);
        public long Seed
        {
            get => Meta.TryGetValue("seed", out var s) ? (s is double d ? (long)d : s is string str && long.TryParse(str, out var l) ? l : 0) : 0;
            set => Meta["seed"] = value.ToString();
        }

        public static string Slug(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in name.ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
            var s = sb.ToString().Trim('-');
            if (string.IsNullOrEmpty(s)) return "world";
            // Windows reserves these device names for any file or folder.
            if (System.Text.RegularExpressions.Regex.IsMatch(s, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$")) s = "world-" + s;
            return s;
        }

        public static WorldSave Create(string name, long seed)
        {
            Directory.CreateDirectory(Root);
            string slug = Slug(name), dir = Path.Combine(Root, slug);
            for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(Root, $"{slug}-{i}");
            var s = new WorldSave(dir);
            s.Meta["version"] = (double)FormatVersion;
            s.Name = name;
            s.Seed = seed;
            s.Meta["created"] = DateTime.UtcNow.ToString("o");
            return s;
        }

        /// <summary>All saved worlds, most recently played first.</summary>
        public static List<WorldSave> List()
        {
            var list = new List<WorldSave>();
            if (!Directory.Exists(Root)) return list;
            foreach (var d in Directory.GetDirectories(Root))
            {
                var s = TryLoadMeta(d);
                if (s != null) list.Add(s);
            }
            list.Sort((a, b) => string.CompareOrdinal(Json.Str(b.Meta, "lastPlayed", ""), Json.Str(a.Meta, "lastPlayed", "")));
            return list;
        }

        public static WorldSave TryLoadMeta(string dir)
        {
            try
            {
                string p = Path.Combine(dir, "world.json");
                if (!File.Exists(p)) return null;
                var s = new WorldSave(dir) { Meta = Json.Parse(File.ReadAllText(p)) as Dictionary<string, object> };
                if (s.Meta == null) return null;
                int v = (int)Json.Num(s.Meta, "version", 0);
                if (v > FormatVersion) { Debug.LogWarning($"WM_SAVE world {dir} is from a newer version ({v})"); return null; }
                return s;
            }
            catch (Exception e) { Debug.LogWarning($"WM_SAVE unreadable world {dir}: {e.Message}"); return null; }
        }

        public void LoadMods()
        {
            mMods.Clear();
            string p = Path.Combine(Dir, "mods.bin");
            if (!File.Exists(p)) return;
            using (var fs = File.OpenRead(p))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            {
                if (r.ReadUInt32() != kMagic) throw new IOException("mods.bin: bad magic");
                int version = r.ReadInt32();
                if (version > FormatVersion) throw new IOException($"mods.bin: newer version {version}");
                int chunks = r.ReadInt32();
                for (int i = 0; i < chunks; i++)
                {
                    int cx = r.ReadInt32(), cz = r.ReadInt32(), n = r.ReadInt32();
                    var d = new Dictionary<int, ushort>(n);
                    for (int k = 0; k < n; k++) { int idx = r.ReadInt32(); ushort v = r.ReadUInt16(); d[idx] = v; }
                    mMods[Chunk.Key(cx, cz)] = d;
                }
            }
        }

        public void Record(Chunk c, int idx, byte block, byte meta)
        {
            long k = Chunk.Key(c.CX, c.CZ);
            if (!mMods.TryGetValue(k, out var d)) mMods[k] = d = new Dictionary<int, ushort>();
            d[idx] = (ushort)(block | (meta << 8));
        }

        /// <summary>Apply stored modifications to a freshly generated column. Returns true if anything changed.</summary>
        public bool ApplyTo(Chunk c)
        {
            if (!mMods.TryGetValue(Chunk.Key(c.CX, c.CZ), out var d) || d.Count == 0) return false;
            foreach (var kv in d)
            {
                c.Blocks[kv.Key] = (byte)(kv.Value & 0xFF);
                byte meta = (byte)(kv.Value >> 8);
                lock (c.Meta)
                {
                    if (meta != 0) c.Meta[kv.Key] = meta; else c.Meta.Remove(kv.Key);
                }
            }
            return true;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            Meta["version"] = (double)FormatVersion;
            Meta["lastPlayed"] = DateTime.UtcNow.ToString("o");
            WriteAtomic(Path.Combine(Dir, "world.json"), Encoding.UTF8.GetBytes(Json.Write(Meta, true)));

            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                using (var w = new BinaryWriter(gz))
                {
                    w.Write(kMagic);
                    w.Write(FormatVersion);
                    w.Write(mMods.Count);
                    foreach (var kv in mMods)
                    {
                        w.Write((int)(kv.Key >> 32));
                        w.Write((int)(uint)kv.Key);
                        w.Write(kv.Value.Count);
                        foreach (var e in kv.Value) { w.Write(e.Key); w.Write(e.Value); }
                    }
                }
                WriteAtomic(Path.Combine(Dir, "mods.bin"), ms.ToArray());
            }
        }

        static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public void Delete()
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }
}
