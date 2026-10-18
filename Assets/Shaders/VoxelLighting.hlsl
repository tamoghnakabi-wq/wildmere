#ifndef WILDMERE_VOXEL_LIGHTING_INCLUDED
#define WILDMERE_VOXEL_LIGHTING_INCLUDED
// Lighting shared by voxel terrain and props (needs URP Lighting.hlsl included first).

#include "VoxelCommon.hlsl"

// The shared voxel/prop lighting: sun (shadow map, voxel sky visibility, cloud shadow), hemisphere ambient
// scaled by voxel sky light, warm torch light, sky reflection. `occlusion` is the baked (vertex + texture) AO.
half3 WMShadeSurface(half3 albedo, half smoothness, half occlusion, float3 nWS, float3 positionWS, float4 positionCS,
                     half sky, half blk, half reflectBoost)
{
    float2 screenUV = GetNormalizedScreenSpaceUV(positionCS);
    AmbientOcclusionFactor ssao = GetScreenSpaceAmbientOcclusion(screenUV);

    BRDFData brdf;
    half alpha = 1.0;
    InitializeBRDFData(albedo, 0.0, half3(0, 0, 0), smoothness, alpha, brdf);

    float3 V = GetWorldSpaceNormalizeViewDir(positionWS);
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    Light sun = GetMainLight(shadowCoord, positionWS, half4(1, 1, 1, 1));
    // No direct sun where the voxel sky light says there is no path to the sky (caves, under overhangs
    // outside the shadow distance); the shadow map handles everything finer than that.
    half skyVis = smoothstep(0.25, 0.85, sky);
    sun.shadowAttenuation *= skyVis * ssao.directAmbientOcclusion * WMCloudShadow(positionWS);
    half3 color = LightingPhysicallyBased(brdf, sun, nWS, V);

    // Baked voxel AO and SSAO find the same creases; take mostly the stronger of the two instead of their
    // product so corners do not go doubly dark.
    half indirectOcc = lerp(occlusion * ssao.indirectAmbientOcclusion, min(occlusion, ssao.indirectAmbientOcclusion), 0.75);

    // Ambient: hemisphere scaled by sky light (squared for a natural falloff into caves).
    half skyAmb = sky * sky;
    half3 hemi = lerp(_WM_AmbientGround.rgb, _WM_AmbientSky.rgb, saturate(nWS.y * 0.5 + 0.5));
    half3 ambient = hemi * skyAmb + _WM_CaveAmbient.rgb;
    ambient += half3(0.5, 0.55, 0.75) * _WM_Precip.w * skyAmb * 0.6; // lightning flash
    // Torch light: warm, gently flickering, with a soft curve (pockets of light in caves).
    half flicker = 0.9 + 0.1 * sin(_Time.y * 9.0 + WMHash(floor(positionWS)) * 40.0) * sin(_Time.y * 3.3 + positionWS.x);
    half3 torch = _WM_BlockLight.rgb * _WM_BlockLight.a * (pow(blk, 2.4) + pow(blk, 8.0) * 0.6) * flicker;
    color += (ambient * indirectOcc + torch * lerp(1.0, indirectOcc, 0.5)) * brdf.diffuse;

    // Sky reflection (sky gradient towards the reflected ray), occluded by sky access; rough surfaces get
    // little of it, which keeps grazing grass fields from turning cyan.
    float3 R = reflect(-V, nWS);
    half3 env = WMSkyGradient(normalize(float3(R.x, max(R.y, 0.02), R.z))) * skyAmb;
    half NoV = saturate(dot(nWS, V));
    half fres = Pow4(1.0 - NoV);
    half specOcc = saturate(smoothness * smoothness * 1.6 + reflectBoost);
    color += env * EnvironmentBRDFSpecular(brdf, fres) * indirectOcc * specOcc;
    return color;
}

#endif
