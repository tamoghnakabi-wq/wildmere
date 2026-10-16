#ifndef WILDMERE_VOXEL_PASSES_INCLUDED
#define WILDMERE_VOXEL_PASSES_INCLUDED
// Vertex/fragment programs for Wildmere/Voxel and Wildmere/VoxelCutout (WM_CUTOUT defined).

#include "VoxelCommon.hlsl"

struct VAttributes
{
    float4 positionOS : POSITION;
    float4 normalOS   : NORMAL;
    float4 uv         : TEXCOORD0;
    float4 light      : TEXCOORD1;
};

struct VVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    half3  normalWS   : TEXCOORD1;
    float4 uv         : TEXCOORD2;
    half4  light      : TEXCOORD3;
};

VVaryings VoxelVert(VAttributes v)
{
    VVaryings o = (VVaryings)0;
    float3 ws = TransformObjectToWorld(v.positionOS.xyz);
    ws = WMWind(ws, v.uv.w, v.uv.y);
    o.positionWS = ws;
    o.positionCS = TransformWorldToHClip(ws);
    o.normalWS = normalize(v.normalOS.xyz);
    o.uv = v.uv;
    o.light = v.light;
    return o;
}

half4 SampleAlbedo(float4 uv)
{
    return SAMPLE_TEXTURE2D_ARRAY(_BlockAlbedo, sampler_BlockAlbedo, frac(uv.xy), uv.z);
}

// frac() breaks derivatives at tile seams; use the continuous UV for mip selection.
half4 SampleArray(TEXTURE2D_ARRAY_PARAM(tex, smp), float4 uv)
{
    float2 dx = ddx(uv.xy), dy = ddy(uv.xy);
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(tex, smp, frac(uv.xy), uv.z, dx, dy);
}

half4 VoxelFrag(VVaryings i) : SV_Target
{
    uint flags = (uint)round(i.uv.w);
    half4 albedoA = SampleArray(TEXTURE2D_ARRAY_ARGS(_BlockAlbedo, sampler_BlockAlbedo), i.uv);
#ifdef WM_CUTOUT
    clip(albedoA.a - 0.5);
#endif
    half4 mask = SampleArray(TEXTURE2D_ARRAY_ARGS(_BlockMask, sampler_BlockAlbedo), i.uv);
    half4 nrmTex = SampleArray(TEXTURE2D_ARRAY_ARGS(_BlockNormal, sampler_BlockAlbedo), i.uv);

    half3 albedo = albedoA.rgb;
    if ((flags & (WM_FLAG_TINT_GRASS | WM_FLAG_TINT_FOLIAGE)) != 0)
        albedo *= lerp(half3(1, 1, 1), WMTint(i.positionWS, flags) * 1.7, mask.a);

    // Subtle per-block variation so large flat areas don't look printed.
    half rnd = i.light.w;
    albedo *= 0.94 + 0.12 * rnd;

    float3 N = normalize(i.normalWS);
    float3 T = WMTangent(N);
    float3 Bt = cross(T, N);
    half2 nxy = nrmTex.rg * 2.0 - 1.0;
    half3 nTS = half3(nxy, sqrt(saturate(1.0 - dot(nxy, nxy))));
    float3 nWS = normalize(nTS.x * T + nTS.y * Bt + nTS.z * N);
    if ((flags & WM_FLAG_PLANT) != 0) nWS = normalize(lerp(float3(0, 1, 0), N, 0.3));

    half sky = i.light.x;            // 0..1
    half blk = i.light.y;            // 0..1
    half vao = lerp(0.42, 1.0, i.light.z);
    half occlusion = vao * lerp(1.0, mask.r, 0.85);
    half smoothness = mask.g;

    // Rain darkens and glosses exposed surfaces.
    half wet = _WM_Wetness * saturate(sky * 1.6 - 0.5) * saturate(nWS.y * 0.7 + 0.3);
    albedo *= lerp(1.0, 0.72, wet);
    smoothness = lerp(smoothness, max(smoothness, 0.82), wet);

    float2 screenUV = GetNormalizedScreenSpaceUV(i.positionCS);
    AmbientOcclusionFactor ssao = GetScreenSpaceAmbientOcclusion(screenUV);

    BRDFData brdf;
    half alpha = 1.0;
    InitializeBRDFData(albedo, 0.0, half3(0, 0, 0), smoothness, alpha, brdf);

    float3 V = GetWorldSpaceNormalizeViewDir(i.positionWS);
    float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
    Light sun = GetMainLight(shadowCoord, i.positionWS, half4(1, 1, 1, 1));
    // No direct sun where the voxel sky light says there is no path to the sky (caves, under overhangs
    // outside the shadow distance); the shadow map handles everything finer than that.
    half skyVis = smoothstep(0.25, 0.85, sky);
    sun.shadowAttenuation *= skyVis * ssao.directAmbientOcclusion;
    half3 color = LightingPhysicallyBased(brdf, sun, nWS, V);

    // Ambient: hemisphere scaled by sky light (squared for a natural falloff into caves).
    half skyAmb = sky * sky;
    half3 hemi = lerp(_WM_AmbientGround.rgb, _WM_AmbientSky.rgb, saturate(nWS.y * 0.5 + 0.5));
    half3 ambient = hemi * skyAmb + _WM_CaveAmbient.rgb;
    // Torch light: warm, gently flickering, with a soft curve.
    half flicker = 0.92 + 0.08 * sin(_Time.y * 9.0 + WMHash(floor(i.positionWS)) * 40.0);
    half3 torch = _WM_BlockLight.rgb * _WM_BlockLight.a * pow(blk, 2.2) * flicker;
    half indirectOcc = occlusion * ssao.indirectAmbientOcclusion;
    color += (ambient + torch) * brdf.diffuse * indirectOcc;

    // Cheap sky reflection for glossy/wet surfaces.
    float3 R = reflect(-V, nWS);
    half3 env = lerp(_WM_AmbientGround.rgb, _WM_AmbientSky.rgb * 1.4, saturate(R.y * 0.5 + 0.5)) * skyAmb;
    half fres = Pow4(1.0 - saturate(dot(nWS, V)));
    color += env * EnvironmentBRDFSpecular(brdf, fres) * indirectOcc;

    if ((flags & WM_FLAG_EMISSIVE) != 0)
        color += albedo * 2.2 * saturate(Luminance(albedo) * 1.6);

    color = WMApplyFog(color, i.positionWS);
    return half4(color, 1);
}

