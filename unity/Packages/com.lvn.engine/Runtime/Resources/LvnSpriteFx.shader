// Спрайтовые эффекты (op `sfx`) для канвас-актёров: обводка, свечение,
// растворение. Один проход поверх обычного UI-блендинга; все эффекты
// выключены нулями. Толщины — в долях UV (масштабируются с артом).
Shader "Hidden/LvnSpriteFx"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Cull Off Lighting Off ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Color;
            float _Outline, _Glow, _Dissolve, _Flash, _Dark, _TintFx;
            fixed4 _OutlineColor, _GlowColor, _TintFxColor;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            float hash21(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            // Максимальная альфа в кольце вокруг точки (для обводки/свечения).
            float ringAlpha(float2 uv, float r)
            {
                float a = 0;
                a = max(a, tex2D(_MainTex, uv + float2( r, 0)).a);
                a = max(a, tex2D(_MainTex, uv + float2(-r, 0)).a);
                a = max(a, tex2D(_MainTex, uv + float2(0,  r)).a);
                a = max(a, tex2D(_MainTex, uv + float2(0, -r)).a);
                float d = r * 0.7071;
                a = max(a, tex2D(_MainTex, uv + float2( d,  d)).a);
                a = max(a, tex2D(_MainTex, uv + float2(-d,  d)).a);
                a = max(a, tex2D(_MainTex, uv + float2( d, -d)).a);
                a = max(a, tex2D(_MainTex, uv + float2(-d, -d)).a);
                return a;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * i.color;

                // Растворение: шумовая маска съедает спрайт, кромка светится.
                if (_Dissolve > 0.001)
                {
                    float n = hash21(floor(i.uv * 220.0));
                    float edge = _Dissolve * 1.06;
                    if (n < edge)
                    {
                        // тонкая горящая кромка на границе распада
                        if (n > edge - 0.08 && col.a > 0.1)
                            return fixed4(_GlowColor.rgb, col.a * (1.0 - _Dissolve));
                        col.a = 0;
                    }
                }

                // Обводка: прозрачный пиксель рядом с непрозрачным.
                if (_Outline > 0.001 && col.a < 0.5)
                {
                    float r = _Outline * 0.02;
                    float near = ringAlpha(i.uv, r);
                    if (near > 0.5)
                        return fixed4(_OutlineColor.rgb, near * _OutlineColor.a * (1.0 - _Dissolve));
                }

                // Свечение: ореол наружу + лёгкий подсвет самого спрайта.
                if (_Glow > 0.001)
                {
                    if (col.a < 0.5)
                    {
                        float halo = ringAlpha(i.uv, _Glow * 0.05) * 0.6
                                   + ringAlpha(i.uv, _Glow * 0.025) * 0.4;
                        if (halo > 0.05)
                            return fixed4(_GlowColor.rgb, halo * _Glow * 0.55 * (1.0 - _Dissolve));
                    }
                    else col.rgb += _GlowColor.rgb * _Glow * 0.25;
                }

                // Перекрас (отравлен/заморожен/призрак) → силуэт → хит-флеш.
                if (_TintFx > 0.001) col.rgb = lerp(col.rgb, _TintFxColor.rgb, _TintFx);
                if (_Dark > 0.001)   col.rgb = lerp(col.rgb, fixed3(0.02, 0.02, 0.03), _Dark);
                if (_Flash > 0.001)  col.rgb = lerp(col.rgb, fixed3(1, 1, 1), _Flash);

                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
