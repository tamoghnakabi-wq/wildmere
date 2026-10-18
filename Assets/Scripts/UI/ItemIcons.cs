using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Wildmere.Items;
using Wildmere.World;
using Debug = UnityEngine.Debug;

namespace Wildmere.UI
{
    /// <summary>
    /// Inventory icons. Every block item is rendered once at start-up as an isometric 3/4 cube (or a
    /// flat sprite for cross-shaped plants) straight from the voxel texture arrays; tool/item sprites
    /// from Resources are copied through the same pipeline. All end up in one mipmapped atlas with a
    /// soft baked drop shadow, so icons look identical in style and stay crisp at any UI scale.
    /// Items with no art fall back to a clean lettered glyph.
    /// </summary>
    public static class ItemIcons
    {
        const int kCell = 128, kCols = 8;
        static readonly Dictionary<int, Sprite> sSprites = new Dictionary<int, Sprite>();
        static Texture2D sAtlas;
        static bool sBuilt;

        public static Texture2D Atlas => sAtlas;
        public static int Count => sSprites.Count;

        /// <summary>Icon sprite for an item id, or null (use <see cref="Set"/> for the glyph fallback).</summary>
        public static Sprite Get(int id)
        {
            if (id == 0) return null;
            if (!sBuilt) Build();
            if (sSprites.TryGetValue(id, out var s)) return s;
            // Not in the atlas (e.g. no GPU): use the raw sprite if one exists.
            s = ItemVisuals.Icon(id);
            sSprites[id] = s;
            return s;
        }

        /// <summary>Show an item's icon on an element; falls back to its initials when no art exists.</summary>
        public static void Set(VisualElement el, int id)
        {
            var glyph = el.userData as Label;
            var sprite = id != 0 ? Get(id) : null;
            if (sprite != null)
            {
                el.style.backgroundImage = new StyleBackground(sprite);
                if (glyph != null) glyph.style.display = DisplayStyle.None;
                return;
            }
            el.style.backgroundImage = StyleKeyword.None;
            if (id == 0) { if (glyph != null) glyph.style.display = DisplayStyle.None; return; }
            if (glyph == null)
            {
                glyph = Ui.Text("", "slot__glyph").Ignore();
                el.Add(glyph);
                el.userData = glyph;
            }
            glyph.style.display = DisplayStyle.Flex;
            glyph.text = Initials(ItemDb.NameOf(id));
        }

        static string Initials(string name)
        {
            var parts = name.Split(' ');
            if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0) return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();
            return name.Length >= 2 ? name.Substring(0, 2) : name;
        }

        // ------------------------------------------------------------------ atlas build

