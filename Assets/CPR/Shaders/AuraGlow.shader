// Soft rim-light "aura" for the patient outline. Brightest at the silhouette edges,
// nearly clear in the middle, gently pulsing. Additive, so it reads as light over passthrough.
// Built-in render pipeline, Quest single-pass stereo safe.
Shader "CPR/AuraGlow"
{
    Properties
    {
        _Color ("Color", Color) = (0.35, 0.8, 1, 1)
        _Power ("Edge Softness (higher = thinner edge)", Range(0.5, 8)) = 2.5
        _Intensity ("Intensity", Range(0, 3)) = 0.9
        _Inner ("Inner Fill", Range(0, 1)) = 0.06
        _PulseSpeed ("Pulse Speed", Float) = 1.5
        _PulseAmount ("Pulse Amount", Range(0, 1)) = 0.2
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend SrcAlpha One
            ZWrite Off
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Power, _Intensity, _Inner, _PulseSpeed, _PulseAmount;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = WorldSpaceViewDir(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float facing = saturate(abs(dot(normalize(i.worldNormal), normalize(i.viewDir))));
                float rim = pow(1.0 - facing, _Power);
                float pulse = 1.0 - _PulseAmount * (0.5 + 0.5 * sin(_Time.y * _PulseSpeed));
                float a = saturate((rim + _Inner) * _Intensity * pulse) * _Color.a;
                return fixed4(_Color.rgb, a);
            }
            ENDCG
        }
    }
}
