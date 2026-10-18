// Weather effects, all procedural in the vertex shader (see Rendering/WeatherParticles.cs):
//   _Mode 0 rain streaks, 1 snow flakes, 3 rain splashes — quads from a static seed mesh, animated in a box
//   that wraps around the camera in world space and is occluded by the per-column rain-height map;
//   _Mode 2 lightning bolt (LineRenderer geometry, additive HDR core for bloom).
Shader "Wildmere/WeatherFx"
{
    Properties
    {
        _Mode ("Mode", Float) = 0
        _WxParams ("Params", Vector) = (0, 15, 22, 11)
        _WxParams2 ("Params2", Vector) = (0, 1, 0.75, 0.018)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst", Float) = 10
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+20" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite Off Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "VoxelCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Mode;
                float4 _WxParams;    // x: active fraction, y: box half-size (m), z: box height (m), w: fall speed (m/s)
                float4 _WxParams2;   // x: time, y: intensity, z: streak length / flake size, w: streak width
                float _SrcBlend, _DstBlend;
            CBUFFER_END
            TEXTURE2D(_WM_WxHeight);
            float4 _WM_WxLight;      // rgb: particle lighting (ambient + light + flash)
            float4 _WM_WxWind;       // xy: horizontal drift per metre of fall, z: wind strength

            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float4 seed : TEXCOORD1; float4 color : COLOR; };
            struct V
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 data : TEXCOORD1;   // x: alpha, y: phase, z: distance
                float4 color : COLOR;
            };

            float GroundAt(float2 xz)
            {
                int2 t = int2(floor(xz)) & 63;
                return LOAD_TEXTURE2D(_WM_WxHeight, t).r;
            }

            float Wrap(float v, float lo, float size) { return lo + frac((v - lo) / size) * size; }

            V vert(A v)
            {
                V o = (V)0;
                o.uv = v.uv;
                o.color = v.color;
                int mode = (int)round(_Mode);
                if (mode == 2)
                {
                    float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                    o.positionCS = TransformWorldToHClip(ws);
                    o.data = float4(1, 0, distance(ws, GetCameraPositionWS()), 0);
                    return o;
                }
                float4 s = v.seed;
                if (s.w >= _WxParams.x || _WM_Underwater > 0.5) { o.positionCS = float4(0, 0, -2, 1); return o; }
                float3 cam = GetCameraPositionWS();
                float t = _WxParams2.x;
                float hb = _WxParams.y, H = _WxParams.z;
                float3 pos;
                float alpha = _WxParams2.y;
                float3 corner;

                if (mode == 3)
                {
                    // Splash: each slot respawns at a new random column every cycle.
                    float rate = 2.4 + s.z;
                    float cyc = t * rate + s.w * 7.31;
                    float id = floor(cyc);
                    float ph = frac(cyc);
                    float hx = WMHash2(float2(id, s.x * 113.0)), hz = WMHash2(float2(s.y * 97.0, id));
                    float2 xz = cam.xz + (float2(hx, hz) - 0.5) * 2.0 * hb;
                    float g = GroundAt(xz);
                    if (g > 250.0 || g > cam.y + 30.0) { o.positionCS = float4(0, 0, -2, 1); return o; }
                    pos = float3(xz.x, g + 0.03, xz.y);
                    float r = lerp(0.05, 0.3, sqrt(ph));
                    corner = float3((v.uv.x - 0.5) * 2.0 * r, 0, (v.uv.y - 0.5) * 2.0 * r);
                    float d = distance(pos, cam);
                    alpha *= (1.0 - smoothstep(hb * 0.6, hb, length(xz - cam.xz)));
                    o.data = float4(alpha, ph, d, 0);
                    o.positionCS = TransformWorldToHClip(pos + corner);
                    return o;
                }

                float speed = _WxParams.w * (0.8 + 0.4 * s.z);
                float fall = t * speed;
                float2 drift = _WM_WxWind.xy * (mode == 1 ? 3.0 : 1.0);
                float y = Wrap(s.y * H - fall, cam.y - H * 0.42, H);
                float x = Wrap(s.x * 2.0 * hb + drift.x * fall, cam.x - hb, 2.0 * hb);
                float z = Wrap(frac(s.x * 7.13 + s.z * 3.7) * 2.0 * hb + drift.y * fall, cam.z - hb, 2.0 * hb);
                if (mode == 1)
                {
                    // Snow swirls.
                    x += sin(t * 0.9 + s.x * 40.0) * 0.5 + sin(t * 2.3 + s.z * 30.0) * 0.12;
                    z += cos(t * 0.7 + s.y * 40.0) * 0.5;
                }
                pos = float3(x, y, z);
                float g = GroundAt(pos.xz);
                alpha *= saturate((pos.y - g) * 2.0);
                float dist = distance(pos, cam);
                alpha *= (1.0 - smoothstep(hb * 0.65, hb, length(pos.xz - cam.xz))) * smoothstep(0.35, 1.4, dist);
                float3 toCam = normalize(cam - pos);
                if (mode == 0)
                {
                    float3 dir = normalize(float3(drift.x, -1.0, drift.y));
                    float len = _WxParams2.z * (0.75 + 0.5 * s.z);
                    float w = max(_WxParams2.w, dist * 0.0018);
                    alpha *= _WxParams2.w / w;
                    float3 side = normalize(cross(dir, toCam));
                    corner = side * (v.uv.x - 0.5) * w - dir * v.uv.y * len;
                }
                else
                {
                    float size = _WxParams2.z * (0.55 + 0.9 * s.z);
                    size = max(size, dist * 0.0022);
                    float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz;
                    corner = (right * (v.uv.x - 0.5) + up * (v.uv.y - 0.5)) * size;
                }
                o.data = float4(alpha, 0, dist, 0);
                o.positionCS = TransformWorldToHClip(pos + corner);
                return o;
            }

            half4 frag(V i) : SV_Target
            {
                int mode = (int)round(_Mode);
                float2 uv = i.uv;
                if (mode == 2)
                {
                    float x = (uv.y - 0.5) * 2.0;
                    float core = exp(-x * x * 10.0);
                    float glow = exp(-x * x * 2.0) * 0.4;
                    float k = _WxParams2.y;
                    return half4(float3(2.6, 3.0, 4.6) * (core * 3.0 + glow) * k, 1);
                }
                float a = i.data.x;
                if (mode == 0)
                {
                    float across = saturate(1.0 - abs(uv.x * 2.0 - 1.0));
                    a *= pow(across, 1.5) * (1.0 - uv.y * 0.75) * 0.34;
                    return half4(_WM_WxLight.rgb * 1.35 + 0.02, a);
                }
                if (mode == 1)
                {
                    float r = length(uv - 0.5) * 2.0;
                    a *= smoothstep(1.0, 0.35, r) * 0.9;
                    return half4(_WM_WxLight.rgb * 1.6 + 0.03, a);
                }
                // Splash ring with a bright first-frame droplet.
                float r = length(uv - 0.5) * 2.0;
                float ph = i.data.y;
                float ring = smoothstep(0.62, 0.86, r) * smoothstep(1.0, 0.88, r);
                float dot0 = smoothstep(0.45, 0.0, r) * saturate(1.0 - ph * 4.0);
                a *= (ring * 0.55 + dot0 * 0.6) * (1.0 - ph) * (1.0 - ph);
                return half4(_WM_WxLight.rgb * 1.5 + 0.03, a);
            }
            ENDHLSL
        }
    }
}
