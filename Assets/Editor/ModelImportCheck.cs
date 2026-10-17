using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>
/// Batch verification of every model under Assets/Resources/Models.
///   tools/unity.sh ModelImportCheck.Run
/// Prints one "WM_MODEL ..." line per asset, "WM_MODEL_FAIL &lt;name&gt; &lt;reason&gt;" for each problem and a
/// final "WM_MODEL_SUMMARY"; exits 0 only when everything passes.
/// </summary>
public static class ModelImportCheck
{
    static readonly HashSet<string> Surfaces = new HashSet<string>
    {
        "M_Fur", "M_Horn", "M_Wood", "M_Bark", "M_Stone", "M_Metal", "M_Cloth", "M_Leaves", "M_Cap",
        "M_Glow", "M_Flame", "M_Ice",
    };

    static readonly string[] Cervid = { "Body", "Neck", "Head", "EarL", "EarR", "LegFL", "LegFR", "LegBL", "LegBR", "Tail" };
    static readonly string[] Bird = { "Body", "Head", "WingL", "WingR", "Tail" };
    static readonly Dictionary<string, string[]> RequiredParts = new Dictionary<string, string[]>
    {
        { "Deer", Cervid },
        { "Doe", Cervid },
        { "Rabbit", new[] { "Body", "Head", "EarL", "EarR", "LegFL", "LegFR", "LegBL", "LegBR", "Tail" } },
        { "Songbird_Blue", Bird },
        { "Songbird_Red", Bird },
    };

    static readonly string[] Expected =
    {
        "Wildlife/Deer", "Wildlife/Doe", "Wildlife/Rabbit", "Wildlife/Songbird_Blue", "Wildlife/Songbird_Red",
        "Tools/Pickaxe_Wood", "Tools/Pickaxe_Stone", "Tools/Pickaxe_Iron", "Tools/Axe_Wood", "Tools/Axe_Stone",
        "Tools/Axe_Iron", "Tools/Shovel_Wood", "Tools/Shovel_Stone", "Tools/Shovel_Iron", "Tools/Torch",
        "Props/Boulder_A", "Props/Boulder_B", "Props/Boulder_C", "Props/Rocks_Small_A", "Props/Rocks_Small_B",
        "Props/FallenLog_Oak", "Props/FallenLog_Birch", "Props/Stump", "Props/MushroomCluster",
        "Props/GiantMushroom", "Props/GlowcapCluster", "Props/SandstonePillar", "Props/DeadTree",
        "Props/IceSpike", "Props/Bush_A", "Props/Bush_B", "Props/Reeds", "Props/LilyPad",
    };

    static int Budget(string category, string name)
    {
        if (category == "Wildlife") return name.StartsWith("Songbird") ? 600 : 2500;
        if (category == "Tools") return 800;
        return 1500;
    }

    static void Log(string s)
    {
        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", s);
    }

