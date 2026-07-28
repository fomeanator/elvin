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
                  _Ghost, _Petrify, _Hologram, _Burn, _Rim, _Shake,
                  _Aura, _Blade, _Lightning, _Runes, _ScopedPart, _AuraStyle;
            fixed4 _OutlineColor, _GlowColor, _TintFxColor, _GhostColor,
                   _HologramColor, _BurnColor, _RimColor, _AuraColor,
                   _AuraColor2, _BladeColor, _LightningColor, _RunesColor;

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

            float uvMask(float2 p)
            {
                return step(0.0, p.x) * step(p.x, 1.0)
                     * step(0.0, p.y) * step(p.y, 1.0);
            }

            float sampleAlpha(float2 p)
            {
                return tex2D(_MainTex, saturate(p)).a * uvMask(p);
            }

            // Максимальная альфа в кольце вокруг точки (для обводки/свечения).
            float ringAlpha(float2 uv, float r)
            {
                float a = 0;
                a = max(a, sampleAlpha(uv + float2( r, 0)));
                a = max(a, sampleAlpha(uv + float2(-r, 0)));
                a = max(a, sampleAlpha(uv + float2(0,  r)));
                a = max(a, sampleAlpha(uv + float2(0, -r)));
                float d = r * 0.7071;
                a = max(a, sampleAlpha(uv + float2( d,  d)));
                a = max(a, sampleAlpha(uv + float2(-d,  d)));
                a = max(a, sampleAlpha(uv + float2( d, -d)));
                a = max(a, sampleAlpha(uv + float2(-d, -d)));
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

                fixed4 col = tex2D(_MainTex, saturate(uv)) * i.color;
                col.a *= uvMask(uv);

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

                // Манхва-ауры: широкое двухцветное дыхание вокруг силуэта,
                // энергетика клинка, молнии и вращающийся магический круг.
                // Всё рисуется внутри прямоугольника самого спрайта — без
                // дополнительных текстур, объектов и кадров анимации.
                if (_Aura > 0.001 || _Blade > 0.001 || _Lightning > 0.001 || _Runes > 0.001)
                {
                    float time = _Time.y;
                    float2 centered = uv - 0.5;
                    float aspect = _MainTex_TexelSize.z / max(_MainTex_TexelSize.w, 1.0);
                    float2 radialUv = float2(centered.x * aspect, centered.y);
                    float radius = length(radialUv);
                    float angle = atan2(radialUv.y, radialUv.x);
                    float pulse = 0.72 + 0.18 * sin(time * 2.5)
                                          + 0.10 * sin(time * 5.7 + uv.y * 11.0);

                    if (col.a <= 0.05)
                    {
                        fixed3 energyRgb = fixed3(0, 0, 0);
                        float energyWeight = 0.0;
                        float energyAlpha = 0.0;

                        if (_Runes > 0.001)
                        {
                            float outerRing = 1.0 - smoothstep(0.004, 0.012, abs(radius - 0.235));
                            float innerRing = 1.0 - smoothstep(0.003, 0.010, abs(radius - 0.184));
                            float ticks = pow(abs(sin(angle * 12.0 + time * 0.72)), 12.0);
                            float glyphs = pow(abs(sin(angle * 7.0 - time * 0.38)), 18.0);
                            float gaps = smoothstep(0.18, 0.42,
                                                    abs(sin(angle * 3.0 + time * 0.16)));
                            float runeInk = (outerRing * (0.34 + ticks * 0.66)
                                           + innerRing * glyphs * 0.72) * gaps
                                           * _Runes * pulse;
                            energyRgb += _RunesColor.rgb * runeInk;
                            energyWeight += runeInk;
                            energyAlpha += runeInk * 0.72;
                        }

                        if (_Aura > 0.001)
                        {
                            float auraNear = ringAlpha(uv, 0.014);
                            float auraFar = ringAlpha(uv, 0.034 + sin(time * 2.2) * 0.003);
                            float rise = 0.58 + 0.42 * noise21(float2(uv.x * 13.0 + time * 0.21,
                                                                      uv.y * 9.0 - time * 1.35));
                            float auraInk = saturate(auraNear * 0.62 + auraFar * 0.31);
                            float auraPulse = pulse * rise;
                            float style = floor(_AuraStyle + 0.5);

                            // Стиль меняет характер движения, но не палитру:
                            // её всегда можно переопределить aura_color/2.
                            if (style < 0.5) // basic: ровное бесстихийное дыхание
                            {
                                auraPulse = 0.72 + 0.16 * sin(time * 1.65)
                                                    + rise * 0.12;
                                auraInk = auraNear * 0.72 + auraFar * 0.20;
                            }
                            else if (style < 1.5) // guard: цельная защитная оболочка
                            {
                                float wardRadius = 0.255 + sin(time * 1.4) * 0.004;
                                float wardRing = 1.0 - smoothstep(0.0024, 0.0065,
                                                                 abs(radius - wardRadius));
                                float wardInner = 1.0 - smoothstep(0.0018, 0.0048,
                                    abs(radius - (wardRadius - 0.021)));
                                float panels = 0.56 + 0.44
                                             * pow(abs(cos(angle * 6.0 - time * 0.24)), 8.0);
                                float wardScan = 0.70 + 0.30
                                               * sin(angle * 4.0 - time * 1.7);
                                auraInk = auraNear * 0.50 + auraFar * 0.18
                                        + wardRing * wardScan * panels * 0.58
                                        + wardInner * 0.18;
                                auraPulse = 0.76 + 0.18 * sin(time * 1.8);
                            }
                            else if (style < 2.5) // fire: языки поднимаются снизу вверх
                            {
                                float flames = noise21(float2(uv.x * 18.0,
                                                               uv.y * 8.0 - time * 2.8));
                                float tongues = smoothstep(0.38, 0.86,
                                    flames + sin(uv.x * 31.0 + time * 2.2) * 0.16);
                                auraInk = auraNear * 0.54 + auraFar * (0.18 + tongues * 0.58);
                                auraPulse = 0.68 + rise * 0.42;
                            }
                            else if (style < 3.5) // frost: тихое сияние и острые кристаллические блики
                            {
                                float facets = pow(abs(sin(angle * 11.0 + radius * 76.0
                                                            - time * 0.45)), 14.0);
                                auraInk = auraNear * 0.72 + auraFar * 0.25
                                        + facets * auraFar * 0.58;
                                auraPulse = 0.82 + 0.12 * sin(time * 1.15);
                            }
                            else if (style < 4.5) // storm: нервные всплески и рваный контур
                            {
                                float surge = pow(saturate(0.5 + 0.5
                                    * sin(time * 8.5 + uv.y * 28.0 + rise * 5.0)), 5.0);
                                auraInk = auraNear * (0.42 + surge * 0.72)
                                        + auraFar * (0.14 + surge * 0.44);
                                auraPulse = 0.72 + surge * 0.48;
                            }
                            else if (style < 5.5) // shadow: медленный тяжёлый дым
                            {
                                float smoke = noise21(float2(uv.x * 7.0 - time * 0.36,
                                                              uv.y * 6.0 - time * 0.72));
                                auraInk = auraNear * 0.44 + auraFar * (0.16 + smoke * 0.58);
                                auraPulse = 0.58 + smoke * 0.46;
                            }
                            else if (style < 6.5) // holy: устойчивое свечение с лучами
                            {
                                float rayFalloff = smoothstep(0.11, 0.18, radius)
                                                 * (1.0 - smoothstep(0.25, 0.31, radius));
                                float rays = pow(abs(sin(angle * 9.0 + time * 0.18)), 16.0)
                                           * rayFalloff;
                                auraInk = auraNear * 0.78 + auraFar * 0.28 + rays * 0.42;
                                auraPulse = 0.88 + 0.10 * sin(time * 1.25);
                            }
                            else if (style < 7.5) // space: вязкий вакуум у силуэта
                            {
                                float voidSmoke = noise21(float2(angle * 3.2 - time * 0.18,
                                                                  radius * 19.0 + time * 0.11));
                                // Не рисуем орбиты вокруг каждого слоя
                                // составного персонажа: чёрную дыру и линзу
                                // создаёт полноэкранный LvnFx.
                                auraInk = auraNear * 0.42
                                        + auraFar * (0.10 + voidSmoke * 0.27);
                                auraPulse = 0.70 + 0.10 * sin(time * 0.56);
                            }
                            else // distortion: чёрный внутренний, красный внешний разлом
                            {
                                float slowWarp = noise21(float2(uv.y * 8.5 - time * 0.22,
                                                                 uv.x * 3.0 + time * 0.09)) - 0.5;
                                float crooked = sin(uv.y * 24.0 + slowWarp * 7.0
                                                   - time * 0.68) * 0.0035;
                                float innerBlack = ringAlpha(uv, 0.005 + crooked);
                                float outerWide = ringAlpha(uv, 0.018
                                                               + slowWarp * 0.006);
                                float outerInner = ringAlpha(uv, 0.011
                                                                + slowWarp * 0.003);
                                float outerBand = saturate(outerWide
                                                         - outerInner * 0.90);
                                float fractureNoise = noise21(float2(
                                    uv.y * 12.0 - time * 0.20,
                                    uv.x * 5.0 + time * 0.08));
                                float fractures = smoothstep(0.38, 0.72,
                                    fractureNoise
                                    + 0.18 * sin(uv.y * 34.0 + slowWarp * 8.0
                                                 - time * 0.46));
                                auraNear = innerBlack;
                                auraFar = outerBand;
                                auraInk = innerBlack * 0.68
                                        + outerBand * fractures * 0.92;
                                auraPulse = 0.78 + 0.10 * sin(time * 0.42
                                                            + uv.y * 4.0);
                            }

                            auraInk = saturate(auraInk) * _Aura * auraPulse;
                            fixed3 auraTone = lerp(_AuraColor.rgb, _AuraColor2.rgb,
                                                   saturate(uv.y * 0.72 + rise * 0.34));
                            if (style > 6.5 && style < 7.5)
                            {
                                float violetOrbit = smoothstep(0.17, 0.29, radius);
                                auraTone = lerp(_AuraColor.rgb, _AuraColor2.rgb,
                                                violetOrbit * (0.62 + rise * 0.38));
                            }
                            else if (style > 7.5)
                            {
                                float outerRed = saturate(auraFar);
                                auraTone = lerp(_AuraColor.rgb, _AuraColor2.rgb,
                                                smoothstep(0.04, 0.72, outerRed));
                            }
                            energyRgb += auraTone * auraInk;
                            energyWeight += auraInk;
                            float auraAlphaScale = style > 7.5 ? 0.68
                                                 : style > 6.5 ? 0.72
                                                 : 0.62;
                            energyAlpha += auraInk * auraAlphaScale;
                        }

                        if (_Blade > 0.001)
                        {
                            // Манхва-клинок: почти белое горячее ядро, плотный
                            // цветной ореол и широкое мягкое послесвечение.
                            float bladeCore = ringAlpha(uv, 0.0045);
                            float bladeNear = ringAlpha(uv, 0.014);
                            float bladeMid = ringAlpha(uv, 0.032);
                            float bladeFar = ringAlpha(uv, 0.058);
                            float trailA = sampleAlpha(uv + float2(-0.026, -0.044));
                            float bladePulse = 0.78 + 0.22
                                             * sin(time * 5.6 + uv.y * 18.0);
                            float coreInk = bladeCore * _Blade
                                          * (0.88 + bladePulse * 0.12);
                            float haloInk = saturate(bladeNear * 0.88
                                                   + bladeMid * 0.48
                                                   + bladeFar * 0.20
                                                   + trailA * 0.16)
                                          * _Blade * bladePulse;
                            fixed3 hotCore = lerp(_BladeColor.rgb,
                                                  fixed3(1.0, 1.0, 1.0), 0.82);
                            energyRgb += hotCore * coreInk * 1.25
                                       + _BladeColor.rgb * haloInk;
                            energyWeight += coreInk * 1.25 + haloInk;
                            energyAlpha += coreInk * 1.18 + haloInk * 0.88;
                        }

                        if (_Lightning > 0.001)
                        {
                            // Непрерывная рваная линия вместо набора прямоугольных
                            // сегментов. Два масштаба шума дают крупные изломы и
                            // мелкую живую дрожь, а короткая ветвь вспыхивает рядом.
                            float frame = floor(time * 9.0);
                            float coarse = noise21(float2(uv.y * 5.5 + frame * 0.071,
                                                          frame * 0.193));
                            float fine = noise21(float2(uv.y * 16.0 - frame * 0.113,
                                                        frame * 0.347));
                            float sidePath = 0.174 + (coarse - 0.5) * 0.080
                                                  + (fine - 0.5) * 0.026;
                            float boltDistance = abs(abs(radialUv.x) - sidePath);
                            float core = 1.0 - smoothstep(0.00020, 0.00090, boltDistance);
                            float halo = 1.0 - smoothstep(0.00085, 0.00170, boltDistance);

                            float flickerBand = sin(uv.y * 47.0 + frame * 1.71
                                                  + coarse * 11.0);
                            float broken = smoothstep(-0.58, -0.04, flickerBand);

                            float branchCenter = frac(frame * 0.173) * 0.70 + 0.15;
                            float branchWindow = 1.0 - smoothstep(0.025, 0.115,
                                                                 abs(uv.y - branchCenter));
                            float branchPath = sidePath
                                             + (uv.y - branchCenter)
                                             * (coarse > 0.5 ? 0.42 : -0.42);
                            float branchDistance = abs(abs(radialUv.x) - branchPath);
                            float branch = (1.0 - smoothstep(0.00018, 0.00085,
                                                            branchDistance))
                                         * branchWindow;

                            float lightningInk = (core * broken + halo * broken * 0.24
                                                + branch * 0.72)
                                               * _Lightning
                                               * (0.82 + 0.18 * sin(time * 21.0));
                            energyRgb += _LightningColor.rgb * lightningInk;
                            energyWeight += lightningInk;
                            energyAlpha += lightningInk * 0.88;
                        }

                        if (energyWeight > 0.008)
                            return fixed4(energyRgb / max(energyWeight, 0.001),
                                          saturate(energyAlpha));
                    }
                    else
                    {
                        if (_Aura > 0.001)
                        {
                            float px = max(_MainTex_TexelSize.x, _MainTex_TexelSize.y);
                            float inside = min(min(sampleAlpha(uv + float2( px * 4.0, 0)),
                                                   sampleAlpha(uv + float2(-px * 4.0, 0))),
                                               min(sampleAlpha(uv + float2(0,  px * 4.0)),
                                                   sampleAlpha(uv + float2(0, -px * 4.0))));
                            float auraRim = saturate(col.a - inside);
                            float style = floor(_AuraStyle + 0.5);
                            float flowSpeed = style > 7.5 ? 0.72
                                            : style > 6.5 ? 0.88
                                            : style > 1.5 && style < 2.5 ? 7.2
                                            : style > 4.5 && style < 5.5 ? 1.3
                                            : style > 3.5 && style < 4.5 ? 9.5
                                            : 3.4;
                            float flowPower = style > 2.5 && style < 3.5 ? 13.0
                                            : style > 3.5 && style < 4.5 ? 4.0
                                            : 7.0;
                            float flow = pow(saturate(0.5 + 0.5
                                               * sin(uv.y * 31.0 - time * flowSpeed
                                                   + noise21(uv * 9.0) * 5.0)), flowPower);
                            if (style > 0.5 && style < 1.5)
                                flow *= 0.18; // барьер цельный, а не пламенный
                            fixed3 auraTone = lerp(_AuraColor.rgb, _AuraColor2.rgb,
                                                   saturate(uv.y + flow * 0.25));
                            if (style > 7.5)
                            {
                                // Разлом съедает внутреннюю кромку в чёрный;
                                // красный остаётся снаружи и только слегка
                                // отражается на самом силуэте.
                                col.rgb = lerp(col.rgb, _AuraColor.rgb,
                                               auraRim * _Aura * 0.88);
                                col.rgb += _AuraColor2.rgb * auraRim * _Aura * 0.08;
                            }
                            else if (style > 6.5)
                            {
                                col.rgb = lerp(col.rgb, _AuraColor.rgb,
                                               auraRim * _Aura * 0.64);
                                col.rgb += _AuraColor2.rgb * auraRim * _Aura * 0.13;
                            }
                            else
                            {
                                col.rgb += auraTone * _Aura
                                         * (auraRim * (0.68 + pulse * 0.30)
                                            + flow * 0.075);
                            }
                        }

                        if (_Blade > 0.001)
                        {
                            float hi = max(col.r, max(col.g, col.b));
                            float lo = min(col.r, min(col.g, col.b));
                            float metalMask = _ScopedPart > 0.5
                                ? 1.0
                                : smoothstep(0.48, 0.86, dot(col.rgb, fixed3(0.299, 0.587, 0.114)))
                                  * (1.0 - smoothstep(0.10, 0.42, hi - lo));
                            float sweepPos = frac(uv.y * 0.86 + uv.x * 0.28 - time * 0.82);
                            float sweep = pow(saturate(1.0 - abs(sweepPos - 0.5) * 2.0), 13.0);
                            float bladeEnergy = metalMask * _Blade;
                            col.rgb = lerp(col.rgb,
                                           _BladeColor.rgb * 0.48
                                           + fixed3(1.0, 1.0, 1.0) * 0.72,
                                           bladeEnergy * 0.72);
                            col.rgb += fixed3(1.0, 1.0, 1.0)
                                     * sweep * bladeEnergy * 0.92;
                        }
                    }
                }

                // Контровой свет изнутри края силуэта.
                if (_Rim > 0.001 && col.a > 0.05)
                {
                    float r = max(_MainTex_TexelSize.x, _MainTex_TexelSize.y) * (1.0 + _Rim * 5.0);
                    float inside = min(min(sampleAlpha(uv + float2( r, 0)),
                                           sampleAlpha(uv + float2(-r, 0))),
                                       min(sampleAlpha(uv + float2(0,  r)),
                                           sampleAlpha(uv + float2(0, -r))));
                    float edge = saturate(col.a - inside) * _Rim;
                    col.rgb += _RimColor.rgb * edge * 1.8;
                }

                // Горение: снизу остаётся тёмный обугленный материал, а сам огонь —
                // узкая живая кромка. Не красим всю пройденную область в оранжевый:
                // это сразу превращает эффект в дешёвую цветовую маску.
                if (_Burn > 0.001)
                {
                    float drift = _Time.y * 0.42;
                    float broad = noise21(float2(uv.x * 7.0 + drift * 0.22,
                                                 uv.y * 10.0 - drift));
                    float detail = noise21(float2(uv.x * 23.0 - drift * 0.37,
                                                  uv.y * 31.0 - drift * 1.8));
                    float flameNoise = broad * 0.72 + detail * 0.28;
                    float front = uv.y + (flameNoise - 0.5) * 0.105;
                    float signedFront = front - _Burn;

                    // Очень тонкий ореол снаружи силуэта — пламя цепляется за край,
                    // но не создаёт толстую неоновую обводку.
                    if (col.a <= 0.05)
                    {
                        float px = max(_MainTex_TexelSize.x, _MainTex_TexelSize.y);
                        float nearby = ringAlpha(uv, px * 3.5);
                        float edgeHalo = 1.0 - smoothstep(0.008, 0.029, abs(signedFront));
                        float flicker = 0.72 + detail * 0.28;
                        float haloAlpha = nearby * edgeHalo * flicker * _Burn * 0.34;
                        if (haloAlpha > 0.012)
                            return fixed4(lerp(_BurnColor.rgb, fixed3(1.0, 0.72, 0.20), 0.42),
                                          haloAlpha);
                    }
                    else
                    {
                        // Уже пройденная огнём часть теряет насыщенность и темнеет,
                        // сохраняя складки, украшения и фактуру исходного арта.
                        float charred = 1.0 - smoothstep(-0.065, 0.018, signedFront);
                        float luminance = dot(col.rgb, fixed3(0.299, 0.587, 0.114));
                        fixed3 soot = col.rgb * 0.20 + fixed3(luminance * 0.045,
                                                             luminance * 0.027,
                                                             luminance * 0.018);
                        col.rgb = lerp(col.rgb, soot, charred * 0.82);

                        // Два узких слоя кромки: янтарный жар и почти белое ядро.
                        float warmEdge = 1.0 - smoothstep(0.008, 0.028, abs(signedFront));
                        float hotCore = 1.0 - smoothstep(0.002, 0.009, abs(signedFront));
                        float flicker = 0.68 + broad * 0.22 + detail * 0.10;
                        col.rgb += _BurnColor.rgb * warmEdge * flicker * _Burn * 0.55;
                        col.rgb += fixed3(1.0, 0.72, 0.22) * hotCore * flicker * _Burn * 0.44;

                        // Редкие тлеющие точки остаются в угле, но не образуют пятна.
                        float emberNoise = noise21(uv * float2(67.0, 83.0) +
                                                   float2(0.0, -_Time.y * 0.9));
                        float ember = smoothstep(0.91, 0.975, emberNoise)
                                    * charred
                                    * smoothstep(-0.24, -0.035, signedFront);
                        col.rgb += _BurnColor.rgb * ember * _Burn * 0.38;
                    }
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