        public static void Build()
        {
            if (sBuilt) return;
            sBuilt = true;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var sw = Stopwatch.StartNew();
            var shader = Resources.Load<Shader>("UI/Shaders/WildmereIcon");
            if (shader == null || !shader.isSupported) { Debug.LogWarning("WM_UI icon shader unavailable; using fallback glyphs"); return; }

            VoxelMaterials.Init(); // loads the block texture arrays (idempotent)
            var mat = new Material(shader) { hideFlags = HideFlags.DontSave };
            mat.SetTexture("_BlockAlbedo", BlockTextures.Albedo);
            mat.SetTexture("_BlockMask", BlockTextures.Mask);
            mat.SetColor("_GrassTint", ((Color)new Color32(110, 160, 70, 255)).linear);
            mat.SetColor("_FoliageTint", ((Color)new Color32(90, 140, 60, 255)).linear);
            mat.SetFloat("_FlipY", FlipY);

            var ids = new List<int>();
            foreach (var d in ItemDb.All)
                if (d.PlacesBlock != 0 || ItemVisuals.Icon(d.Id) != null) ids.Add(d.Id);
            ids.Sort();
            int rows = Mathf.CeilToInt(ids.Count / (float)kCols);
            int aw = kCols * kCell, ah = Mathf.Max(1, rows) * kCell;
            var atlasPx = new Color32[aw * ah];

            var msaa = new RenderTexture(new RenderTextureDescriptor(kCell, kCell, RenderTextureFormat.ARGB32, 0)
            { msaaSamples = 4, sRGB = true, useMipMap = false }) { name = "UiIconMsaa" };
            var resolved = new RenderTexture(new RenderTextureDescriptor(kCell, kCell, RenderTextureFormat.ARGB32, 0)
            { msaaSamples = 1, sRGB = true }) { name = "UiIconResolved" };
            msaa.Create(); resolved.Create();
            var read = new Texture2D(kCell, kCell, TextureFormat.RGBA32, false, false);
            var mesh = new Mesh { name = "UiIconMesh" };
            var prev = RenderTexture.active;
            int n = 0;
            try
            {
                for (int k = 0; k < ids.Count; k++)
                {
                    int id = ids[k];
                    var def = ItemDb.Get(id);
                    Graphics.SetRenderTarget(msaa);
                    GL.Clear(true, true, new Color(0, 0, 0, 0));
                    bool drawn = def.PlacesBlock != 0 ? DrawBlock(mat, mesh, def.PlacesBlock) : DrawSprite(mat, mesh, ItemVisuals.Icon(id));
                    if (!drawn) continue;
                    Graphics.Blit(msaa, resolved);
                    RenderTexture.active = resolved;
                    read.ReadPixels(new Rect(0, 0, kCell, kCell), 0, 0, false);
                    var px = read.GetPixels32();
                    PostProcess(px);
                    int cx = n % kCols, cy = rows - 1 - n / kCols;
                    for (int y = 0; y < kCell; y++)
                        System.Array.Copy(px, y * kCell, atlasPx, (cy * kCell + y) * aw + cx * kCell, kCell);
                    sSprites[id] = null; // placeholder, filled after the atlas exists
                    sSlots[id] = new Vector2Int(cx, cy);
                    n++;
                }
            }
            finally
            {
                RenderTexture.active = prev;
                Graphics.SetRenderTarget(prev);
                msaa.Release(); resolved.Release();
                Object.Destroy(msaa); Object.Destroy(resolved); Object.Destroy(read); Object.Destroy(mesh); Object.Destroy(mat);
            }

            sAtlas = new Texture2D(aw, ah, TextureFormat.RGBA32, true, false)
            {
                name = "UiItemAtlas", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 1, hideFlags = HideFlags.DontSave,
            };
            sAtlas.SetPixels32(atlasPx);
            sAtlas.Apply(true, true);
            foreach (var kv in sSlots)
            {
                var r = new Rect(kv.Value.x * kCell, kv.Value.y * kCell, kCell, kCell);
                var s = Sprite.Create(sAtlas, r, new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                s.name = "icon-" + ItemDb.NameOf(kv.Key);
                s.hideFlags = HideFlags.DontSave;
                sSprites[kv.Key] = s;
            }
            Debug.Log($"WM_UI icons rendered={n} atlas={aw}x{ah} ms={sw.Elapsed.TotalMilliseconds:F0}");
        }

        static readonly Dictionary<int, Vector2Int> sSlots = new Dictionary<int, Vector2Int>();

        /// <summary>
        /// Clip-space Y sign for rendering into the icon render texture. Metal/D3D (UV origin at the top)
        /// need the flip so ReadPixels returns the image upright.
        /// </summary>
        static float FlipY => SystemInfo.graphicsUVStartsAtTop ? -1f : 1f;

        // Isometric 3/4 view: yaw 45 degrees, then tilt the top 30 degrees towards the viewer.
        static readonly Quaternion kIso = Quaternion.AngleAxis(-30f, Vector3.right) * Quaternion.AngleAxis(45f, Vector3.up);

        static bool DrawBlock(Material mat, Mesh mesh, byte id)
        {
            var d = Blocks.Get(id);
            int flags = 0;
            if (d.Emissive) flags |= VFlags.Emissive;
            if (d.Tint == TintKind.Grass) flags |= VFlags.TintGrass;
            if (d.Tint == TintKind.Foliage) flags |= VFlags.TintFoliage;

            var v = new List<Vector3>();
            var col = new List<Color>();
            var uv = new List<Vector4>();
            var idx = new List<int>();

            void Quad(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, int layer, float shade, float bottomShade)
            {
                int b = v.Count;
                v.Add(bl); v.Add(br); v.Add(tr); v.Add(tl);
                var lo = new Color(shade * bottomShade, shade * bottomShade, shade * bottomShade, 1f);
                var hi = new Color(shade, shade, shade, 1f);
                col.Add(lo); col.Add(lo); col.Add(hi); col.Add(hi);
                uv.Add(new Vector4(0, 0, layer, flags)); uv.Add(new Vector4(1, 0, layer, flags));
                uv.Add(new Vector4(1, 1, layer, flags)); uv.Add(new Vector4(0, 1, layer, flags));
                idx.Add(b); idx.Add(b + 1); idx.Add(b + 2); idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
            }

            float cutoff;
            if (d.Shape == RenderShape.Cross)
            {
                // Plants, flowers and torches read best as a flat upright sprite.
                cutoff = 0.5f;
                Quad(new Vector3(-0.8f, -0.82f), new Vector3(0.8f, -0.82f), new Vector3(0.8f, 0.78f), new Vector3(-0.8f, 0.78f), d.LSide, 1f, 1f);
            }
            else
            {
                cutoff = d.Shape == RenderShape.Leaves ? 0.5f : 0f;
                const float h = 0.5f, k = 1.06f;
                var off = new Vector3(0f, 0.05f, 0f);
                Vector3 P(float x, float y, float z) { var p = kIso * new Vector3(x, y, z) * k; return new Vector3(p.x, p.y) + off; }
                // Hidden faces first (they show through leaves and glass), then the three visible ones.
                Quad(P(-h, -h, h), P(h, -h, h), P(h, -h, -h), P(-h, -h, -h), d.LBottom, 0.42f, 1f);
                Quad(P(h, -h, h), P(-h, -h, h), P(-h, h, h), P(h, h, h), d.LSide, 0.5f, 0.9f);
                Quad(P(-h, -h, h), P(-h, -h, -h), P(-h, h, -h), P(-h, h, h), d.LSide, 0.55f, 0.9f);
                Quad(P(-h, -h, -h), P(h, -h, -h), P(h, h, -h), P(-h, h, -h), d.LFront, 0.84f, 0.86f);
                Quad(P(h, -h, -h), P(h, -h, h), P(h, h, h), P(h, h, -h), d.LSide, 0.63f, 0.86f);
                Quad(P(-h, h, -h), P(h, h, -h), P(h, h, h), P(-h, h, h), d.LTop, 1.02f, 1.02f);
            }
            mesh.Clear();
            mesh.SetVertices(v);
            mesh.SetColors(col);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(idx, 0);
            mat.SetFloat("_Cutoff", cutoff);
            mat.SetFloat("_Glass", d.Shape == RenderShape.Glass ? 1f : 0f);
            mat.SetPass(0);
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            return true;
        }

        static bool DrawSprite(Material mat, Mesh mesh, Sprite s)
        {
            if (s == null || s.texture == null) return false;
            var t = s.texture;
            var r = s.textureRect;
            float u0 = r.xMin / t.width, u1 = r.xMax / t.width, v0 = r.yMin / t.height, v1 = r.yMax / t.height;
            const float e = 0.86f;
            mesh.Clear();
            mesh.SetVertices(new List<Vector3> { new Vector3(-e, -e + 0.06f), new Vector3(e, -e + 0.06f), new Vector3(e, e + 0.06f), new Vector3(-e, e + 0.06f) });
            mesh.SetColors(new List<Color> { Color.white, Color.white, Color.white, Color.white });
            mesh.SetUVs(0, new List<Vector4> { new Vector4(u0, v0), new Vector4(u1, v0), new Vector4(u1, v1), new Vector4(u0, v1) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mat.SetTexture("_MainTex", t);
            mat.SetPass(1);
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
            return true;
        }

        /// <summary>Add a soft drop shadow under the (premultiplied) icon and convert to straight alpha.</summary>
        static void PostProcess(Color32[] px)
        {
            const int N = kCell, R = 3, Off = 4;
            var a = new float[N * N];
            for (int i = 0; i < a.Length; i++) a[i] = px[i].a / 255f;
            var tmp = new float[N * N];
            var blur = new float[N * N];
            for (int pass = 0; pass < 2; pass++)
            {
                var src = pass == 0 ? a : blur;
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float s = 0; int c = 0;
                        for (int k = -R; k <= R; k++) { int xx = x + k; if (xx < 0 || xx >= N) continue; s += src[y * N + xx]; c++; }
                        tmp[y * N + x] = s / c;
                    }
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float s = 0; int c = 0;
                        for (int k = -R; k <= R; k++) { int yy = y + k; if (yy < 0 || yy >= N) continue; s += tmp[yy * N + x]; c++; }
                        blur[y * N + x] = s / c;
                    }
            }
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int i = y * N + x;
                    int sy = y + Off; // row 0 is the bottom: sample above to cast the shadow downwards
                    float sh = sy < N ? blur[sy * N + x] * 0.55f : 0f;
                    var p = px[i];
                    float ia = p.a / 255f;
                    float oa = ia + sh * (1f - ia);
                    if (oa <= 0.002f) { px[i] = new Color32(0, 0, 0, 0); continue; }
                    // Premultiplied over black shadow, then un-premultiply.
                    float r = p.r / 255f / oa, g = p.g / 255f / oa, b = p.b / 255f / oa;
                    px[i] = new Color32((byte)Mathf.Clamp(r * 255f, 0, 255), (byte)Mathf.Clamp(g * 255f, 0, 255), (byte)Mathf.Clamp(b * 255f, 0, 255), (byte)(oa * 255f));
                }
        }

