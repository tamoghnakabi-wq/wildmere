#ifndef WILDMERE_VOXEL_PASSES_INCLUDED
#define WILDMERE_VOXEL_PASSES_INCLUDED
// Vertex/fragment programs for Wildmere/Voxel and Wildmere/VoxelCutout (WM_CUTOUT defined).

#include "VoxelCommon.hlsl"
#include "VoxelLighting.hlsl"

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

    float3 eye = GetCameraPositionWS();
    float dist = distance(i.positionWS, eye);
    // Distance fade of fine surface detail: normal-map bevels and gloss settle down further away so big
    // flat areas don't show a printed block grid or sparkle.
    half far = _WM_Quality.w > 0.5 ? saturate((dist - 10.0) / 45.0) : 0.0;

    half3 albedo = albedoA.rgb;
    if ((flags & (WM_FLAG_TINT_GRASS | WM_FLAG_TINT_FOLIAGE)) != 0)
        albedo *= lerp(half3(1, 1, 1), WMTint(i.positionWS, flags) * ((flags & WM_FLAG_PLANT) != 0 ? 0.72 : 0.88), mask.a);

    // Subtle per-block variation so large flat areas don't look printed.
    half rnd = i.light.w;
    albedo *= 0.95 + 0.1 * rnd;

    float3 N = normalize(i.normalWS);
    float3 T = WMTangent(N);
    float3 Bt = cross(T, N);
    half2 nxy = nrmTex.rg * 2.0 - 1.0;
    // Tops are walked on and seen at grazing angles: their bevels read as a grid, so flatten them more.
    half nStrength = lerp(N.y > 0.5 ? 0.7 : 1.0, 0.3, far);
    nxy *= nStrength;

    half sky = i.light.x;            // 0..1
    half blk = i.light.y;            // 0..1
    half vao = lerp(0.42, 1.0, i.light.z);
    half occlusion = vao * lerp(1.0, mask.r, lerp(0.85, 0.4, far));
    half smoothness = mask.g * lerp(1.0, 0.6, far);

    // Rain: wet surfaces darken and gloss up; puddles collect on flat tops and ripple under the drops.
    half reflectBoost = 0;
    half upness = N.y;
    half wet = _WM_Wetness * saturate(sky * 1.6 - 0.5) * saturate(N.y * 0.7 + 0.3);
    half puddle = 0;
    if (wet > 0.001 && (flags & WM_FLAG_PLANT) == 0)
    {
        puddle = WMPuddleMask(i.positionWS, upness, sky) * _WM_Wetness;
        albedo *= lerp(1.0, 0.68, wet);
        smoothness = lerp(smoothness, max(smoothness, 0.78), wet);
        nxy *= 1.0 - puddle * 0.9;
        smoothness = lerp(smoothness, 0.97, puddle);
        albedo *= 1.0 - puddle * 0.35;
        nxy += WMRainRipples(i.positionWS.xz, _WM_Precip.x) * saturate(wet * 2.0 - 0.6) * (1.0 - far);
        reflectBoost = puddle * 0.8 + wet * 0.2;
    }

    // Snow settles on open, up-facing surfaces while it snows (patchy at first, full cover later).
    if (_WM_Surface.x > 0.001 && N.y > 0.5 && (flags & WM_FLAG_EMISSIVE) == 0)
    {
        half patch = SAMPLE_TEXTURE2D_LOD(_WM_CloudNoise, sampler_WM_CloudNoise, i.positionWS.xz * (1.0 / 9.0), 0).g;
        half cover = saturate((_WM_Surface.x * 1.35 - patch) * 6.0) * smoothstep(0.82, 0.97, sky);
        if ((flags & WM_FLAG_PLANT) != 0) cover *= saturate(i.uv.y) * 0.8;
        albedo = lerp(albedo, half3(0.74, 0.77, 0.82) * (0.94 + 0.1 * rnd), cover);
        smoothness = lerp(smoothness, 0.35, cover);
        nxy *= 1.0 - cover * 0.5;
        occlusion = lerp(occlusion, max(occlusion, 0.8), cover);
    }

    half3 nTS = half3(nxy, sqrt(saturate(1.0 - dot(nxy, nxy))));
    float3 nWS = normalize(nTS.x * T + nTS.y * Bt + nTS.z * N);
    if ((flags & WM_FLAG_PLANT) != 0) nWS = normalize(lerp(float3(0, 1, 0), N, 0.3));

    half3 color = WMShadeSurface(albedo, smoothness, occlusion, nWS, i.positionWS, i.positionCS, sky, blk, reflectBoost);

    // Underwater terrain: dancing caustics where sunlight reaches through the surface.
    if (_WM_Quality.x > 0.5 && i.positionWS.y < WM_SEA_LEVEL + 0.9 && sky > 0.05 && _WM_Underwater < 0.5)
    {
        half depthBelow = WM_SEA_LEVEL + 0.9 - i.positionWS.y;
        float4 sc = TransformWorldToShadowCoord(i.positionWS);
        Light L = GetMainLight(sc, i.positionWS, half4(1, 1, 1, 1));
        half c = WMCaustics(i.positionWS);
        color += albedo * L.color * L.shadowAttenuation * saturate(N.y * 0.6 + 0.4) * c * 1.6 *
                 saturate(depthBelow * 2.0) * exp(-depthBelow * 0.22) * smoothstep(0.05, 0.4, sky);
    }

    if ((flags & WM_FLAG_EMISSIVE) != 0)
    {
        // Flames flicker fast; cool-coloured glowcaps breathe slowly.
        float t = _Time.y;
        half ph = rnd * 37.0;
        half cool = saturate((albedo.b - albedo.r) * 6.0);
        half flame = 0.82 + 0.1 * sin(t * 11.0 + ph) + 0.08 * sin(t * 23.0 + ph * 1.7);
        half breathe = 0.8 + 0.2 * sin(t * 1.4 + ph);
        half pulse = lerp(flame, breathe, cool);
        color += albedo * 2.6 * saturate(Luminance(albedo) * 1.8) * pulse;
    }

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
