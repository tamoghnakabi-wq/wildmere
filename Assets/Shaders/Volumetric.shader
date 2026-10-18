// Shadow-mapped volumetric light (see Rendering/VolumetricLight.cs).
// Pass 0 ray-marches the main light's cascaded shadow map through the haze + mist density field at reduced
// resolution; pass 1 blurs that result and adds it onto the camera colour.
Shader "Hidden/Wildmere/Volumetric"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "VoxelCommon.hlsl"

        float4 _WM_VolParams;   // x: max march distance, y: steps, z: strength, w: density boost
        float4 _WM_VolTint;     // rgb: tint
        float4 _WM_VolTexel;    // xy: 1/size of the low-res buffer
        float4 _WM_LightColor;  // rgb: main light colour * intensity

        float ShadowAt(float3 p)
        {
            half cascade = ComputeCascadeIndex(p);
            float4 sc = mul(_MainLightWorldToShadow[cascade], float4(p, 1.0));
            if (BEYOND_SHADOW_FAR(sc)) return 1.0;   // beyond every cascade URP's no-op matrix lands here too
            return SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmapTexture, sampler_LinearClampCompare, sc.xyz);
        }

        float DensityAt(float y)
        {
            float haze = _WM_Fog.x * exp(-_WM_Fog.y * max(y - _WM_Fog.z, -20.0)) * _WM_VolParams.w;
            float mist = _WM_Mist.x * exp(-_WM_Mist.y * max(y - _WM_Mist.z, -6.0));
            return haze + mist;
        }

        half4 FragMarch(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            float raw = SampleSceneDepth(uv);
            float3 cam = GetCameraPositionWS();
        #if UNITY_REVERSED_Z
            bool sky = raw <= 1e-6;
        #else
            bool sky = raw >= 1.0 - 1e-6;
        #endif
            float3 posWS = ComputeWorldSpacePosition(uv, sky ? (UNITY_REVERSED_Z ? 1e-5 : 1.0 - 1e-5) : raw, UNITY_MATRIX_I_VP);
            float3 v = posWS - cam;
            float dist = length(v);
            float3 dir = v / max(dist, 1e-4);
            dist = sky ? _WM_VolParams.x : min(dist, _WM_VolParams.x);

            int steps = (int)_WM_VolParams.y;
            float stepLen = dist / steps;
            float jitter = WMIGN(input.positionCS.xy);
            float3 L = _WM_SunDir.xyz;
            float cosT = dot(dir, L);
            bool under = _WM_Underwater > 0.5;

            float accum = 0;
            float od = 0;
            [loop] for (int i = 0; i < steps; i++)
            {
                float t = (i + jitter) * stepLen;
                float3 p = cam + dir * t;
                float dens = under ? 0.09 : DensityAt(p.y);
                float sh = ShadowAt(p);
                accum += sh * dens * exp(-od) * stepLen;
                od += dens * stepLen;
            }
            float phase;
            if (under)
                phase = 0.25 + 2.5 * pow(saturate(cosT), 6.0);
            else
                phase = 0.12 + 1.1 * pow(saturate(cosT * 0.5 + 0.5), 8.0) + 2.2 * pow(saturate(cosT), 32.0);
            float3 col = _WM_LightColor.rgb * _WM_VolTint.rgb * phase * accum * _WM_VolParams.z;
            if (under) col *= float3(0.35, 0.8, 0.9);
            return half4(col, 1);
        }

        half4 FragComposite(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            float2 o = _WM_VolTexel.xy;
            // 9-tap tent blur hides the per-pixel dither of the low-res march.
            half3 c = SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb * 4.0;
            c += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(o.x, 0) * 1.5, 0).rgb * 2.0;
            c += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv - float2(o.x, 0) * 1.5, 0).rgb * 2.0;
            c += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(0, o.y) * 1.5, 0).rgb * 2.0;
            c += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv - float2(0, o.y) * 1.5, 0).rgb * 2.0;
            c += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv + o * 1.5, 0).rgb;
            c += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv - o * 1.5, 0).rgb;
            c += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(o.x, -o.y) * 1.5, 0).rgb;
            c += SAMPLE_TEXTURE2D_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x, o.y) * 1.5, 0).rgb;
            return half4(c / 16.0, 0);
        }
        ENDHLSL

        Pass
        {
            Name "March"
            Blend Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragMarch
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            Blend One One
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}
