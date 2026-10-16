// Wildmere procedural sky (baseline by the lead; the Graphics module owns and extends it).
// Gradient sky, sun and moon discs with glow, twinkling stars, and drifting 2.5D clouds lit by
// the sun. The horizon blends into the same haze colour the terrain fog uses.
Shader "Wildmere/Sky"
{
    Properties
    {
        _CloudNoise ("Cloud Noise", 2D) = "gray" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "VoxelCommon.hlsl"

            TEXTURE2D(_CloudNoise); SAMPLER(sampler_CloudNoise);
            float4 _WM_SkyZenith;     // rgb
            float4 _WM_SkyHorizon;    // rgb
            float4 _WM_SunColor;      // rgb: sun disc/glow colour, a: sun visibility
            float4 _WM_MoonDir;       // xyz towards the moon, w: moon visibility
            float4 _WM_RealSunDir;    // xyz towards the sun (even at night)
            float4 _WM_Clouds;        // x: coverage 0..1, y: density/darkness, z: speed, w: brightness
            float  _WM_Stars;         // 0..1

            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            V vert(A v)
            {
                V o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }

            float Hash31(float3 p) { p = frac(p * 0.1031); p += dot(p, p.yzx + 33.33); return frac((p.x + p.y) * p.z); }

            float CloudField(float2 p)
            {
                float t = _Time.y * _WM_Clouds.z;
                half4 a = SAMPLE_TEXTURE2D_LOD(_CloudNoise, sampler_CloudNoise, p * 0.00035 + float2(t * 0.0012, t * 0.0005), 0);
                half4 b = SAMPLE_TEXTURE2D_LOD(_CloudNoise, sampler_CloudNoise, p * 0.0011 + float2(t * 0.0021, -t * 0.0009), 0);
                float n = a.r * 0.55 + a.g * 0.25 + b.b * 0.2;
                return smoothstep(1.0 - _WM_Clouds.x, 1.0 - _WM_Clouds.x + 0.35, n);
            }

            half4 frag(V i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float up = d.y;
                float h = saturate(up);
                float3 col = lerp(_WM_SkyHorizon.rgb, _WM_SkyZenith.rgb, pow(h, 0.45));

                // Sun glow (Mie-like) and disc.
                float3 sunDir = _WM_RealSunDir.xyz;
                float cs = dot(d, sunDir);
                float vis = _WM_SunColor.a;
                col += _WM_SunColor.rgb * (pow(saturate(cs), 8.0) * 0.35 + pow(saturate(cs), 64.0) * 0.6) * vis;
                col += _WM_SunColor.rgb * smoothstep(0.9993, 0.99965, cs) * 24.0 * vis;

                // Stars.
                float stars = 0;
                if (_WM_Stars > 0.01 && up > 0)
                {
                    float3 sp = d * 300.0;
                    float3 cell = floor(sp);
                    float r = Hash31(cell);
                    float3 f = frac(sp) - 0.5;
                    float tw = 0.7 + 0.3 * sin(_Time.y * (2.0 + r * 4.0) + r * 60.0);
                    stars = step(0.9965, r) * smoothstep(0.32, 0.0, length(f)) * tw * _WM_Stars * smoothstep(0.0, 0.25, up);
                    col += stars * float3(0.9, 0.95, 1.0) * 2.5;
                }

                // Moon.
                float cm = dot(d, _WM_MoonDir.xyz);
                col += float3(0.75, 0.82, 0.95) * smoothstep(0.99955, 0.9998, cm) * 3.0 * _WM_MoonDir.w;
                col += float3(0.2, 0.25, 0.35) * pow(saturate(cm), 64.0) * 0.4 * _WM_MoonDir.w;

                // Clouds on a curved plane above the camera.
                if (up > 0.0)
                {
                    float dist = 900.0 / (up + 0.08);
                    float2 p = d.xz * dist + GetCameraPositionWS().xz;
                    float c = CloudField(p);
                    // Light the cloud: brighter toward the sun, darker undersides as coverage grows.
                    float lightAmt = saturate(0.55 + 0.45 * dot(normalize(float3(d.x, 0.3, d.z)), sunDir));
                    float3 lit = lerp(_WM_SkyHorizon.rgb * 0.9, _WM_SunColor.rgb * 0.6 + _WM_SkyZenith.rgb * 0.5, lightAmt) * _WM_Clouds.w;
                    float3 cloudCol = lerp(lit, lit * (1.0 - _WM_Clouds.y * 0.65), c * c);
                    float fade = smoothstep(0.0, 0.18, up);
                    col = lerp(col, cloudCol, c * fade * 0.92);
                }

                // Below/at the horizon: haze colour so distant terrain fog meets the sky seamlessly.
                float haze = 1.0 - smoothstep(-0.02, 0.22, up);
                float3 fogCol = lerp(_WM_FogColor.rgb, _WM_FogSunColor.rgb, pow(saturate(dot(normalize(float3(d.x, 0, d.z)), sunDir)), 5.0));
                col = lerp(col, fogCol, haze);
                if (_WM_Underwater > 0.5) col = _WM_UnderwaterColor.rgb;
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
