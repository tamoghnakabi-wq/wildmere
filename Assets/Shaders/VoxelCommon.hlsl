#ifndef WILDMERE_VOXEL_COMMON_INCLUDED
#define WILDMERE_VOXEL_COMMON_INCLUDED
// Shared by every Wildmere world shader: vertex layout decoding, wind, the analytic atmosphere (sky
// gradient, aerial perspective, ground mist), clouds (sky layer, cloud shadows and reflections all read
// the same field), weather (wetness, puddles, rain ripples) and the voxel lighting model (sun via URP
// main light + shadows, hemisphere ambient scaled by voxel sky light, warm block light from torches).
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

#define WM_SEA_LEVEL 62.0

TEXTURE2D_ARRAY(_BlockAlbedo); SAMPLER(sampler_BlockAlbedo);
TEXTURE2D_ARRAY(_BlockNormal);
TEXTURE2D_ARRAY(_BlockMask);
TEXTURE2D(_WM_GrassTint); SAMPLER(sampler_WM_GrassTint);
TEXTURE2D(_WM_FoliageTint);
TEXTURE2D(_WM_CloudNoise); SAMPLER(sampler_WM_CloudNoise);   // r/g/b turbulence (low/mid/high), a worley puffs
TEXTURE2D(_WM_Caustics); SAMPLER(sampler_WM_Caustics);

// Globals driven by Atmosphere / Weather (C#: Shader.SetGlobal*). Not part of any material CBUFFER, so
// every shader stays SRP-batcher compatible.
float  _WM_TintSize;
float4 _WM_SunDir;          // xyz: direction TOWARDS the main light (sun by day, moon at night)
float4 _WM_RealSunDir;      // xyz: towards the sun (even at night)
float4 _WM_MoonDir;         // xyz: towards the moon, w: moon visibility
float4 _WM_AmbientSky;      // rgb: ambient from above (already includes intensity)
float4 _WM_AmbientGround;   // rgb: ambient from below
float4 _WM_CaveAmbient;     // rgb: minimum light with no sky access
float4 _WM_BlockLight;      // rgb: torch light colour, a: intensity
float4 _WM_FogColor;        // rgb: haze in-scatter colour (ambient part)
float4 _WM_FogSunColor;     // rgb: haze in-scatter colour from the sun (multiplied by the phase function)
float4 _WM_Fog;             // x: density, y: height falloff, z: base height, w: start distance
float4 _WM_Mist;            // x: ground-mist density, y: mist height falloff, z: mist base height, w: phase sharpness
float4 _WM_ViewFade;        // x: fade start (m), y: fade end (m) — hides the streaming edge
float4 _WM_Wind;            // xy: direction, z: strength, w: gustiness
float4 _WM_WindOffset;      // xy: accumulated cloud drift (m), z: accumulated wave time, w: unused
float  _WM_Wetness;         // 0..1 rain wetness
float4 _WM_Precip;          // x: rain intensity, y: snow intensity, z: puddle amount, w: lightning flash 0..1
float4 _WM_Lightning;       // xyz: direction towards the last strike, w: flash
float  _WM_Underwater;      // 1 when the camera is under water
float4 _WM_UnderwaterColor;
float4 _WM_SkyZenith;       // rgb
float4 _WM_SkyHorizon;      // rgb
float4 _WM_SunColor;        // rgb: sun disc/glow colour, a: sun visibility
float  _WM_Stars;           // 0..1
float4 _WM_Clouds;          // x: coverage 0..1, y: darkness (storm), z: base altitude (m), w: thickness (m)
float4 _WM_CloudLight;      // rgb: sun light reaching the cloud tops, a: cloud-shadow strength on the ground
float4 _WM_CloudAmbient;    // rgb: ambient light inside the clouds, a: cloud raymarch steps
float4 _WM_Quality;         // x: caustics, y: cloud shadows, z: puddles, w: distance normal fade (all 0/1)

