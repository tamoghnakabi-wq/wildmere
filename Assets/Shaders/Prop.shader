// Wildmere props / wildlife / held tools (baseline by the lead; Graphics owns and extends it).
// Albedo from sRGB vertex colours, optional triplanar detail from the block texture array, the
// same sun/ambient/fog model as the voxel terrain. Per-instance sky light via _SkyLight.
Shader "Wildmere/Prop"
{
    Properties
    {
        _DetailLayer ("Detail Layer", Float) = -1
        _DetailAmount ("Detail Amount", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.25
        _Emission ("Emission", Float) = 0
        _Wind ("Wind", Float) = 0
        _SkyLight ("Sky Light", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _DetailLayer, _DetailAmount, _Smoothness, _Emission, _Wind, _SkyLight;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "VoxelCommon.hlsl"

            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; half3 normalWS : TEXCOORD1; half4 color : TEXCOORD2; UNITY_VERTEX_INPUT_INSTANCE_ID };

            V vert(A v)
            {
                V o = (V)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                if (_Wind > 0)
                {
                    float h = saturate(v.positionOS.y);
                    float t = _Time.y;
                    ws.xz += _WM_Wind.xy * sin(t * 1.7 + dot(ws.xz, float2(0.3, 0.2))) * 0.06 * h * _WM_Wind.z * _Wind;
                }
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color;
                return o;
            }

            half4 frag(V i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 N = normalize(i.normalWS);
                half3 albedo = SRGBToLinear(saturate(i.color.rgb));
                if (_DetailLayer >= 0 && _DetailAmount > 0)
                {
                    float3 an = pow(abs(N), 4.0); an /= (an.x + an.y + an.z);
                    float3 p = i.positionWS;
                    half3 dx = SAMPLE_TEXTURE2D_ARRAY(_BlockAlbedo, sampler_BlockAlbedo, p.zy, _DetailLayer).rgb;
                    half3 dy = SAMPLE_TEXTURE2D_ARRAY(_BlockAlbedo, sampler_BlockAlbedo, p.xz, _DetailLayer).rgb;
                    half3 dz = SAMPLE_TEXTURE2D_ARRAY(_BlockAlbedo, sampler_BlockAlbedo, p.xy, _DetailLayer).rgb;
                    half3 d = dx * an.x + dy * an.y + dz * an.z;
                    half lum = Luminance(d);
                    albedo *= lerp(1.0, saturate(lum / 0.45), _DetailAmount);
                }

                BRDFData brdf; half alpha = 1;
                InitializeBRDFData(albedo, 0, half3(0, 0, 0), _Smoothness, alpha, brdf);
                float3 Vd = GetWorldSpaceNormalizeViewDir(i.positionWS);
                float2 suv = GetNormalizedScreenSpaceUV(i.positionCS);
                AmbientOcclusionFactor ssao = GetScreenSpaceAmbientOcclusion(suv);
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS), i.positionWS, half4(1, 1, 1, 1));
                sun.shadowAttenuation *= smoothstep(0.25, 0.85, _SkyLight) * ssao.directAmbientOcclusion;
                half3 color = LightingPhysicallyBased(brdf, sun, N, Vd);
                half sky2 = _SkyLight * _SkyLight;
                half3 hemi = lerp(_WM_AmbientGround.rgb, _WM_AmbientSky.rgb, saturate(N.y * 0.5 + 0.5));
                color += (hemi * sky2 + _WM_CaveAmbient.rgb) * brdf.diffuse * ssao.indirectAmbientOcclusion;
                color += albedo * _Emission;
                color = WMApplyFog(color, i.positionWS);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection; float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; };
            V vert(A v)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(v);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
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
                return o;
            }
            half4 frag(V i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float3 n : TEXCOORD0; };
            V vert(A v) { V o; UNITY_SETUP_INSTANCE_ID(v); o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.n = TransformObjectToWorldNormal(v.normalOS); return o; }
            half4 frag(V i) : SV_Target { return half4(normalize(i.n), 0); }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct A { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; };
            V vert(A v) { V o; UNITY_SETUP_INSTANCE_ID(v); o.positionCS = TransformObjectToHClip(v.positionOS.xyz); return o; }
            half4 frag(V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
