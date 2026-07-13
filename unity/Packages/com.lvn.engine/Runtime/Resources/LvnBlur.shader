// LvnBlur — separable gaussian blur for the world camera (FxLayer's real
// depth-of-field, replacing the old "white veil" imitation).
//
// How to read this if shaders are new to you:
//  • A fragment shader is a tiny function the GPU runs FOR EVERY PIXEL of the
//    output, in parallel. All it can do is read textures and do math.
//  • Blur = "each output pixel is the weighted average of its neighbours".
//    A naive 2D average of a 9×9 area is 81 texture reads per pixel. The
//    gaussian's trick: it's SEPARABLE — blur horizontally (pass over the
//    image reading 5 neighbours in a row), then blur that result vertically
//    (5 more), and the result is mathematically the same as the full 2D
//    blur: 10 reads instead of 81.
//  • The weights (0.227, 0.316, 0.070) are a normal-distribution bell curve
//    sampled at offsets 0 / ±1.38 / ±3.23 texels. The fractional offsets are
//    a second trick: sampling BETWEEN two texels makes the GPU's bilinear
//    filter average them for free, so 5 reads act like 9.
Shader "Hidden/Lvn/Blur"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        // Post-processing boilerplate: draw over everything, no depth writes.
        ZTest Always Cull Off ZWrite Off

        // ── Pass 0: one blur step along _Dir ((1,0)=horizontal, (0,1)=vertical) ──
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img      // stock "fill the screen" vertex shader
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;   // (1/width, 1/height) — auto-filled by Unity
            float2 _Dir;                 // blur axis for this pass
            float  _Radius;              // spread, in texels

            fixed4 frag (v2f_img i) : SV_Target
            {
                float2 step = _Dir * _MainTex_TexelSize.xy * _Radius;
                fixed4 c = tex2D(_MainTex, i.uv) * 0.227027;
                c += (tex2D(_MainTex, i.uv + step * 1.384615)
                    + tex2D(_MainTex, i.uv - step * 1.384615)) * 0.316216;
                c += (tex2D(_MainTex, i.uv + step * 3.230769)
                    + tex2D(_MainTex, i.uv - step * 3.230769)) * 0.070270;
                return c;
            }
            ENDCG
        }

        // ── Pass 1: composite — mix the sharp frame with the blurred copy ──
        // Animating _Mix 0→1 is what makes `blur alpha=… duration=…` fade in
        // smoothly instead of snapping.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;          // the sharp source frame
            sampler2D _BlurTex;          // the blurred, downsampled copy
            float _Mix;                  // 0 = sharp, 1 = fully blurred

            fixed4 frag (v2f_img i) : SV_Target
            {
                return lerp(tex2D(_MainTex, i.uv), tex2D(_BlurTex, i.uv), _Mix);
            }
            ENDCG
        }
    }
    Fallback Off
}