    public static void Run()
    {
        int failed = 0, total = 0;
        try
        {
            string manifest = File.Exists("Assets/Resources/Models/models.json")
                ? File.ReadAllText("Assets/Resources/Models/models.json") : null;
            if (manifest == null) { Log("WM_MODEL_FAIL models.json missing"); failed++; }

            var paths = AssetDatabase.FindAssets("t:Model", new[] { "Assets/Resources/Models" })
                .Select(AssetDatabase.GUIDToAssetPath).Where(ModelPostprocessor.IsModel).OrderBy(p => p).ToList();
            foreach (var p in paths)
            {
                var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                if (!ModelPostprocessor.Matches(mi))
                {
                    // Imported before the postprocessor existed (or settings drifted): re-import once.
                    ModelPostprocessor.Apply(mi);
                    mi.SaveAndReimport();
                    mi = (ModelImporter)AssetImporter.GetAtPath(p);
                }
                if (!ModelPostprocessor.Matches(mi)) { Log("WM_MODEL_FAIL " + p + " importer settings not applied"); failed++; }
            }

            var seen = new HashSet<string>();
            foreach (var p in paths)
            {
                total++;
                string rel = p.Substring(ModelPostprocessor.ModelRoot.Length);          // Category/Name.fbx
                string category = rel.Split('/')[0];
                string name = Path.GetFileNameWithoutExtension(p);
                seen.Add(category + "/" + name);
                var fails = CheckModel(p, category, name, manifest);
                foreach (var f in fails) Log("WM_MODEL_FAIL " + name + " " + f);
                if (fails.Count > 0) failed++;
            }
            foreach (var e in Expected)
                if (!seen.Contains(e)) { Log("WM_MODEL_FAIL " + e + " missing FBX"); failed++; }
        }
        catch (Exception ex)
        {
            Log("WM_MODEL_FAIL exception " + ex);
            failed++;
        }
        Log(string.Format("WM_MODEL_SUMMARY assets={0} failed={1} result={2}", total, failed, failed == 0 ? "PASS" : "FAIL"));
        EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    struct Info { public Bounds b; public bool has; }

    static Bounds WorldBounds(MeshFilter mf)
    {
        var m = mf.sharedMesh.bounds;
        var M = mf.transform.localToWorldMatrix;
        var b = new Bounds(M.MultiplyPoint3x4(m.center), Vector3.zero);
        for (int i = 0; i < 8; i++)
        {
            var c = m.center + Vector3.Scale(m.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            b.Encapsulate(M.MultiplyPoint3x4(c));
        }
        return b;
    }

    static Bounds PartBounds(Transform t)
    {
        var mf = t ? t.GetComponent<MeshFilter>() : null;
        return mf && mf.sharedMesh ? WorldBounds(mf) : new Bounds();
    }

    static Transform Find(Transform root, string n)
    {
        if (root.name == n) return root;
        foreach (Transform c in root)
        {
            var r = Find(c, n);
            if (r) return r;
        }
        return null;
    }

    /// <summary>Accumulates vertex-colour luminance (sum, sum of squares, count). False if unreadable.</summary>
    static bool ColourStats(Mesh mesh, ref double s, ref double s2, ref int n, out float meshSd)
    {
        meshSd = -1f;
        try
        {
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                var md = data[0];
                if (!md.HasVertexAttribute(VertexAttribute.Color)) return false;
                var cols = new Unity.Collections.NativeArray<Color>(md.vertexCount, Unity.Collections.Allocator.Temp);
                md.GetColors(cols);
                double ms = 0, ms2 = 0;
                for (int i = 0; i < cols.Length; i++)
                {
                    double l = 0.2126 * cols[i].r + 0.7152 * cols[i].g + 0.0722 * cols[i].b;
                    ms += l; ms2 += l * l;
                }
                int c = Math.Max(cols.Length, 1);
                meshSd = (float)Math.Sqrt(Math.Max(0, ms2 / c - (ms / c) * (ms / c)));
                s += ms; s2 += ms2; n += cols.Length;
                cols.Dispose();
                return true;
            }
        }
        catch (Exception) { return false; }
    }

    static List<string> CheckModel(string path, string category, string name, string manifest)
    {
        var fails = new List<string>();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (!prefab) { fails.Add("cannot load prefab"); return fails; }
        var go = (GameObject)Object.Instantiate(prefab);
        go.name = prefab.name;
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        go.transform.localScale = Vector3.one;
        try
        {
            if (manifest != null && !manifest.Contains("\"" + name + "\"")) fails.Add("not listed in models.json");
            var filters = go.GetComponentsInChildren<MeshFilter>(true);
            var lod0 = filters.Where(f => !f.name.EndsWith("_LOD1")).ToList();
            var lod1 = filters.Where(f => f.name.EndsWith("_LOD1")).ToList();
            if (lod0.Count == 0) { fails.Add("no meshes"); return fails; }

            int tris0 = 0, tris1 = 0;
            bool colours = true;
            double cs = 0, cs2 = 0;
            int cn = 0;
            var mats = new SortedSet<string>();
            foreach (var f in filters)
            {
                var mesh = f.sharedMesh;
                int t = 0;
                for (int s = 0; s < mesh.subMeshCount; s++) t += (int)mesh.GetIndexCount(s) / 3;
                if (f.name.EndsWith("_LOD1")) tris1 += t; else tris0 += t;
                if (!mesh.HasVertexAttribute(VertexAttribute.Color)) { colours = false; fails.Add("no vertex colours on " + f.name); }
                else
                {
                    float msd;
                    bool lod = f.name.EndsWith("_LOD1");
                    double a = 0, a2 = 0; int an = 0;
                    if (ColourStats(mesh, ref a, ref a2, ref an, out msd))
                    {
                        if (!lod) { cs += a; cs2 += a2; cn += an; }
                        if (msd < 0.002f) fails.Add("flat single-colour fill on " + f.name);
                    }
                }
                var r = f.GetComponent<MeshRenderer>();
                var sm = r ? r.sharedMaterials : new Material[0];
                if (sm.Length != mesh.subMeshCount) fails.Add(f.name + " has " + sm.Length + " materials for " + mesh.subMeshCount + " submeshes");
                foreach (var m in sm)
                {
                    if (!m) { fails.Add(f.name + " null material"); continue; }
                    mats.Add(m.name);
                    if (!Surfaces.Contains(m.name)) fails.Add(f.name + " material '" + m.name + "' not an M_<Surface> name");
                }
            }
            float spread = cn > 0 ? (float)Math.Sqrt(Math.Max(0, cs2 / cn - (cs / cn) * (cs / cn))) : float.PositiveInfinity;
            if (cn > 0 && spread < 0.03f) fails.Add(string.Format("vertex colours too flat (luma sd {0:F3})", spread));

            var b = WorldBounds(lod0[0]);
            foreach (var f in lod0) b.Encapsulate(WorldBounds(f));

            int budget = Budget(category, name);
            if (tris0 > budget) fails.Add(string.Format("tris {0} over budget {1}", tris0, budget));
            if (category != "Tools")
            {
                if (lod1.Count == 0) fails.Add("no _LOD1 meshes");
                else
                {
                    float ratio = tris1 / (float)tris0;
                    if (ratio < 0.25f || ratio > 0.6f) fails.Add(string.Format("LOD1 ratio {0:F2} outside 0.25-0.60", ratio));
                }
                if (Mathf.Abs(b.min.y) > 0.02f) fails.Add(string.Format("origin not on the ground (min.y={0:F3})", b.min.y));
                float cx = b.center.x, cz = b.center.z;
                float half = Mathf.Max(b.extents.x, b.extents.z);
                if (Mathf.Abs(cx) > 0.25f * half + 0.02f || Mathf.Abs(cz) > 0.25f * half + 0.02f)
                    fails.Add(string.Format("origin not centred on the footprint (centre {0:F2},{1:F2})", cx, cz));
            }

            // Transforms must carry no rotation/scale (exporter baked the axis conversion).
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t == go.transform) continue;
                if (Quaternion.Angle(t.localRotation, Quaternion.identity) > 0.05f) fails.Add(t.name + " has local rotation " + t.localEulerAngles);
                if ((t.localScale - Vector3.one).sqrMagnitude > 1e-6f) fails.Add(t.name + " has local scale " + t.localScale);
            }

            if (category == "Wildlife") CheckAnimal(go.transform, name, fails);
            if (category == "Tools") CheckTool(go.transform, name, b, fails);
            if (category == "Tools" || category == "Wildlife")
            {
                var icon = AssetDatabase.LoadAssetAtPath<Sprite>(ModelPostprocessor.IconRoot + name + ".png");
                if (!icon) fails.Add("icon missing or not imported as Sprite");
                else if (icon.texture.width != 256 || icon.texture.height != 256) fails.Add("icon not 256 px");
            }

            var parts = go.GetComponentsInChildren<Transform>(true)
                .Where(t => t != go.transform && !t.name.EndsWith("_LOD1")).Select(t => t.name).ToList();
            Log(string.Format("WM_MODEL {0} tris={1} lod1={2} size={3:F2}x{4:F2}x{5:F2} min.y={6:F3} colors={7} colsd={8} mats={9} parts={10}",
                name, tris0, tris1, b.size.x, b.size.y, b.size.z, b.min.y, colours ? "yes" : "no",
                cn == 0 ? "n/a" : spread.ToString("F3"),
                string.Join(",", mats), parts.Count > 0 ? string.Join(",", parts) : "-"));
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
        return fails;
    }

