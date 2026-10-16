// Wildmere water (baseline written by the lead; the Graphics module owns and extends it).
// Transparent surface with depth-based colour/opacity, animated normals, sky reflection with
// fresnel, sun glint and shoreline foam.
Shader "Wildmere/Water"
{
    Properties
    {
        _WaterNormalA ("Normal A", 2D) = "bump" {}
        _WaterNormalB ("Normal B", 2D) = "bump" {}
        _ShallowColor ("Shallow", Color) = (0.10, 0.42, 0.45, 1)
        _DeepColor ("Deep", Color) = (0.02, 0.10, 0.20, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" }
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Back

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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "VoxelCommon.hlsl"

            TEXTURE2D(_WaterNormalA); SAMPLER(sampler_WaterNormalA);
            TEXTURE2D(_WaterNormalB);
            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor, _DeepColor;
            CBUFFER_END

            struct A { float4 positionOS : POSITION; float4 normalOS : NORMAL; float4 uv : TEXCOORD0; float4 light : TEXCOORD1; };
            struct V
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half4 light : TEXCOORD2;
                float4 screen : TEXCOORD3;
            };

            V vert(A v)
            {
                V o;
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                if (v.normalOS.y > 0.5) ws.y += sin(_Time.y * 1.3 + ws.x * 0.6 + ws.z * 0.4) * 0.025 - 0.02;
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.normalWS = normalize(v.normalOS.xyz);
                o.light = v.light;
                o.screen = ComputeScreenPos(o.positionCS);
                return o;
            }

            half3 SampleWaves(float2 p)
            {
                float t = _Time.y;
                half4 a = SAMPLE_TEXTURE2D(_WaterNormalA, sampler_WaterNormalA, p * 0.11 + float2(t * 0.020, t * 0.013));
                half4 b = SAMPLE_TEXTURE2D(_WaterNormalB, sampler_WaterNormalA, p * 0.23 + float2(-t * 0.017, t * 0.024));
                half2 n = (a.rg * 2 - 1) * 0.6 + (b.rg * 2 - 1) * 0.4;
                return normalize(half3(n.x, 1.0, n.y));
            }

            half4 frag(V i) : SV_Target
            {
                float3 N0 = normalize(i.normalWS);
                float3 N = N0.y > 0.5 ? SampleWaves(i.positionWS.xz) : N0;
                float3 Vd = GetWorldSpaceNormalizeViewDir(i.positionWS);

                // Water depth behind the surface (scene depth minus surface depth along the view ray).
                float2 suv = i.screen.xy / i.screen.w;
                float sceneZ = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                float surfZ = LinearEyeDepth(i.positionCS.z, _ZBufferParams);
                float depth = max(0.0, sceneZ - surfZ);
                float deep = 1.0 - exp(-depth * 0.28);

                half sky = i.light.x;
                half skyAmb = sky * sky;
                half3 ambient = lerp(_WM_AmbientGround.rgb, _WM_AmbientSky.rgb, 0.8) * skyAmb + _WM_CaveAmbient.rgb;
                half3 body = lerp(_ShallowColor.rgb, _DeepColor.rgb, deep) * (ambient + 0.15);

                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light sun = GetMainLight(shadowCoord, i.positionWS, half4(1, 1, 1, 1));
                half shadow = sun.shadowAttenuation * smoothstep(0.25, 0.85, sky);
                body += _ShallowColor.rgb * sun.color * saturate(dot(N, sun.direction)) * shadow * 0.35 * (1 - deep);

                float3 R = reflect(-Vd, N);
                half3 skyCol = lerp(_WM_FogColor.rgb, _WM_AmbientSky.rgb * 1.6, saturate(R.y * 1.5)) * max(skyAmb, 0.15);
                half fres = 0.02 + 0.98 * pow(1.0 - saturate(dot(N, Vd)), 5.0);
                float3 H = normalize(sun.direction + Vd);
                half spec = pow(saturate(dot(N, H)), 380.0) * 6.0 * shadow;

                half3 color = lerp(body, skyCol, fres) + sun.color * spec;
                half foam = saturate(1.0 - depth * 2.2) * (0.55 + 0.45 * sin(_Time.y * 2.0 + i.positionWS.x * 1.7 + i.positionWS.z * 1.3));
                color = lerp(color, ambient + sun.color * shadow * 0.5, foam * 0.35);
                half alpha = saturate(lerp(0.35, 0.92, deep) + fres * 0.5 + foam * 0.25);
                if (_WM_Underwater > 0.5) { alpha = 0.85; color = lerp(color, _WM_UnderwaterColor.rgb, 0.5); }
                color = WMApplyFog(color, i.positionWS);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
