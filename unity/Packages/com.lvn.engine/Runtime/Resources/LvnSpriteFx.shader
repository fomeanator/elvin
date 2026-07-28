// Спрайтовые эффекты (op `sfx`) для канвас-актёров: обводка, свечение,
// растворение, призрак, камень, голограмма, горение, контровой свет и дрожь.
// Один проход поверх UI-блендинга; все эффекты выключены нулями.
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
            float _Outline, _Glow, _Dissolve, _Flash, _Dark, _TintFx,
                  _Ghost, _Petrify, _Hologram, _Burn, _Rim, _Shake;
            fixed4 _OutlineColor, _GlowColor, _TintFxColor, _GhostColor,
                   _HologramColor, _BurnColor, _RimColor;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Дрожь не меняет placement: смещаем уже clip-space позицию.
                float jitterX = sin(_Time.y * 47.0 + v.vertex.y * 0.031);
                float jitterY = cos(_Time.y * 41.0 + v.vertex.x * 0.027);
                o.pos.xy += float2(jitterX, jitterY) * _Shake * o.pos.w * 0.006;
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            float hash21(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            float noise21(float2 p)
            {
                float2 cell = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(hash21(cell), hash21(cell + float2(1, 0)), f.x),
                            lerp(hash21(cell + float2(0, 1)), hash21(cell + 1.0), f.x), f.y);
            }

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
                float2 uv = i.uv;

                // Призрак и голограмма слегка ломают сам силуэт, а не весь кадр.
                if (_Ghost > 0.001)
                    uv.x += sin(uv.y * 18.0 + _Time.y * 2.4) * _Ghost * 0.007;
                if (_Hologram > 0.001)
                {
                    float band = step(0.93, frac(uv.y * 13.0 - _Time.y * 2.8));
                    uv.x += (band - 0.5) * _Hologram * 0.018;
                }

                fixed4 col = tex2D(_MainTex, uv) * i.color;

                // Растворение: шумовая маска съедает спрайт, кромка светится.
                if (_Dissolve > 0.001)
                {
                    float n = hash21(floor(uv * 220.0));
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
                    float near = ringAlpha(uv, r);
                    if (near > 0.5)
                        return fixed4(_OutlineColor.rgb, near * _OutlineColor.a * (1.0 - _Dissolve));
                }

                // Свечение: ореол наружу + лёгкий подсвет самого спрайта.
                if (_Glow > 0.001)
                {
                    if (col.a < 0.5)
                    {
                        float halo = ringAlpha(uv, _Glow * 0.05) * 0.6
                                   + ringAlpha(uv, _Glow * 0.025) * 0.4;
                        if (halo > 0.05)
                            return fixed4(_GlowColor.rgb, halo * _Glow * 0.55 * (1.0 - _Dissolve));
                    }
                    else col.rgb += _GlowColor.rgb * _Glow * 0.25;
                }

                // Контровой свет изнутри края силуэта.
                if (_Rim > 0.001 && col.a > 0.05)
                {
                    float r = max(_MainTex_TexelSize.x, _MainTex_TexelSize.y) * (1.0 + _Rim * 5.0);
                    float inside = min(min(tex2D(_MainTex, uv + float2( r, 0)).a,
                                           tex2D(_MainTex, uv + float2(-r, 0)).a),
                                       min(tex2D(_MainTex, uv + float2(0,  r)).a,
                                           tex2D(_MainTex, uv + float2(0, -r)).a));
                    float edge = saturate(col.a - inside) * _Rim;
                    col.rgb += _RimColor.rgb * edge * 1.8;
                }

                // Горение: снизу поднимается шумная обугленная зона с яркой кромкой.
                if (_Burn > 0.001 && col.a > 0.05)
                {
                    float n = noise21(uv * float2(11.0, 17.0));
                    float front = uv.y * 0.78 + n * 0.22;
                    float charred = 1.0 - smoothstep(_Burn - 0.055, _Burn + 0.10, front);
                    float fireEdge = 1.0 - smoothstep(0.018, 0.072, abs(front - _Burn));
                    col.rgb = lerp(col.rgb, col.rgb * 0.12, charred * _Burn);
                    col.rgb += _BurnColor.rgb * fireEdge * _Burn * 1.35;
                }

                // Камень: обесцвечивание + зернистая минеральная фактура.
                if (_Petrify > 0.001 && col.a > 0.05)
                {
                    float lum = dot(col.rgb, fixed3(0.299, 0.587, 0.114));
                    float stoneNoise = (noise21(uv * 42.0) - 0.5) * 0.12
                                     + (hash21(floor(uv * 320.0)) - 0.5) * 0.025;
                    fixed3 stone = fixed3(lum * 0.82 + stoneNoise, lum * 0.84 + stoneNoise, lum * 0.88 + stoneNoise);
                    col.rgb = lerp(col.rgb, stone, _Petrify);
                }

                // Голограмма: холодный цвет, строки и неравномерная прозрачность.
                if (_Hologram > 0.001 && col.a > 0.01)
                {
                    float scan = 0.65 + 0.35 * sin(uv.y * 520.0 + _Time.y * 9.0);
                    col.rgb = lerp(col.rgb, col.rgb * 0.38 + _HologramColor.rgb * (0.42 + scan * 0.30), _Hologram * 0.76);
                    col.a *= lerp(1.0, 0.58 + scan * 0.24, _Hologram);
                }

                // Призрак: холодная полупрозрачность с медленным дыханием.
                if (_Ghost > 0.001 && col.a > 0.01)
                {
                    float breathe = 0.72 + 0.18 * sin(_Time.y * 2.1 + uv.y * 5.0);
                    col.rgb = lerp(col.rgb, _GhostColor.rgb, _Ghost * 0.68);
                    col.a *= lerp(1.0, breathe, _Ghost);
                }

                // Перекрас (отравлен/заморожен) → силуэт → хит-флеш.
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
