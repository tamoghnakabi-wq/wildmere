Shader "Wildmere/Crack"
{
    Properties
    {
        [NoScaleOffset] _CrackTex ("Crack Array", 2DArray) = "" {}
        _Stage ("Stage", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+40" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Blend DstColor Zero Cull Back Offset -2, -2
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D_ARRAY(_CrackTex); SAMPLER(sampler_CrackTex);
            CBUFFER_START(UnityPerMaterial) float _Stage; CBUFFER_END
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            V vert(A v) { V o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); o.uv = v.uv; return o; }
            half4 frag(V i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D_ARRAY(_CrackTex, sampler_CrackTex, i.uv, _Stage);
                // Multiply blend: darken where the crack mask is opaque.
                half k = c.a * 0.85;
                return half4(lerp(half3(1, 1, 1), c.rgb * 0.25, k), 1);
            }
            ENDHLSL
        }
    }
}