float WMHash(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float WMHash2(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Interleaved gradient noise (Jimenez) — per-pixel dither for raymarches.
float WMIGN(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// ------------------------------------------------------------------ wind

// Wind sway in world space; identical in every pass so shadows follow the foliage.
float3 WMWind(float3 positionWS, float flags, float v)
{
    uint f = (uint)round(flags);
    if ((f & WM_FLAG_WIND) == 0) return positionWS;
    float t = _Time.y;
    float strength = _WM_Wind.z;
    float phase = dot(positionWS.xz, float2(0.37, 0.23));
    // Gusts roll across the landscape along the wind direction.
    float along = dot(positionWS.xz, _WM_Wind.xy);
    float gust = 0.55 + 0.45 * sin(t * 0.9 - along * 0.07) * (0.5 + 0.5 * sin(t * 0.31 - along * 0.021));
    gust = lerp(1.0, gust * 1.6, saturate(_WM_Wind.w));
    float sway = sin(t * 1.9 + phase) * 0.6 + sin(t * 3.7 + phase * 1.7) * 0.25;
    float3 dir = float3(_WM_Wind.x, 0, _WM_Wind.y);
    if ((f & WM_FLAG_PLANT) != 0)
    {
        float k = v > 0.5 ? 1.0 : 0.0; // only the top edge of a plant moves (plant uv.v is 0 or 1)
        float lean = strength * gust;
        positionWS += dir * ((sway * 0.09 + 0.06) * lean) * k;
        positionWS.y -= abs(sway) * 0.03 * lean * k;
    }
    else
    {
        float jitter = WMHash(floor(positionWS * 0.999)) - 0.5;
        positionWS += dir * (sway * 0.04 * strength * gust) + float3(jitter, 0, -jitter) * 0.02 * strength * sin(t * 2.3 + phase);
    }
    return positionWS;
}

// ------------------------------------------------------------------ atmosphere

// Phase function for haze: broad forward lobe plus a tight halo around the sun.
float WMPhase(float c)
{
    float broad = pow(saturate(c * 0.5 + 0.5), 6.0);
    float halo = pow(saturate(c), 48.0 * _WM_Mist.w + 8.0);
    return broad * 0.9 + halo * 1.6;
}

// Optical depth of the layer rho(y) = d0 * exp(-k (y - h0)) along a ray segment from `eye`.
float WMHeightOD(float3 eye, float3 dir, float dist, float d0, float k, float h0)
{
    float a = d0 * exp(-k * max(eye.y - h0, -12.0 / max(k, 1e-3)));
    float b = k * dir.y * dist;
    float f = abs(b) > 1e-3 ? (1.0 - exp(-b)) / b : 1.0 - 0.5 * b;
    return a * dist * max(f, 0.0);
}

// Haze colour seen along a direction: ambient in-scatter plus sun-scattered light.
half3 WMFogColorDir(float3 dir)
{
    return _WM_FogColor.rgb + _WM_FogSunColor.rgb * WMPhase(dot(dir, _WM_SunDir.xyz));
}

// Fraction of the surface colour replaced by haze (aerial perspective + ground mist + view-distance fade).
float WMFogAmount(float3 positionWS)
{
    float3 eye = GetCameraPositionWS();
    float3 v = positionWS - eye;
    float d = length(v);
    float3 dir = v / max(d, 1e-4);
    float dd = max(0.0, d - _WM_Fog.w);
    float3 start = eye + dir * min(d, _WM_Fog.w);
    float od = WMHeightOD(start, dir, dd, _WM_Fog.x, _WM_Fog.y, _WM_Fog.z) + dd * _WM_Fog.x * 0.18;
    od += WMHeightOD(eye, dir, d, _WM_Mist.x, _WM_Mist.y, _WM_Mist.z);
    float fog = 1.0 - exp(-od);
    float edge = smoothstep(_WM_ViewFade.x, _WM_ViewFade.y, length(positionWS.xz - eye.xz));
    return saturate(max(fog, edge));
}

half3 WMFogColor(float3 positionWS)
{
    return WMFogColorDir(normalize(positionWS - GetCameraPositionWS()));
}

half3 WMApplyFog(half3 color, float3 positionWS)
{
    if (_WM_Underwater > 0.5)
    {
        float d = distance(positionWS, GetCameraPositionWS());
        float3 absorb = float3(0.22, 0.075, 0.06);
        float3 T = exp(-absorb * d);
        return color * T + _WM_UnderwaterColor.rgb * (1.0 - T);
    }
    return lerp(color, WMFogColorDir(normalize(positionWS - GetCameraPositionWS())), WMFogAmount(positionWS));
}

// ------------------------------------------------------------------ sky + clouds

// Sky radiance along a direction without clouds/stars/discs (used by the sky shader and reflections).
half3 WMSkyGradient(float3 d)
{
    float up = d.y;
    float h = saturate(up);
    // Zenith-to-horizon gradient with a brighter, thicker band near the horizon (multiple scattering).
    half3 col = lerp(_WM_SkyHorizon.rgb, _WM_SkyZenith.rgb, pow(h, 0.5));
    col += _WM_SkyHorizon.rgb * 0.18 * exp(-h * 9.0);
    // Sun-ward glow over the whole sky (sunrise/sunset colour wraps around the sun).
    float cs = dot(d, _WM_RealSunDir.xyz);
    col += _WM_SunColor.rgb * _WM_SunColor.a * (pow(saturate(cs * 0.5 + 0.5), 5.0) * 0.22 * (1.0 - h * 0.6) + pow(saturate(cs), 24.0) * 0.45);
    // Below the horizon: haze colour so distant fog meets the sky seamlessly.
    float haze = 1.0 - smoothstep(-0.03, 0.2, up);
    col = lerp(col, WMFogColorDir(normalize(float3(d.x, max(d.y, 0.0) * 0.2, d.z))), haze);
    // Lightning lights up the whole sky (stronger towards the strike).
    col += half3(0.55, 0.6, 0.8) * _WM_Lightning.w * (0.35 + 0.65 * pow(saturate(dot(d, _WM_Lightning.xyz) * 0.5 + 0.5), 4.0));
    return col;
}

// Column thickness of the cloud layer at a world position (0 = clear .. 1 = full thickness). Shared by the
// sky raymarch, cloud shadows and water reflections so all three agree.
float WMCloudShape(float2 xz, float detailAmt)
{
    float2 p = xz + _WM_WindOffset.xy;
    half4 a = SAMPLE_TEXTURE2D_LOD(_WM_CloudNoise, sampler_WM_CloudNoise, p * (1.0 / 4200.0), 0);
    half4 b = SAMPLE_TEXTURE2D_LOD(_WM_CloudNoise, sampler_WM_CloudNoise, p * (1.0 / 1300.0) + float2(0.37, 0.71), 0);
    float base = a.r * 0.48 + a.a * 0.22 + b.g * 0.2 + b.a * 0.1;
    float cov = _WM_Clouds.x;
    float thr = lerp(0.58, 0.08, cov);
    float shape = saturate((base - thr) / lerp(0.16, 0.30, cov));
    // Erode the edges with high-frequency turbulence so silhouettes look billowy.
    shape = saturate(shape * 1.25 - (1.0 - shape) * b.b * 0.9 * detailAmt - 0.05);
    return shape;
}

// Cloud shadow on the ground: project along the main light to the middle of the cloud layer.
half WMCloudShadow(float3 positionWS)
{
    if (_WM_Quality.y < 0.5 || _WM_CloudLight.a <= 0.001) return 1.0;
    float3 L = _WM_SunDir.xyz;
    float alt = _WM_Clouds.z + _WM_Clouds.w * 0.35;
    float t = (alt - positionWS.y) / max(L.y, 0.12);
    float c = WMCloudShape(positionWS.xz + L.xz * t, 0.0);
    return 1.0 - _WM_CloudLight.a * smoothstep(0.05, 0.6, c);
}

// Cheap single-sample cloud colour for reflections (sky-colour approximation for water and wet ground).
half3 WMSkyReflection(float3 R)
{
    float3 d = float3(R.x, max(R.y, 0.0), R.z);
    half3 sky = WMSkyGradient(normalize(d + float3(0, 1e-3, 0)));
    if (R.y > 0.02)
    {
        float camY = GetCameraPositionWS().y;
        float t = (_WM_Clouds.z + _WM_Clouds.w * 0.3 - camY) / R.y;
        float2 p = GetCameraPositionWS().xz + R.xz * t;
        float c = WMCloudShape(p, 0.0) * smoothstep(0.02, 0.2, R.y);
        half3 cloudCol = _WM_CloudAmbient.rgb * 1.1 + _WM_CloudLight.rgb * 0.35;
        sky = lerp(sky, cloudCol, c * 0.85);
    }
    return sky;
}

// ------------------------------------------------------------------ surfaces

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

// Expanding rain-drop rings on wet, up-facing surfaces (returns a tangent-space xz normal offset).
float2 WMRainRipples(float2 p, float intensity)
{
    if (intensity <= 0.01) return 0;
    float2 acc = 0;
    float t = _Time.y;
    [unroll] for (int layer = 0; layer < 2; layer++)
    {
        float2 q = p * (layer == 0 ? 1.7 : 2.9) + layer * 7.31;
        float2 cell = floor(q);
        float2 f = frac(q) - 0.5;
        float h = WMHash2(cell + layer * 19.7);
        float2 o = float2(WMHash2(cell + 3.1), WMHash2(cell + 5.7)) - 0.5;
        float phase = frac(t * (0.9 + h * 0.6) + h);
        float2 dv = f - o * 0.6;
        float r = length(dv);
        float ring = r - phase * 0.45;
        float wave = sin(ring * 60.0) * exp(-ring * ring * 900.0) * (1.0 - phase) * step(h, intensity * 0.9 + 0.1);
        acc += dv / max(r, 1e-3) * wave;
    }
    return acc * 0.35;
}

// Puddle mask: low-frequency noise on flat, sky-exposed tops (only meaningful while wet).
half WMPuddleMask(float3 positionWS, half upness, half sky)
{
    if (_WM_Quality.z < 0.5 || _WM_Precip.z <= 0.01) return 0;
    half4 n = SAMPLE_TEXTURE2D_LOD(_WM_CloudNoise, sampler_WM_CloudNoise, positionWS.xz * (1.0 / 38.0), 0);
    half m = smoothstep(0.62 - _WM_Precip.z * 0.28, 0.72 - _WM_Precip.z * 0.25, n.g * 0.7 + n.a * 0.5);
    return m * saturate(upness * 4.0 - 3.0) * saturate(sky * 3.0 - 1.8) * _WM_Precip.z;
}

// Animated underwater caustics (two scrolling layers of a cell pattern; min() gives the bright web).
half WMCaustics(float3 positionWS)
{
    float t = _Time.y;
    float2 p = positionWS.xz + positionWS.y * 0.3;
    half a = SAMPLE_TEXTURE2D(_WM_Caustics, sampler_WM_Caustics, p * 0.21 + float2(t * 0.031, t * 0.017)).r;
    half b = SAMPLE_TEXTURE2D(_WM_Caustics, sampler_WM_Caustics, p * 0.17 + float2(-t * 0.023, t * 0.029) + 0.5).r;
    return min(a, b);
}

#endif
