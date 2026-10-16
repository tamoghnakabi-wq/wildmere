using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Wildmere.Core
{
    /// <summary>
    /// Command-line flags for the shipping binary (also the test harness):
    ///   -selftest            scripted gameplay checks, exit code 0 = pass
    ///   -shots 5,12 -shotdir D   screenshots at those seconds after the world is ready
    ///   -quitafter S         quit S seconds after start
    ///   -bench S             fly a fixed path for S seconds and print WM_PERF lines
    ///   -seed N -world NAME -new   start straight into a world (new or existing by name)
    ///   -time 0..1 -viewdist N -preset low|medium|high|ultra -flycam -camat x,y,z -camlook x,y,z
    ///   -nosave              never write the world to disk
    /// </summary>
    public static class Cli
    {
        static Dictionary<string, string> sArgs;

        static void Parse()
        {
            if (sArgs != null) return;
            sArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var a = Environment.GetCommandLineArgs();
            for (int i = 1; i < a.Length; i++)
            {
                if (!a[i].StartsWith("-") || IsNumber(a[i])) continue;
                string key = a[i].TrimStart('-');
                string val = i + 1 < a.Length && (!a[i + 1].StartsWith("-") || IsNumber(a[i + 1])) ? a[++i] : "";
                sArgs[key] = val;
            }
        }

        // "-8,3,-46" or "-0.5" are values, not flags.
        static bool IsNumber(string s) => s.Length > 1 && s[0] == '-' && (char.IsDigit(s[1]) || s[1] == '.');

        public static bool Has(string key) { Parse(); return sArgs.ContainsKey(key); }
        public static string Str(string key, string fallback = null) { Parse(); return sArgs.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback; }

        public static float Float(string key, float fallback)
        {
            var s = Str(key);
            return s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
        }

        public static long Long(string key, long fallback)
        {
            var s = Str(key);
            return s != null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : fallback;
        }

        public static Vector3? Vec(string key)
        {
            var s = Str(key);
            if (s == null) return null;
            var p = s.Split(',');
            if (p.Length != 3) return null;
            return new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture),
                float.Parse(p[2], CultureInfo.InvariantCulture));
        }

        public static List<float> Floats(string key)
        {
            var l = new List<float>();
            var s = Str(key);
            if (s == null) return l;
            foreach (var p in s.Split(','))
                if (float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) l.Add(f);
            return l;
        }
    }
}
