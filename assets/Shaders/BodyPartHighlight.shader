// Body-part highlight shader for the MG-MotionLLM Unity demo.
//
// Think of this as a normal lit Material (albedo from _MainTex/_Color, like the SMPL sample
// material) with an extra EMISSIVE input. The emissive is masked per vertex by a "glow"
// value the C# side (BodyPartHighlighter) bakes into the mesh's vertex-colour ALPHA each
// frame: 0 = no glow, 1 = full glow. Emission = _HighlightColor * glow * _EmissionStrength.
//
// With _EmissionStrength > 1 the emission goes into HDR, so a Bloom post-process (Post-
// Processing Stack) makes the highlighted body part actually glow. Without Bloom it still
// shows as a bright coloured region, just not glowing.
Shader "MGMotionLLM/BodyPartHighlight"
{
    Properties
    {
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.2
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _HighlightColor ("Highlight Color", Color) = (1.0, 1.0, 1.0, 1.0)
        _EmissionStrength ("Highlight Strength", Range(0, 4)) = 0.6
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        // Standard lighting so the body looks the same as before; custom vertex function to
        // carry the baked per-vertex glow (vertex colour alpha) into the surface shader.
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0

        sampler2D _MainTex;

        struct Input
        {
            float2 uv_MainTex;
            float glow;       // baked per-vertex highlight intensity (0..1)
        };

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;
        fixed4 _HighlightColor;
        float _EmissionStrength;

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.glow = v.color.a;   // BodyPartHighlighter writes the glow mask into colour.a
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
            // Emissive highlight; strength > 1 goes HDR so Bloom can pick it up.
            o.Emission = _HighlightColor.rgb * saturate(IN.glow) * _EmissionStrength;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