        // ------------------------------------------------------------------ item text

        public static string Kind(ItemDef d)
        {
            if (d == null) return "";
            if (d.Tool != ToolKind.None)
            {
                string tier = d.Tier switch { 1 => "Wood", 2 => "Stone", 3 => "Iron", _ => "" };
                return $"{d.Tool} · {tier} tier";
            }
            if (d.PlacesBlock != 0)
            {
                var b = Blocks.Get(d.PlacesBlock);
                if (b.LightEmit > 0) return "Light source";
                if (d.PlacesBlock == B.Workbench || d.PlacesBlock == B.Furnace) return "Crafting station";
                if (b.Shape == RenderShape.Cross) return "Plant";
                return "Block";
            }
            return "Material";
        }

        static readonly Dictionary<int, string> kDescriptions = new Dictionary<int, string>
        {
            { B.Stone, "Dense rock from the hills. Breaks into cobblestone." },
            { B.Dirt, "Loose earth, quick to dig with a shovel or by hand." },
            { B.Grass, "Turf with living roots. Yields dirt when dug." },
            { B.Sand, "Fine and pale. Fire it with coal to make glass." },
            { B.Sandstone, "Sand pressed into a warm, workable stone." },
            { B.Gravel, "Loose stones. Sometimes hides a shard of flint." },
            { B.Clay, "Smooth, grey and cool to the touch." },
            { B.Snow, "Packed powder from high, cold places." },
            { B.Ice, "Clear and brittle. Shatters when broken." },
            { B.Cobblestone, "Rough rubble — the backbone of early tools and walls." },
            { B.MossyCobblestone, "Cobblestone softened by moss and time." },
            { B.Planks, "Sawn timber. The start of almost everything." },
            { B.Glass, "Clear panes that let the light in." },
            { B.LogOak, "A sturdy trunk. Split it into planks." },
            { B.LogBirch, "Pale, papery bark over clean wood." },
            { B.LogSpruce, "Dark resinous timber from the cold forests." },
            { B.LogDark, "Heavy, near-black heartwood." },
            { B.CoalOre, "Stone seamed with coal." },
            { B.IronOre, "Rust-flecked rock. Needs a stone pickaxe or better." },
            { B.GoldOre, "Glinting veins deep underground. Needs an iron pickaxe." },
            { B.Cactus, "Prickly desert growth." },
            { B.Workbench, "Place it to unlock tools and advanced building recipes nearby." },
            { B.Furnace, "Place it to smelt ore, sand and stone nearby." },
            { B.Torch, "Holds back the dark. Place it on any solid surface." },
            { B.GlowCap, "A cave mushroom with a soft cyan glow." },
            { ItemIds.Stick, "A simple shaft for handles and torches." },
            { ItemIds.Coal, "Burns hot. Fuel for furnaces and torches." },
            { ItemIds.RawIron, "Unrefined ore. Smelt it in a furnace." },
            { ItemIds.IronIngot, "Refined iron, ready for the strongest tools." },
            { ItemIds.RawGold, "Heavy, soft ore. Smelt it in a furnace." },
            { ItemIds.GoldIngot, "A bright bar of refined gold." },
            { ItemIds.Flint, "A sharp, glassy shard found in gravel." },
            { ItemIds.PickaxeWood, "Mines stone and coal. Wears quickly." },
            { ItemIds.PickaxeStone, "Mines iron ore and everything softer." },
            { ItemIds.PickaxeIron, "Mines gold ore and makes short work of rock." },
            { ItemIds.AxeWood, "Fells trees faster than bare hands." },
            { ItemIds.AxeStone, "A heavier head for quicker chopping." },
            { ItemIds.AxeIron, "Cuts through timber in a few swings." },
            { ItemIds.ShovelWood, "Digs dirt, sand and gravel faster." },
            { ItemIds.ShovelStone, "A sturdier blade for steady digging." },
            { ItemIds.ShovelIron, "Moves earth with ease." },
        };

        public static string Description(int id)
        {
            var d = ItemDb.Get(id);
            if (d != null && !string.IsNullOrEmpty(d.Description)) return d.Description;
            if (kDescriptions.TryGetValue(id, out var s)) return s;
            if (d != null && d.PlacesBlock != 0)
            {
                var b = Blocks.Get(d.PlacesBlock);
                if (Blocks.IsLeaves(b.Id)) return "Living foliage. Breaking it sometimes drops a stick.";
                if (b.Shape == RenderShape.Cross) return "A wild plant. Place it on soil.";
            }
            return "";
        }
    }
}
