// Flattens up to eight authored actor layers in one UI draw.  The ordinary
// CanvasGroup alpha is applied after this fragment has produced one silhouette,
// so clothes cannot reveal the body while the actor fades.
Shader "Hidden/LvnActorComposite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _LayerCount ("Layer count", Float) = 0
        [HideInInspector] _WardrobeMode ("Wardrobe flow enabled", Float) = 0
        [HideInInspector] _WardrobeProgress ("Wardrobe flow progress", Range(0,1)) = 0
        [HideInInspector] _WardrobeFromTop ("Wardrobe flow from top", Float) = 0
        [HideInInspector] _Layer0 ("Layer 0", 2D) = "black" {}
        [HideInInspector] _Layer1 ("Layer 1", 2D) = "black" {}
        [HideInInspector] _Layer2 ("Layer 2", 2D) = "black" {}
        [HideInInspector] _Layer3 ("Layer 3", 2D) = "black" {}
        [HideInInspector] _Layer4 ("Layer 4", 2D) = "black" {}
        [HideInInspector] _Layer5 ("Layer 5", 2D) = "black" {}
        [HideInInspector] _Layer6 ("Layer 6", 2D) = "black" {}
        [HideInInspector] _Layer7 ("Layer 7", 2D) = "black" {}
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 255
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                fixed4 color : COLOR;
            };

            fixed4 _Color;
            float4 _ClipRect;
            float _LayerCount;
            float _WardrobeMode;
            float _WardrobeProgress;
            float _WardrobeFromTop;
            sampler2D _Layer0, _Layer1, _Layer2, _Layer3;
            sampler2D _Layer4, _Layer5, _Layer6, _Layer7;
            float4 _MapA0, _MapA1, _MapA2, _MapA3, _MapA4, _MapA5, _MapA6, _MapA7;
            float4 _MapB0, _MapB1, _MapB2, _MapB3, _MapB4, _MapB5, _MapB6, _MapB7;
            float4 _Uv0, _Uv1, _Uv2, _Uv3, _Uv4, _Uv5, _Uv6, _Uv7;
            fixed4 _Tint0, _Tint1, _Tint2, _Tint3, _Tint4, _Tint5, _Tint6, _Tint7;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 sampleLayer(sampler2D tex, float4 mapA, float4 mapB, float4 uvRect,
                               fixed4 tint, float2 p)
            {
                float2 q = float2(dot(mapA.xy, p) + mapA.z,
                                  dot(mapB.xy, p) + mapB.z);
                float inside = step(0.0, q.x) * step(q.x, 1.0)
                             * step(0.0, q.y) * step(q.y, 1.0);
                fixed4 c = tex2D(tex, lerp(uvRect.xy, uvRect.zw, saturate(q))) * tint;
                c.a *= inside;
                return c;
            }

            // acc.rgb is premultiplied while layers are folded back-to-front.
            float4 overLayer(float4 acc, fixed4 src)
            {
                acc.rgb = src.rgb * src.a + acc.rgb * (1.0 - src.a);
                acc.a = src.a + acc.a * (1.0 - src.a);
                return acc;
            }

            // Small texture-free 1D value noise. The effect lives for ~0.21 s
            // on one already-composited actor graphic, so it adds no idle cost
            // and no extra noise texture/readback on low-end devices.
            float hash11(float p)
            {
                p = frac(p * 0.1031);
                p *= p + 33.33;
                p *= p + p;
                return frac(p);
            }

            float noise1(float x)
            {
                float i0 = floor(x);
                float f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(hash11(i0), hash11(i0 + 1.0), f);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 acc = 0;
                if (_LayerCount > 0.5) acc = overLayer(acc, sampleLayer(_Layer0, _MapA0, _MapB0, _Uv0, _Tint0, i.uv));
                if (_LayerCount > 1.5) acc = overLayer(acc, sampleLayer(_Layer1, _MapA1, _MapB1, _Uv1, _Tint1, i.uv));
                if (_LayerCount > 2.5) acc = overLayer(acc, sampleLayer(_Layer2, _MapA2, _MapB2, _Uv2, _Tint2, i.uv));
                if (_LayerCount > 3.5) acc = overLayer(acc, sampleLayer(_Layer3, _MapA3, _MapB3, _Uv3, _Tint3, i.uv));
                if (_LayerCount > 4.5) acc = overLayer(acc, sampleLayer(_Layer4, _MapA4, _MapB4, _Uv4, _Tint4, i.uv));
                if (_LayerCount > 5.5) acc = overLayer(acc, sampleLayer(_Layer5, _MapA5, _MapB5, _Uv5, _Tint5, i.uv));
                if (_LayerCount > 6.5) acc = overLayer(acc, sampleLayer(_Layer6, _MapA6, _MapB6, _Uv6, _Tint6, i.uv));
                if (_LayerCount > 7.5) acc = overLayer(acc, sampleLayer(_Layer7, _MapA7, _MapB7, _Uv7, _Tint7, i.uv));

                // Convert the internal premultiplied result back to the straight
                // alpha expected by Unity UI's SrcAlpha/OneMinusSrcAlpha blend.
                if (acc.a > 0.0001) acc.rgb /= acc.a;
                fixed4 color = fixed4(acc.rgb, acc.a) * i.color;

                // Wardrobe-only "silk flow": the old opaque composite peels
                // along a broad, gently irregular edge (feet-up for clothes,
                // head-down for hair) and reveals the fully assembled new rig.
                // No individual layer becomes translucent: no x-ray frame.
                if (_WardrobeMode > 0.5)
                {
                    bool hairFlow = _WardrobeFromTop > 0.5;
                    // Hair is deliberately calmer and cheaper: one broad,
                    // almost-flat contour with no animated-looking highlight.
                    // Clothes keep the more organic two-scale fabric edge.
                    float wave;
                    if (hairFlow)
                        wave = (noise1(i.uv.x * 6.0) - 0.5) * 0.018;
                    else
                        wave = (noise1(i.uv.x * 9.0) - 0.5) * 0.10
                             + (noise1(i.uv.x * 23.0 + 7.1) - 0.5) * 0.035;
                    float travel = hairFlow ? 1.0 - i.uv.y : i.uv.y;
                    float field = travel + wave;
                    float threshold = lerp(-0.15, 1.15, _WardrobeProgress);
                    float feather = hairFlow ? 0.09 : 0.055;
                    float keep = smoothstep(threshold - feather, threshold + feather, field);
                    if (_WardrobeProgress <= 0.0001) keep = 1.0;
                    if (_WardrobeProgress >= 0.9999) keep = 0.0;

                    if (!hairFlow)
                    {
                        float rim = 1.0 - smoothstep(0.008, 0.05, abs(field - threshold));
                        fixed3 accent = fixed3(0.04, 0.82, 0.78);
                        color.rgb += accent * rim * 0.12 * color.a;
                    }
                    color.a *= keep;
                }

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif
                return color;
            }
            ENDCG
        }
    }
}
