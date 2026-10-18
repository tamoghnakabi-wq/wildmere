using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Wildmere.Rendering
{
    /// <summary>
    /// Shadow-mapped volumetric sun/moon light (god rays through canopies, shafts in morning mist and dark
    /// forests, light beams under water). A RenderGraph pass enqueued per camera from
    /// RenderPipelineManager.beginCameraRendering (no renderer asset changes): it ray-marches the main
    /// light's cascaded shadow map through the haze/mist density at reduced resolution (per-pixel dithered),
    /// then blurs and adds the in-scattered light onto the camera colour after transparents, before post.
    /// Quality: 1 = quarter res / 8 steps, 2 = quarter res / 12 steps, 3 = half res / 16 steps.
    /// </summary>
    public static class VolumetricLight
    {
        static Material sMat;
        static Pass sPass;
        static readonly int kParams = Shader.PropertyToID("_WM_VolParams");
        static readonly int kTint = Shader.PropertyToID("_WM_VolTint");
        static readonly int kTexel = Shader.PropertyToID("_WM_VolTexel");

        /// <summary>Overall strength (Atmosphere sets it from time of day, biome and weather).</summary>
        public static float Strength = 1f;
        /// <summary>Extra in-scattering density multiplier on top of the haze (dark forest, mist).</summary>
        public static float DensityBoost = 1f;
        public static Color Tint = Color.white;

        public static void Enqueue(Camera cam, UniversalAdditionalCameraData data)
        {
            if (RenderQuality.Volumetric <= 0 || data.renderType != CameraRenderType.Base || Strength <= 0.001f) return;
            if (sMat == null)
            {
                var sh = Shader.Find("Hidden/Wildmere/Volumetric");
                if (sh == null) { RenderQuality.Volumetric = 0; Debug.LogWarning("WM_GFX volumetric shader missing"); return; }
                sMat = CoreUtils.CreateEngineMaterial(sh);
                sPass = new Pass();
            }
            var r = data.scriptableRenderer;
            if (r == null) return;
            sPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            sPass.ConfigureInput(ScriptableRenderPassInput.Depth);
            r.EnqueuePass(sPass);
        }

        sealed class Pass : ScriptableRenderPass
        {
            sealed class Data
            {
                public Material Mat;
                public TextureHandle Src;
                public Vector4 Params, Tint, Texel;
            }

            static readonly MaterialPropertyBlock sBlock = new MaterialPropertyBlock();
            static readonly int kBlitTex = Shader.PropertyToID("_BlitTexture");
            static readonly int kBlitScaleBias = Shader.PropertyToID("_BlitScaleBias");

            public Pass() { profilingSampler = new ProfilingSampler("WM Volumetric Light"); }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                var cam = frameData.Get<UniversalCameraData>();
                if (res.isActiveTargetBackBuffer || !res.mainShadowsTexture.IsValid() || !res.cameraDepthTexture.IsValid()) return;
                if (cam.maxShadowDistance <= 0f) return;

                int q = RenderQuality.Volumetric;
                int div = q >= 3 ? 2 : 4;
                int steps = q >= 3 ? 16 : q == 2 ? 12 : 8;
                var desc = renderGraph.GetTextureDesc(res.activeColorTexture);
                desc.width = Mathf.Max(1, desc.width / div);
                desc.height = Mathf.Max(1, desc.height / div);
                desc.colorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                desc.depthBufferBits = DepthBits.None;
                desc.msaaSamples = MSAASamples.None;
                desc.clearBuffer = false;
                desc.filterMode = FilterMode.Bilinear;
                desc.wrapMode = TextureWrapMode.Clamp;
                desc.name = "WM_VolumetricLow";
                var low = renderGraph.CreateTexture(desc);

                var p = new Vector4(Mathf.Min(cam.maxShadowDistance * 0.95f, 140f), steps, Strength, DensityBoost);
                var tint = new Vector4(Tint.r, Tint.g, Tint.b, 0);
                var texel = new Vector4(1f / desc.width, 1f / desc.height, desc.width, desc.height);

                using (var builder = renderGraph.AddRasterRenderPass<Data>("WM Volumetric March", out var d, profilingSampler))
                {
                    d.Mat = sMat; d.Params = p; d.Tint = tint; d.Texel = texel;
                    builder.UseTexture(res.cameraDepthTexture);
                    builder.UseTexture(res.mainShadowsTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderAttachment(low, 0, AccessFlags.Write);
                    builder.SetRenderFunc((Data data, RasterGraphContext ctx) =>
                    {
                        sBlock.Clear();
                        sBlock.SetVector(kBlitScaleBias, new Vector4(1, 1, 0, 0));
                        sBlock.SetVector(kParams, data.Params);
                        sBlock.SetVector(kTint, data.Tint);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Mat, 0, MeshTopology.Triangles, 3, 1, sBlock);
                    });
                }

                using (var builder = renderGraph.AddRasterRenderPass<Data>("WM Volumetric Composite", out var d, profilingSampler))
                {
                    d.Mat = sMat; d.Src = low; d.Texel = texel; d.Params = p;
                    builder.UseTexture(low);
                    builder.SetRenderAttachment(res.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderFunc((Data data, RasterGraphContext ctx) =>
                    {
                        sBlock.Clear();
                        sBlock.SetTexture(kBlitTex, data.Src);
                        sBlock.SetVector(kBlitScaleBias, new Vector4(1, 1, 0, 0));
                        sBlock.SetVector(kTexel, data.Texel);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, data.Mat, 1, MeshTopology.Triangles, 3, 1, sBlock);
                    });
                }
            }
        }
    }
}