// ------------------------------------------------------------------ shadow / depth passes

float3 _LightDirection;
float3 _LightPosition;

struct SAttributes
{
    float4 positionOS : POSITION;
    float4 normalOS   : NORMAL;
    float4 uv         : TEXCOORD0;
};

struct SVaryings
{
    float4 positionCS : SV_POSITION;
    float4 uv         : TEXCOORD0;
    half3  normalWS   : TEXCOORD1;
};

SVaryings ShadowVert(SAttributes v)
{
    SVaryings o = (SVaryings)0;
    float3 ws = WMWind(TransformObjectToWorld(v.positionOS.xyz), v.uv.w, v.uv.y);
    float3 n = normalize(v.normalOS.xyz);
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 L = normalize(_LightPosition - ws);
#else
    float3 L = _LightDirection;
#endif
    float4 clip = TransformWorldToHClip(ApplyShadowBias(ws, n, L));
#if UNITY_REVERSED_Z
    clip.z = min(clip.z, UNITY_NEAR_CLIP_VALUE);
#else
    clip.z = max(clip.z, UNITY_NEAR_CLIP_VALUE);
#endif
    o.positionCS = clip;
    o.uv = v.uv;
    return o;
}

SVaryings DepthVert(SAttributes v)
{
    SVaryings o = (SVaryings)0;
    float3 ws = WMWind(TransformObjectToWorld(v.positionOS.xyz), v.uv.w, v.uv.y);
    o.positionCS = TransformWorldToHClip(ws);
    o.uv = v.uv;
    o.normalWS = normalize(v.normalOS.xyz);
    return o;
}

half4 ShadowFrag(SVaryings i) : SV_Target
{
#ifdef WM_CUTOUT
    clip(SAMPLE_TEXTURE2D_ARRAY(_BlockAlbedo, sampler_BlockAlbedo, frac(i.uv.xy), i.uv.z).a - 0.5);
#endif
    return 0;
}

half4 DepthNormalsFrag(SVaryings i) : SV_Target
{
#ifdef WM_CUTOUT
    clip(SAMPLE_TEXTURE2D_ARRAY(_BlockAlbedo, sampler_BlockAlbedo, frac(i.uv.xy), i.uv.z).a - 0.5);
#endif
    return half4(normalize(i.normalWS), 0);
}

#endif
