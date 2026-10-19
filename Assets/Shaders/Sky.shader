// Wildmere procedural sky: zenith/horizon gradient with sun-ward scattering, sun disc with limb darkening,
// moon disc with soft maria, twinkling stars, and a 2.5D cloud layer raymarched through a heightfield
// (flat bases, billowy tops) lit by the sun with self-shadowing, powder darkening and a silver lining.
// Coverage/darkness/drift come from the weather system; the horizon blends into the terrain haze.
Shader "Wildmere/Sky"
{
    Properties { }
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

            
            float3 Stars(float3 d, float up)
            {
                if (_WM_Stars <= 0.01 || up <= 0) return 0;
                float3 acc = 0;
                [unroll] for (int k = 0; k < 2; k++)
                {
                    float scale = k == 0 ? 260.0 : 520.0;
                    float3 sp = d * scale;
                    float3 cell = floor(sp);
                    float r = Hash31(cell + k * 17.0);
                    float3 f = frac(sp) - 0.5;
                    float thresh = k == 0 ? 0.9955 : 0.9975;
                    float tw = 0.65 + 0.35 * sin(_Time.y * (1.5 + r * 5.0) + r * 60.0);
                    float s = step(thresh, r) * smoothstep(0.34, 0.0, length(f)) * tw;
                    float temp = frac(r * 91.7);
                    float3 tint = lerp(float3(1.0, 0.82, 0.65), float3(0.75, 0.85, 1.0), temp);
                    acc += s * tint * (k == 0 ? 3.0 : 1.4);
                }
                return acc * _WM_Stars * smoothstep(0.0, 0.3, up);
            }

            // Cloud layer: one intersection with the middle of the cloud slab, so every cloud appears exactly once.
            // Thickness is conveyed by lighting (self-shadowing towards the light, powder-dark cores, silver edges)
            // rather than by stepping through the slab: a 2-4 step "first hit" march drew several offset copies of
            // each cloud (a sliced, multi-layered look), and a density march turned the noise peaks into spikes.
            // Returns (in-scatter, transmittance).
            float4 Clouds(float3 d, float3 camPos, float2 pixel)
            {
                if (d.y < 0.004) return float4(0, 0, 0, 1);
                float h0 = _WM_Clouds.z, thick = _WM_Clouds.w;
                float camY = min(camPos.y, h0 - 2.0);
                int q = (int)clamp(_WM_CloudAmbient.a, 1, 12);
                float tA = (h0 + thick * 0.5 - camY) / d.y;
                float2 hitXZ = camPos.xz + d.xz * tA;
                float shape = WMCloudShape(hitXZ, 1.0);
                float alpha = smoothstep(0.0, 0.32, shape);
                if (alpha <= 0.003) return float4(0, 0, 0, 1);
                // "Height" inside the puff for ambient shading: thicker columns are brighter on top.
                float hf = saturate(shape * 0.8 + 0.1);

                float3 L = _WM_RealSunDir.y > -0.05 ? _WM_RealSunDir.xyz : _WM_MoonDir.xyz;
                float c = dot(d, L);
                // Self-shadowing: how much cloud lies between the (density-weighted) hit and the light.
                float2 lstep = L.xz / max(L.y, 0.2) * thick * 0.22;
                float occ = WMCloudShape(hitXZ + lstep, 0.0);
                if (q > 4) occ = occ * 0.6 + WMCloudShape(hitXZ + lstep * 2.2, 0.0) * 0.4;
                float depth = shape * (1.0 + _WM_Clouds.y * 1.5);
                float beer = exp(-(occ * 2.2 + depth * 1.2) * (1.0 + _WM_Clouds.y));
                float powder = 1.0 - exp(-shape * 5.0);
                // Silver lining: thin edges glow when looking towards the light.
                float edge = 1.0 - smoothstep(0.0, 0.45, shape);
                float forward = pow(saturate(c), 8.0) * 1.8 + pow(saturate(c), 64.0) * 3.0;
                float3 sunL = _WM_CloudLight.rgb * (beer * lerp(1.0, powder, 0.5) * (0.65 + 0.35 * saturate(c * 0.5 + 0.5)) + edge * forward * 0.9);
                float3 amb = _WM_CloudAmbient.rgb * lerp(0.62, 1.12, saturate(hf + (1.0 - shape) * 0.5)) * (1.0 - _WM_Clouds.y * 0.5);
                float3 colr = sunL + amb;
                // Lightning glows inside the clouds.
                colr += float3(0.5, 0.55, 0.75) * _WM_Lightning.w * (0.2 + 1.6 * pow(saturate(dot(d, _WM_Lightning.xyz) * 0.5 + 0.5), 10.0)) * (0.4 + shape);
                alpha *= lerp(0.92, 1.0, _WM_Clouds.y);
                // Far clouds fade into the horizon haze.
                float fade = exp(-tA / 12000.0) * smoothstep(0.004, 0.07, d.y) * (1.0 - _WM_Surface.y * 0.65);
                alpha *= fade;
                return float4(colr * alpha, 1.0 - alpha);
            }

            half4 frag(V i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float up = d.y;
                if (_WM_Underwater > 0.5)
                {
                    // Looking up from under water: bright Snell's window, dark everywhere else.
                    float win = smoothstep(0.55, 0.8, up);
                    return half4(lerp(_WM_UnderwaterColor.rgb, _WM_UnderwaterColor.rgb * 3.0 + _WM_SkyZenith.rgb * 0.3, win), 1);
                }
                float3 col = WMSkyGradient(d);

                // Stars and moon behind everything else.
                col += Stars(d, up);
                float cm = dot(d, _WM_MoonDir.xyz);
                float moonDisc = smoothstep(0.99955, 0.99975, cm);
                if (moonDisc > 0)
                {
                    float3 md = _WM_MoonDir.xyz;
                    float3 mt = normalize(cross(md, float3(0, 1, 0.001)));
                    float3 mb = cross(mt, md);
                    float2 muv = float2(dot(d, mt), dot(d, mb)) * 60.0;
                    half maria = SAMPLE_TEXTURE2D_LOD(_WM_CloudNoise, sampler_WM_CloudNoise, muv * 0.8 + 0.3, 0).a;
                    col += float3(0.82, 0.86, 0.95) * moonDisc * (2.2 + maria * 1.2) * _WM_MoonDir.w;
                }
                col += float3(0.25, 0.3, 0.42) * (pow(saturate(cm), 90.0) * 0.6 + pow(saturate(cm), 12.0) * 0.06) * _WM_MoonDir.w;

                // Sun disc with limb darkening (bloom picks it up).
                float cs = dot(d, _WM_RealSunDir.xyz);
                float vis = _WM_SunColor.a;
                float disc = smoothstep(0.99955, 0.99972, cs);
                float limb = sqrt(saturate((cs - 0.99955) / 0.00045));
                col += _WM_SunColor.rgb * disc * (14.0 + 18.0 * limb) * vis;

                // Clouds on top.
                float4 cl = Clouds(d, GetCameraPositionWS(), i.positionCS.xy);
                col = col * cl.a + cl.rgb;

                // Horizon band: haze below and right at the horizon (matches distant terrain fog).
                float haze = max(1.0 - smoothstep(-0.02, 0.1 + _WM_Surface.y * 0.6, up), _WM_Surface.y * 0.45);
                col = lerp(col, WMFogColorDir(normalize(float3(d.x, 0.0, d.z))), haze * 0.85);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
