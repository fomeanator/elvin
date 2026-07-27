// Мультиэффект кадра (op `fx`) — один убер-проход + три служебных пасса
// блума. Все эффекты выключены нулями своих юниформ: стоимость невключённого
// эффекта — одна ветка-умножение на ноль. Алгоритмы классические (виньетка,
// зерно, аберрация, скан-линии, пикселизация, аналоговый глитч, грейдинг,
// радиальные лучи, блум по порогу) — реализация своя, по мотивам открытых
// пост-стеков (Kino/Keijiro, MIT) без заимствования кода.
Shader "Hidden/LvnFx"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        float4 _MainTex_TexelSize;

        struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
        ENDCG

        // ── 0: убер-композит ─────────────────────────────────────────────
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _BloomTex;
            float _Vignette, _Grain, _Chromatic, _Scanlines, _Pixelate,
                  _Glitch, _Saturation, _Contrast, _Bloom, _Rays, _Distort;
            float4 _Tint;      // rgb множитель (1,1,1 = нет)
            float4 _RayCenter; // xy — источник лучей в uv

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float t = _Time.y;

                // Дисторшн: тепловое марево / вода — синусоидальный сдвиг uv.
                if (_Distort > 0.001)
                    uv += float2(sin(uv.y * 42.0 + t * 2.6), sin(uv.x * 38.0 + t * 2.2)) * _Distort * 0.006;

                // Пикселизация: решётка крупных текселей.
                if (_Pixelate > 0.5)
                {
                    float2 cells = float2(_MainTex_TexelSize.z, _MainTex_TexelSize.w) / _Pixelate;
                    uv = (floor(uv * cells) + 0.5) / cells;
                }

                // Аналоговый глитч: дрожь строк + дрейф цвета, всё от времени.
                if (_Glitch > 0.001)
                {
                    float line = floor(uv.y * 240.0);
                    float jitter = (hash(float2(line, floor(t * 24.0))) - 0.5)
                                   * _Glitch * 0.04 * step(0.6, hash(float2(floor(t * 8.0), line * 0.13)));
                    uv.x += jitter;
                }

                // Хроматическая аберрация: RGB расходятся по радиусу.
                float2 fromC = uv - 0.5;
                fixed4 col;
                if (_Chromatic > 0.001)
                {
                    float k = _Chromatic * 0.012;
                    col.r = tex2D(_MainTex, uv + fromC * k).r;
                    col.g = tex2D(_MainTex, uv).g;
                    col.b = tex2D(_MainTex, uv - fromC * k).b;
                    col.a = 1;
                }
                else col = tex2D(_MainTex, uv);

                // Лучи света: 12 радиальных сэмплов к источнику, взвешенных яркостью.
                if (_Rays > 0.001)
                {
                    float2 dir = (_RayCenter.xy - uv) / 12.0;
                    float2 p = uv; float acc = 0; float w = 1.0;
                    [unroll] for (int s = 0; s < 12; s++)
                    {
                        p += dir;
                        fixed3 c = tex2D(_MainTex, p).rgb;
                        acc += max(c.r, max(c.g, c.b)) * w;
                        w *= 0.87;
                    }
                    col.rgb += _Rays * 0.10 * acc * _Tint.rgb;
                }

                // Блум: заранее размытая яркая часть кадра.
                if (_Bloom > 0.001) col.rgb += tex2D(_BloomTex, uv).rgb * _Bloom;

                // Грейдинг: насыщенность → контраст → тон.
                float lum = dot(col.rgb, float3(0.299, 0.587, 0.114));
                col.rgb = lerp(float3(lum, lum, lum), col.rgb, _Saturation);
                col.rgb = (col.rgb - 0.5) * _Contrast + 0.5;
                col.rgb *= _Tint.rgb;

                // Скан-линии.
                if (_Scanlines > 0.001)
                    col.rgb *= 1.0 - _Scanlines * 0.5 * (0.5 + 0.5 * sin(uv.y * _MainTex_TexelSize.w * 3.14159));

                // Зерно (анимированное).
                if (_Grain > 0.001)
                    col.rgb += (hash(uv * (t + 1.0) * 601.0) - 0.5) * _Grain * 0.35;

                // Виньетка (естественное затухание к углам).
                if (_Vignette > 0.001)
                {
                    float d = length(fromC) * 1.4142;
                    col.rgb *= 1.0 - _Vignette * smoothstep(0.45, 1.05, d);
                }

                return col;
            }
            ENDCG
        }

        // ── 1: блум-префильтр (порог яркости, в четверть разрешения) ─────
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragPre
            fixed4 fragPre(v2f i) : SV_Target
            {
                fixed3 c = tex2D(_MainTex, i.uv).rgb;
                float lum = max(c.r, max(c.g, c.b));
                return fixed4(c * smoothstep(0.65, 0.95, lum), 1);
            }
            ENDCG
        }

        // ── 2/3: раздельный гаусс для блума ──────────────────────────────
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlur
            float4 _Dir;
            fixed4 fragBlur(v2f i) : SV_Target
            {
                float2 d = _Dir.xy * _MainTex_TexelSize.xy * 1.8;
                fixed3 c = tex2D(_MainTex, i.uv).rgb * 0.294;
                c += tex2D(_MainTex, i.uv + d).rgb * 0.235;
                c += tex2D(_MainTex, i.uv - d).rgb * 0.235;
                c += tex2D(_MainTex, i.uv + d * 2.2).rgb * 0.118;
                c += tex2D(_MainTex, i.uv - d * 2.2).rgb * 0.118;
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
