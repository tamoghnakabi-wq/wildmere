// Wildmere water. Animated normals (two scrolling maps drifting with the wind, flattened with distance)
// plus rain ripples; depth-based absorption and in-water scattering; refraction of the opaque scene
// texture (WM_REFRACTION, a quality toggle — without it the surface alpha-blends by depth); Fresnel
// reflection of the sky/cloud approximation with sun glint; shoreline foam; seen from below, a Snell's
// window onto the surface. Same aerial perspective as the terrain.
Shader "Wildmere/Water"
{
    Properties
    {
        _WaterNormalA ("Normal A", 2D) = "bump" {}
        _WaterNormalB ("Normal B", 2D) = "bump" {}
        _WaterFoam ("Foam", 2D) = "black" {}
        _ShallowColor ("Scatter (shallow)", Color) = (0.05, 0.24, 0.25, 1)
        _DeepColor ("Scatter (deep)", Color) = (0.015, 0.075, 0.12, 1)
        _Absorb ("Absorption per metre", Vector) = (0.55, 0.2, 0.15, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" }
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ WM_REFRACTION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "VoxelCommon.hlsl"

            TEXTURE2D(_WaterNormalA); SAMPLER(sampler_WaterNormalA);
            TEXTURE2D(_WaterNormalB);
            TEXTURE2D(_WaterFoam); SAMPLER(sampler_WaterFoam);
            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor, _DeepColor, _Absorb;
            CBUFFER_END

            struct A { float4 positionOS : POSITION; float4 normalOS : NORMAL; float4 uv : TEXCOORD0; float4 light : TEXCOORD1; };
            struct V
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half4 light : TEXCOORD2;
            };

            V vert(A v)
            {
                V o;
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                if (v.normalOS.y > 0.5)
                {
                    float t = _WM_WindOffset.z;
                    ws.y += (sin(t * 1.3 + ws.x * 0.6 + ws.z * 0.4) * 0.6 + sin(t * 1.9 - ws.x * 0.31 + ws.z * 0.77) * 0.4) * 0.03 * (0.6 + _WM_Wind.z * 0.4) - 0.02;
                }
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.normalWS = normalize(v.normalOS.xyz);
                o.light = v.light;
                return o;
            }

            float2 SampleWaves(float2 p, float fade)
            {
                float t = _WM_WindOffset.z;
                float2 w = _WM_Wind.xy;
                float2 wp = float2(-w.y, w.x);
                half4 a = SAMPLE_TEXTURE2D(_WaterNormalA, sampler_WaterNormalA, p * 0.09 + w * t * 0.035 + wp * t * 0.006);
                half4 b = SAMPLE_TEXTURE2D(_WaterNormalB, sampler_WaterNormalA, p * 0.21 - wp * t * 0.028 + w * t * 0.02);
                half4 c = SAMPLE_TEXTURE2D(_WaterNormalA, sampler_WaterNormalA, p * 0.023 + w * t * 0.012);
                float2 n = (a.rg * 2 - 1) * 0.55 + (b.rg * 2 - 1) * 0.35 * fade + (c.rg * 2 - 1) * 0.45;
                return n * (0.55 + _WM_Wind.z * 0.3);
            }

            half4 frag(V i, bool front : SV_IsFrontFace) : SV_Target
            {
                bool under = _WM_Underwater > 0.5;
                if (!front && !under) discard;
                float3 eye = GetCameraPositionWS();
                float3 N0 = normalize(i.normalWS) * (front ? 1.0 : -1.0);
                float3 Vd = normalize(eye - i.positionWS);
                float dist = distance(eye, i.positionWS);
                bool top = abs(N0.y) > 0.5;

                float fade = 1.0 - saturate(dist / 90.0);
                float2 n2 = top ? SampleWaves(i.positionWS.xz, fade) : float2(0, 0);
                n2 *= lerp(0.35, 1.0, fade);
                n2 += WMRainRipples(i.positionWS.xz, _WM_Precip.x) * 2.2 * fade;
                float3 N = top ? normalize(float3(n2.x, N0.y > 0 ? 1.0 : -1.0, n2.y)) : N0;

                half sky = i.light.x;
                half skyAmb = sky * sky;
                float2 suv = GetNormalizedScreenSpaceUV(i.positionCS);
                float surfEye = LinearEyeDepth(i.positionCS.z, _ZBufferParams);

                // In-water scattering colour, lit by sky ambient and the sun.
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light sun = GetMainLight(shadowCoord, i.positionWS, half4(1, 1, 1, 1));
                half shadow = sun.shadowAttenuation * smoothstep(0.25, 0.85, sky) * WMCloudShadow(i.positionWS);
                half3 ambient = _WM_AmbientSky.rgb * skyAmb + _WM_CaveAmbient.rgb;
                half3 scatterShallow = _ShallowColor.rgb * (ambient * 0.9 + sun.color * shadow * saturate(sun.direction.y) * 0.35);
                half3 scatterDeep = _DeepColor.rgb * (ambient + sun.color * shadow * 0.08);

                // Water thickness behind the surface along the view ray.
                float rawScene = SampleSceneDepth(suv);
                float sceneEye = LinearEyeDepth(rawScene, _ZBufferParams);
                float thick = max(0.0, sceneEye - surfEye);
                float3 scenePos = ComputeWorldSpacePosition(suv, rawScene, UNITY_MATRIX_I_VP);
                float vdepth = max(0.0, i.positionWS.y - scenePos.y);   // vertical water depth (for foam)

                half3 below;
                half alpha;
            #if defined(WM_REFRACTION)
                float2 ruv = suv + N.xz * (0.035 / max(surfEye * 0.12, 1.0)) * saturate(thick * 0.6);
                float rawR = SampleSceneDepth(ruv);
                float sceneEyeR = LinearEyeDepth(rawR, _ZBufferParams);
                if (sceneEyeR < surfEye) { ruv = suv; sceneEyeR = sceneEye; }    // never refract things in front
                float path = under ? 0.0 : max(0.0, sceneEyeR - surfEye) * 1.15;
                half3 refr = SampleSceneColor(ruv);
                float3 T = exp(-_Absorb.xyz * path);
                half deepK = 1.0 - exp(-path * 0.12);
                below = refr * T + lerp(scatterShallow, scatterDeep, deepK) * (1.0 - T);
                alpha = 1.0;
            #else
                float path = thick * 1.15;
                float3 T = exp(-_Absorb.xyz * path);
                half deepK = 1.0 - exp(-path * 0.12);
                below = lerp(scatterShallow, scatterDeep, deepK);
                alpha = saturate(1.0 - dot(T, float3(0.25, 0.45, 0.3)) * 0.85);
                alpha = lerp(alpha, 1.0, saturate(dist / 140.0));
            #endif

                // Reflection: sky + clouds approximation, occluded where there is no sky (caves), and sun glint.
                float NoV = saturate(dot(N, Vd));
                half fres = 0.02 + 0.98 * pow(1.0 - NoV, 5.0);
                float3 R = reflect(-Vd, N);
                half3 refl = WMSkyReflection(R) * lerp(0.08, 1.0, skyAmb);
                float3 H = normalize(sun.direction + Vd);
                float NoH = saturate(dot(N, H));
                half glint = (pow(NoH, 900.0) * 18.0 + pow(NoH, 120.0) * 0.9) * shadow * saturate(sun.direction.y * 6.0);
                half3 color = lerp(below, refl, fres) + sun.color * glint;
            #if !defined(WM_REFRACTION)
                alpha = saturate(alpha + fres * 0.6 + glint);
            #endif

                // Shoreline foam: thin water against blocks, animated, lit like the surface.
                if (top && !under)
                {
                    float2 fp = i.positionWS.xz * 0.45 + _WM_Wind.xy * _WM_WindOffset.z * 0.03;
                    half f1 = SAMPLE_TEXTURE2D(_WaterFoam, sampler_WaterFoam, fp).r;
                    half f2 = SAMPLE_TEXTURE2D(_WaterFoam, sampler_WaterFoam, fp * 0.63 + 0.37 - _WM_Wind.yx * _WM_WindOffset.z * 0.02).r;
                    half shore = 1.0 - smoothstep(0.0, 0.55, vdepth);
                    half foam = saturate((f1 * 0.6 + f2 * 0.6) * shore * 1.6 - (1.0 - shore) * 0.3) * fade;
                    half3 foamCol = ambient * 0.9 + sun.color * shadow * saturate(sun.direction.y) * 0.8;
                    color = lerp(color, foamCol, foam * 0.85);
                #if !defined(WM_REFRACTION)
                    alpha = max(alpha, foam);
                #endif
                }

                if (under)
                {
                    // Looking up at the surface from below: Snell's window, total internal reflection outside it.
                    half window = smoothstep(0.62, 0.75, abs(dot(Vd, float3(0, 1, 0))));
                #if defined(WM_REFRACTION)
                    half3 through = SampleSceneColor(suv + N.xz * 0.02) * 1.3;
                #else
                    half3 through = _WM_SkyZenith.rgb + _WM_AmbientSky.rgb * 0.5;
                #endif
                    color = lerp(_WM_UnderwaterColor.rgb * 1.4, through, window);
                    alpha = 0.92;
                }
                color = WMApplyFog(color, i.positionWS);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
