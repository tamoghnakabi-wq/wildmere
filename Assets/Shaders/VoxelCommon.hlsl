#ifndef WILDMERE_VOXEL_COMMON_INCLUDED
#define WILDMERE_VOXEL_COMMON_INCLUDED
// Shared by every Wildmere world shader: vertex layout decoding, wind, atmosphere/fog and the
// voxel lighting model (sun via URP main light + shadows, hemisphere ambient scaled by voxel sky
// light, warm block light from torches).
//
// Vertex layout (ChunkMesher.VoxelVertex):
//   POSITION  float3   chunk-local position
//   NORMAL    snorm8x4 axis-aligned face normal (tangent derived below)
//   TEXCOORD0 float4   u, v (block units), texture-array layer, flags (VFlags)
//   TEXCOORD1 unorm8x4 sky light, block light, vertex AO, per-block random
//
// Tangent rules (must match ChunkMesher.EmitFace): +X u=+z, -X u=-z, +Z u=-x, -Z u=+x, ±Y u=+x;
// tangent = |n.y| > .5 ? (1,0,0) : (-n.z, 0, n.x); bitangent = cross(tangent, normal).

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

#define WM_FLAG_EMISSIVE 1
#define WM_FLAG_WIND 2
#define WM_FLAG_PLANT 4
#define WM_FLAG_TINT_GRASS 8
#define WM_FLAG_TINT_FOLIAGE 16
#define WM_FLAG_WATER 32

TEXTURE2D_ARRAY(_BlockAlbedo); SAMPLER(sampler_BlockAlbedo);
TEXTURE2D_ARRAY(_BlockNormal);
TEXTURE2D_ARRAY(_BlockMask);
TEXTURE2D(_WM_GrassTint); SAMPLER(sampler_WM_GrassTint);
TEXTURE2D(_WM_FoliageTint);

// Globals driven by the atmosphere / weather systems (C#: Shader.SetGlobal*).
float  _WM_TintSize;
float4 _WM_SunDir;          // xyz: direction TOWARDS the sun (or moon at night)
float4 _WM_AmbientSky;      // rgb: ambient from above (already includes intensity)
float4 _WM_AmbientGround;   // rgb: ambient from below
float4 _WM_CaveAmbient;     // rgb: minimum light with no sky access
float4 _WM_BlockLight;      // rgb: torch light colour, a: intensity
float4 _WM_FogColor;        // rgb: haze colour away from the sun
float4 _WM_FogSunColor;     // rgb: haze colour towards the sun
float4 _WM_Fog;             // x: density, y: height falloff, z: base height, w: start distance
float4 _WM_ViewFade;        // x: fade start (m), y: fade end (m) — hides the streaming edge
float4 _WM_Wind;            // xy: direction, z: strength, w: gustiness
float  _WM_Wetness;         // 0..1 rain wetness
float  _WM_Underwater;      // 1 when the camera is under water
float4 _WM_UnderwaterColor;

float WMHash(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Wind sway in world space; identical in every pass so shadows follow the foliage.
float3 WMWind(float3 positionWS, float flags, float v)
{
    uint f = (uint)round(flags);
    if ((f & WM_FLAG_WIND) == 0) return positionWS;
    float t = _Time.y;
    float strength = _WM_Wind.z;
    float phase = dot(positionWS.xz, float2(0.37, 0.23));
    float gust = 0.6 + 0.4 * sin(t * 0.7 + positionWS.x * 0.05) * _WM_Wind.w;
    float sway = sin(t * 1.9 + phase) * 0.6 + sin(t * 3.7 + phase * 1.7) * 0.25;
    float3 dir = float3(_WM_Wind.x, 0, _WM_Wind.y);
    if ((f & WM_FLAG_PLANT) != 0)
    {
        float k = v > 0.5 ? 1.0 : 0.0; // only the top edge of a plant moves (plant uv.v is 0 or 1)
        positionWS += dir * (sway * 0.11 * strength * gust) * k;
        positionWS.y -= abs(sway) * 0.03 * strength * k;
    }
    else
    {
        float jitter = WMHash(floor(positionWS * 0.999)) - 0.5;
        positionWS += dir * (sway * 0.045 * strength * gust) + float3(jitter, 0, -jitter) * 0.02 * strength * sin(t * 2.3 + phase);
    }
    return positionWS;
}

// Analytic aerial perspective (distance + height haze with sun-ward scattering) and the
// view-distance fade into the sky colour.
float WMFogAmount(float3 positionWS)
{
    float3 eye = GetCameraPositionWS();
    float d = distance(positionWS, eye);
    float dd = max(0.0, d - _WM_Fog.w);
    float y0 = max(0.0, eye.y - _WM_Fog.z) * _WM_Fog.y;
    float y1 = max(0.0, positionWS.y - _WM_Fog.z) * _WM_Fog.y;
    float density = (exp(-y0) + 4.0 * exp(-(y0 + y1) * 0.5) + exp(-y1)) / 6.0;
    float fog = 1.0 - exp(-dd * _WM_Fog.x * (0.25 + 0.75 * density));
    float edge = smoothstep(_WM_ViewFade.x, _WM_ViewFade.y, length(positionWS.xz - eye.xz));
    return saturate(max(fog, edge));
}

half3 WMFogColor(float3 positionWS)
{
    float3 ray = normalize(positionWS - GetCameraPositionWS());
    float sunward = pow(saturate(dot(ray, _WM_SunDir.xyz)), 5.0);
    return lerp(_WM_FogColor.rgb, _WM_FogSunColor.rgb, sunward);
}

half3 WMApplyFog(half3 color, float3 positionWS)
{
    if (_WM_Underwater > 0.5)
    {
        float d = distance(positionWS, GetCameraPositionWS());
        float k = 1.0 - exp(-d * 0.085);
        return lerp(color, _WM_UnderwaterColor.rgb, saturate(k));
    }
    return lerp(color, WMFogColor(positionWS), WMFogAmount(positionWS));
}

half3 WMTint(float3 positionWS, uint flags)
{
    // Texel centres sit at block centres; bilinear filtering blends neighbouring columns smoothly.
    float2 uv = positionWS.xz / _WM_TintSize;
    if ((flags & WM_FLAG_TINT_FOLIAGE) != 0) return SRGBToLinear(SAMPLE_TEXTURE2D_LOD(_WM_FoliageTint, sampler_WM_GrassTint, uv, 0).rgb);
    return SRGBToLinear(SAMPLE_TEXTURE2D_LOD(_WM_GrassTint, sampler_WM_GrassTint, uv, 0).rgb);
}

float3 WMTangent(float3 n)
{
    return abs(n.y) > 0.5 ? float3(1, 0, 0) : float3(-n.z, 0, n.x);
}

#endif
