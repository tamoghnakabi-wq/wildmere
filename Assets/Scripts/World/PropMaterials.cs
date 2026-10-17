using System.Collections.Generic;
using UnityEngine;

namespace Wildmere.World
{
    /// <summary>
    /// Materials for Blender-made models (props, wildlife, held tools). Models carry albedo in vertex
    /// colours and name their materials M_&lt;Surface&gt;; each surface maps to "Wildmere/Prop" with a
    /// triplanar detail layer from the block texture array so models share the voxel world's grain.
    /// </summary>
    public static class PropMaterials
    {
        struct Surface { public string Detail; public float DetailAmount, Smoothness, Emission, Wind; }

        static readonly Dictionary<string, Surface> kSurfaces = new Dictionary<string, Surface>
        {
            { "Stone",  new Surface { Detail = "stone", DetailAmount = 0.55f, Smoothness = 0.22f } },
            { "Rock",   new Surface { Detail = "stone", DetailAmount = 0.55f, Smoothness = 0.22f } },
            { "Moss",   new Surface { Detail = "grass_top", DetailAmount = 0.35f, Smoothness = 0.12f } },
            { "Bark",   new Surface { Detail = "log_oak_side", DetailAmount = 0.45f, Smoothness = 0.15f } },
            { "Wood",   new Surface { Detail = "planks", DetailAmount = 0.3f, Smoothness = 0.25f } },
            { "Sand",   new Surface { Detail = "sandstone_side", DetailAmount = 0.5f, Smoothness = 0.15f } },
            { "Sandstone", new Surface { Detail = "sandstone_side", DetailAmount = 0.5f, Smoothness = 0.15f } },
            { "Ice",    new Surface { Detail = "ice", DetailAmount = 0.3f, Smoothness = 0.85f } },
            { "Metal",  new Surface { Detail = null, Smoothness = 0.55f } },
            { "Fur",    new Surface { Detail = null, Smoothness = 0.1f } },
            { "Feather", new Surface { Detail = null, Smoothness = 0.2f } },
            { "Leaf",   new Surface { Detail = null, Smoothness = 0.3f, Wind = 1f } },
            { "Leaves", new Surface { Detail = null, Smoothness = 0.3f, Wind = 1f } },
            { "Cloth",  new Surface { Detail = null, Smoothness = 0.12f } },
            { "Horn",   new Surface { Detail = null, Smoothness = 0.35f } },
            { "Foliage", new Surface { Detail = null, Smoothness = 0.3f, Wind = 1f } },
            { "Cap",    new Surface { Detail = null, Smoothness = 0.35f } },
            { "Glow",   new Surface { Detail = null, Smoothness = 0.4f, Emission = 2.5f } },
            { "Flame",  new Surface { Detail = null, Smoothness = 0f, Emission = 4f } },
        };

        static readonly Dictionary<string, Material> sCache = new Dictionary<string, Material>();
        static Material sSprite, sDefault;

        public static Material Sprite
        {
            get
            {
                if (sSprite == null)
                {
                    var sh = Shader.Find("Universal Render Pipeline/Unlit");
                    sSprite = new Material(sh) { name = "ItemSprite" };
                    sSprite.SetFloat("_AlphaClip", 1f);
                    sSprite.SetFloat("_Cutoff", 0.5f);
                    sSprite.EnableKeyword("_ALPHATEST_ON");
                }
                return sSprite;
            }
        }

        /// <summary>Material for an imported model material (matched by its M_ name).</summary>
        public static Material ForModel(Material imported)
        {
            string name = imported != null ? imported.name : "Default";
            int i = name.IndexOf("M_");
            string surface = i >= 0 ? name.Substring(i + 2) : name;
            int sp = surface.IndexOfAny(new[] { ' ', '(', '.' });
            if (sp > 0) surface = surface.Substring(0, sp);
            return Get(surface);
        }

        public static Material Get(string surface)
        {
            if (sCache.TryGetValue(surface, out var m)) return m;
            var sh = Shader.Find("Wildmere/Prop");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            m = new Material(sh) { name = "Prop " + surface, enableInstancing = true };
            kSurfaces.TryGetValue(surface, out var s);
            if (!kSurfaces.ContainsKey(surface)) s = new Surface { Smoothness = 0.25f };
            int layer = -1;
            if (s.Detail != null && BlockTextures.Layers.TryGetValue(s.Detail, out int l)) layer = l;
            m.SetFloat("_DetailLayer", layer);
            m.SetFloat("_DetailAmount", layer >= 0 ? s.DetailAmount : 0f);
            m.SetFloat("_Smoothness", s.Smoothness);
            m.SetFloat("_Emission", s.Emission);
            m.SetFloat("_Wind", s.Wind);
            sCache[surface] = m;
            return m;
        }

        public static Material Default => sDefault != null ? sDefault : (sDefault = Get("Default"));
    }
}
