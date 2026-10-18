// Offscreen icon renderer for the Wildmere UI (lives in Resources so builds always include it).
// Vertices arrive already in clip space (the C# side does the isometric projection), so no camera,
// lights or depth are involved: faces are drawn back-to-front with premultiplied alpha.
//   Pass 0: block faces sampled from the voxel texture array (layer in uv.z, VFlags in uv.w)
//   Pass 1: a sprite (tool/item icons) for a uniform, mipmapped, drop-shadowed atlas
Shader "Hidden/Wildmere/UIIcon"
{
    Properties
    {
        _MainTex ("Sprite", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Cull Off ZWrite Off ZTest Always
        Blend One OneMinusSrcAlpha

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D_ARRAY(_BlockAlbedo); SAMPLER(sampler_BlockAlbedo);
        TEXTURE2D_ARRAY(_BlockMask);
        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
        float4 _GrassTint;
        float4 _FoliageTint;
        float _Cutoff;
        float _Glass;
        float _FlipY;

        struct IconAttributes { float4 pos : POSITION; float4 color : COLOR; float4 uv : TEXCOORD0; };
        struct IconVaryings { float4 pos : SV_POSITION; float4 color : COLOR; float4 uv : TEXCOORD0; };

        IconVaryings IconVert(IconAttributes i)
        {
            IconVaryings o;
            o.pos = float4(i.pos.x, i.pos.y * _FlipY, 0.5, 1.0);
            o.color = i.color;
            o.uv = i.uv;
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "Block"
            HLSLPROGRAM
            #pragma vertex IconVert
            #pragma fragment BlockFrag
            half4 BlockFrag(IconVaryings i) : SV_Target
            {
                float2 uv = clamp(i.uv.xy, 0.002, 0.998);
                uint flags = (uint)round(i.uv.w);
                half4 a = SAMPLE_TEXTURE2D_ARRAY(_BlockAlbedo, sampler_BlockAlbedo, uv, i.uv.z);
                clip(a.a - _Cutoff);
                half4 m = SAMPLE_TEXTURE2D_ARRAY(_BlockMask, sampler_BlockAlbedo, uv, i.uv.z);
                half3 c = a.rgb;
                if ((flags & 8u) != 0u) c *= lerp(half3(1, 1, 1), _GrassTint.rgb * 1.7, m.a);
                if ((flags & 16u) != 0u) c *= lerp(half3(1, 1, 1), _FoliageTint.rgb * 1.7, m.a);
                c *= ((flags & 1u) != 0u) ? 1.12 : i.color.rgb;
                half alpha = _Cutoff > 0.01 ? 1.0 : (_Glass > 0.5 ? saturate(a.a * 1.25 + 0.14) : a.a);
                return half4(c * alpha, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Sprite"
            HLSLPROGRAM
            #pragma vertex IconVert
            #pragma fragment SpriteFrag
            half4 SpriteFrag(IconVaryings i) : SV_Target
            {
                half4 t = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv.xy);
                return half4(t.rgb * t.a, t.a);
            }
            ENDHLSL
        }
    }
}
