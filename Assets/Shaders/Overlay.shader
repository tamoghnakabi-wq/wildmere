// Small unlit helpers: block selection outline (Wildmere/Outline) and break-progress cracks
// (Wildmere/Crack, samples the crack texture array by stage).
Shader "Wildmere/Outline"
{
    Properties { _Color ("Color", Color) = (0, 0, 0, 0.55) }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+50" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Cull Off Offset -1, -1
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial) float4 _Color; CBUFFER_END
            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; };
            V vert(A v) { V o; o.positionCS = TransformObjectToHClip(v.positionOS.xyz); return o; }
            half4 frag(V i) : SV_Target { return _Color; }
            ENDHLSL
        }
    }
}