    static void CheckAnimal(Transform root, string name, List<string> fails)
    {
        string[] req;
        if (!RequiredParts.TryGetValue(name, out req)) { fails.Add("no required-part list for " + name); return; }
        foreach (var p in req)
        {
            var t = Find(root, p);
            if (!t) { fails.Add("missing part " + p); continue; }
            if (!t.GetComponent<MeshFilter>()) fails.Add(p + " has no mesh");
            if (!t.Find(p + "_LOD1")) fails.Add(p + " has no " + p + "_LOD1 child");
        }
        var body = Find(root, "Body");
        var head = Find(root, "Head");
        if (!body || !head) return;
        var bb = PartBounds(body);
        var hb = PartBounds(head);
        // Facing +Z: head in front of the body centre.
        if (hb.center.z < bb.center.z + 0.25f * bb.extents.z) fails.Add(string.Format("head not toward +Z (head z {0:F2}, body z {1:F2})", hb.center.z, bb.center.z));
        var tail = Find(root, "Tail");
        if (tail && PartBounds(tail).center.z > bb.center.z) fails.Add("tail not toward -Z");
        // Animal's LEFT side is -X in Unity (facing +Z, +Y up, X right).
        var earL = Find(root, "EarL"); var earR = Find(root, "EarR");
        if (earL && earR && !(earL.position.x < 0 && earR.position.x > 0)) fails.Add("EarL/EarR sides swapped");
        var legFL = Find(root, "LegFL"); var legBL = Find(root, "LegBL"); var legFR = Find(root, "LegFR");
        if (legFL && legFR && !(legFL.position.x < 0 && legFR.position.x > 0)) fails.Add("LegFL/LegFR sides swapped");
        if (legFL && legBL && !(legFL.position.z > legBL.position.z)) fails.Add("front legs not toward +Z");
        var wingL = Find(root, "WingL"); var wingR = Find(root, "WingR");
        if (wingL && wingR && !(wingL.position.x < 0 && wingR.position.x > 0)) fails.Add("WingL/WingR sides swapped");
        // Joint pivots: legs pivot near their top, not at the mesh centre.
        foreach (var ln in new[] { "LegFL", "LegFR", "LegBL", "LegBR" })
        {
            var l = Find(root, ln);
            if (!l || name.StartsWith("Songbird")) continue;
            var lb = PartBounds(l);
            if (l.position.y < lb.center.y) fails.Add(ln + " pivot below its mesh centre (expected at the hip/shoulder)");
        }
    }

    static void CheckTool(Transform root, string name, Bounds b, List<string> fails)
    {
        // Grip at the origin, long axis +Y.
        if (!(b.size.y > b.size.x && b.size.y > b.size.z)) fails.Add("long axis is not +Y");
        if (!(b.min.y < -0.03f && b.min.y > -0.25f)) fails.Add(string.Format("grip origin wrong: butt at y={0:F3} (expected -0.25..-0.03)", b.min.y));
        if (b.max.y < 0.3f) fails.Add("tool shorter than 0.3 m above the grip");
        if (Mathf.Abs(b.center.x) > 0.05f) fails.Add("tool not centred on X");
        var handle = Find(root, "Handle");
        if (!handle) { fails.Add("missing Handle"); return; }
        if (handle.position.sqrMagnitude > 1e-6f) fails.Add("Handle pivot not at the grip origin");
        var hbx = PartBounds(handle);
        if (!(hbx.min.x < 0 && hbx.max.x > 0 && hbx.min.z < 0 && hbx.max.z > 0)) fails.Add("origin not inside the handle");
        if (name == "Torch")
        {
            var flame = Find(root, "Flame");
            if (!flame) { fails.Add("missing Flame child"); return; }
            var fr = flame.GetComponent<MeshRenderer>();
            if (!fr || fr.sharedMaterials.Length != 1 || !fr.sharedMaterials[0] || fr.sharedMaterials[0].name != "M_Flame") fails.Add("Flame must use only M_Flame");
            if (PartBounds(flame).min.y < hbx.max.y - 0.08f) fails.Add("Flame not at the top of the torch");
            return;
        }
        var head = Find(root, "Head");
        if (!head) { fails.Add("missing Head"); return; }
        var hb = PartBounds(head);
        if (hb.center.y < 0.55f * b.max.y) fails.Add("Head not at the top (+Y) end");
        if (name.StartsWith("Pickaxe") || name.StartsWith("Axe"))
        {
            if (hb.max.z < 1.15f * -hb.min.z) fails.Add(string.Format("working edge not toward +Z (head z {0:F2}..{1:F2})", hb.min.z, hb.max.z));
        }
        else if (name.StartsWith("Shovel"))
        {
            if (hb.center.z < 0.003f) fails.Add(string.Format("blade face not toward +Z (head centre z {0:F3})", hb.center.z));
        }
    }
}
