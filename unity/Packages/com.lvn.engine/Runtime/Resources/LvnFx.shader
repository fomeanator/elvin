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

        // Дешёвый value-noise без текстур. Нужен атмосферным эффектам:
        // туман остаётся мягким, а частицы не повторяются заметной сеткой.
        float noise2(float2 p)
        {
            float2 cell = floor(p);
            float2 f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(hash(cell), hash(cell + float2(1, 0)), f.x),
                        lerp(hash(cell + float2(0, 1)), hash(cell + 1.0), f.x), f.y);
        }
        ENDCG

        // ── 0: убер-композит ─────────────────────────────────────────────
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _BloomTex;
            float _Vignette, _Grain, _Chromatic, _Scanlines, _Pixelate,
                  _Glitch, _Saturation, _Contrast, _Bloom, _Rays, _Distort,
                  _Frost, _Blink, _Invert, _Fog, _Rain, _Snow, _Embers,
                  _Blood, _Poison, _Shockwave, _Speedlines, _Dream, _Sepia,
                  _Posterize, _Letterbox, _Space, _SpaceRadius,
                  _Sketch, _Halftone, _Heat, _Ripple, _Dust, _Ink;
            float4 _Tint;      // rgb множитель (1,1,1 = нет)
            float4 _RayCenter; // xy — источник лучей в uv
            float4 _FxCenter;  // xy — эпицентр удара в uv
            float4 _SpaceCenter; // xy — центр пространственной линзы
            float4 _FogColor, _EmberColor, _BloodColor, _PoisonColor, _InkColor;
            float4 _SpaceColor;

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float t = _Time.y;
                float screenAspect = _MainTex_TexelSize.z
                                   / max(_MainTex_TexelSize.w, 1.0);
                float2 spaceDelta = float2((uv.x - _SpaceCenter.x) * screenAspect,
                                            uv.y - _SpaceCenter.y);
                float spaceDistance = length(spaceDelta);
                float spaceR = max(_SpaceRadius, 0.05);

                // Ударная волна: значение 0→1 — фаза расширения кольца.
                // Автор двигает её через dur, поэтому эффект не зависит от FPS.
                if (_Shockwave > 0.001 && _Shockwave < 0.999)
                {
                    float2 delta = uv - _FxCenter.xy;
                    float radius = length(delta);
                    float ring = 1.0 - smoothstep(0.0, 0.035, abs(radius - _Shockwave * 0.82));
                    uv += normalize(delta + 0.0001) * ring * (1.0 - _Shockwave) * 0.035;
                }

                // Дисторшн: тепловое марево / вода — синусоидальный сдвиг uv.
                if (_Distort > 0.001)
                    uv += float2(sin(uv.y * 42.0 + t * 2.6), sin(uv.x * 38.0 + t * 2.2)) * _Distort * 0.006;

                // Марево: воздух дрожит У ЗЕМЛИ и успокаивается кверху —
                // потому и вертикальный градиент, а не ровный сдвиг по кадру.
                // Ровный читается как «поплыл экран», градиент — как жара.
                if (_Heat > 0.001)
                {
                    float ground = 1.0 - smoothstep(0.0, 0.85, uv.y);
                    float wob = sin(uv.y * 58.0 - t * 3.1) * 0.6
                              + noise2(float2(uv.x * 9.0, uv.y * 22.0 - t * 1.4)) - 0.5;
                    uv.x += wob * ground * _Heat * 0.010;
                    uv.y += sin(uv.x * 31.0 + t * 2.2) * ground * _Heat * 0.004;
                }

                // Круги по воде: расходятся от точки удара (та же _FxCenter,
                // что у ударной волны) и НЕ затухают по времени — это
                // состояние сцены, а не одиночный всплеск.
                if (_Ripple > 0.001)
                {
                    float2 rd = float2((uv.x - _FxCenter.x) * screenAspect, uv.y - _FxCenter.y);
                    float rl = length(rd);
                    float wave = sin(rl * 68.0 - t * 3.4) * exp(-rl * 2.2);
                    uv += (rd / max(rl, 0.0001)) * wave * _Ripple * 0.010;
                }

                // Пространственная линза: фон изгибается к чёрному ядру,
                // слегка закручиваясь вдоль аккреционного кольца.
                if (_Space > 0.001)
                {
                    float2 spaceDir = spaceDelta / max(spaceDistance, 0.0001);
                    float2 dirUv = float2(spaceDir.x / screenAspect, spaceDir.y);
                    float2 tangentUv = float2(-spaceDir.y / screenAspect, spaceDir.x);
                    float lensBand = smoothstep(spaceR * 0.10, spaceR * 0.34,
                                                spaceDistance)
                                   * (1.0 - smoothstep(spaceR * 0.92,
                                                      spaceR * 1.34,
                                                      spaceDistance));
                    float gravity = (1.0 - saturate(spaceDistance / (spaceR * 1.34)));
                    float ripple = 0.72 + 0.28
                                 * sin(spaceDistance * 94.0 - t * 1.15);
                    uv += dirUv * lensBand * gravity * ripple * _Space * 0.055;
                    uv += tangentUv * lensBand * gravity * _Space
                        * sin(t * 0.34 + spaceDistance * 31.0) * 0.018;
                }

                // Сон/видение: медленный плавающий объектив. Сам soft-focus
                // накладывается после основного сэмпла.
                if (_Dream > 0.001)
                    uv += float2(sin(uv.y * 7.0 + t * 0.7), cos(uv.x * 6.0 + t * 0.6)) * _Dream * 0.004;

                // Пикселизация: решётка крупных текселей.
                if (_Pixelate > 0.5)
                {
                    float2 cells = float2(_MainTex_TexelSize.z, _MainTex_TexelSize.w) / _Pixelate;
                    uv = (floor(uv * cells) + 0.5) / cells;
                }

                // Аналоговый глитч: дрожь строк + дрейф цвета, всё от времени.
                if (_Glitch > 0.001)
                {
                    float row = floor(uv.y * 240.0);
                    float jitter = (hash(float2(row, floor(t * 24.0))) - 0.5)
                                   * _Glitch * 0.04 * step(0.6, hash(float2(floor(t * 8.0), row * 0.13)));
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

                // Чёрное ядро и светящаяся аккреционная кромка. Само
                // искривление уже произошло выше до чтения кадра.
                if (_Space > 0.001)
                {
                    float core = 1.0 - smoothstep(spaceR * 0.10,
                                                  spaceR * 0.28,
                                                  spaceDistance);
                    float shadow = 1.0 - smoothstep(spaceR * 0.30,
                                                    spaceR * 0.62,
                                                    spaceDistance);
                    float ringRadius = spaceR * 0.39;
                    float ring = 1.0 - smoothstep(spaceR * 0.025,
                                                  spaceR * 0.085,
                                                  abs(spaceDistance - ringRadius));
                    float grain = 0.70 + noise2(float2(
                        atan2(spaceDelta.y, spaceDelta.x) * 5.0 - t * 0.22,
                        floor(t * 3.0))) * 0.30;
                    col.rgb *= 1.0 - saturate(core * 0.98 + shadow * 0.30 * _Space);
                    col.rgb += _SpaceColor.rgb * ring * grain * _Space * 0.78;
                    col.rgb += ring * ring * _Space * 0.24;
                }

                // Мягкий фокус сна: четыре соседних сэмпла. Не включён —
                // дополнительных чтений текстуры нет благодаря ветке.
                if (_Dream > 0.001)
                {
                    float2 d = _MainTex_TexelSize.xy * (2.0 + _Dream * 5.0);
                    fixed3 soft = tex2D(_MainTex, uv + float2( d.x, 0)).rgb
                                + tex2D(_MainTex, uv + float2(-d.x, 0)).rgb
                                + tex2D(_MainTex, uv + float2(0,  d.y)).rgb
                                + tex2D(_MainTex, uv + float2(0, -d.y)).rgb;
                    col.rgb = lerp(col.rgb, soft * 0.25, _Dream * 0.72);
                    col.rgb += float3(0.035, 0.025, 0.055) * _Dream;
                }

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

                // Сепия и постеризация — стилизация поверх общего грейдинга.
                if (_Sepia > 0.001)
                {
                    float3 sep = float3(dot(col.rgb, float3(0.393, 0.769, 0.189)),
                                        dot(col.rgb, float3(0.349, 0.686, 0.168)),
                                        dot(col.rgb, float3(0.272, 0.534, 0.131)));
                    col.rgb = lerp(col.rgb, sep, _Sepia);
                }
                if (_Posterize > 0.001)
                {
                    float levels = lerp(16.0, 3.0, saturate(_Posterize));
                    col.rgb = floor(saturate(col.rgb) * levels + 0.5) / levels;
                }

                // Карандаш: кромки по Собелю ложатся штрихом, цвет уходит в
                // бумагу. Штрих идёт ПОД одним углом и дрожит зерном — ровная
                // сетка читается как решётка, а не как рука.
                if (_Sketch > 0.001)
                {
                    float2 px = _MainTex_TexelSize.xy * 1.6;
                    float l00 = dot(tex2D(_MainTex, uv + float2(-px.x, -px.y)).rgb, float3(0.299, 0.587, 0.114));
                    float l10 = dot(tex2D(_MainTex, uv + float2( 0.0, -px.y)).rgb, float3(0.299, 0.587, 0.114));
                    float l20 = dot(tex2D(_MainTex, uv + float2( px.x, -px.y)).rgb, float3(0.299, 0.587, 0.114));
                    float l01 = dot(tex2D(_MainTex, uv + float2(-px.x, 0.0)).rgb, float3(0.299, 0.587, 0.114));
                    float l21 = dot(tex2D(_MainTex, uv + float2( px.x, 0.0)).rgb, float3(0.299, 0.587, 0.114));
                    float l02 = dot(tex2D(_MainTex, uv + float2(-px.x, px.y)).rgb, float3(0.299, 0.587, 0.114));
                    float l12 = dot(tex2D(_MainTex, uv + float2( 0.0, px.y)).rgb, float3(0.299, 0.587, 0.114));
                    float l22 = dot(tex2D(_MainTex, uv + float2( px.x, px.y)).rgb, float3(0.299, 0.587, 0.114));
                    float gx = (l20 + 2.0 * l21 + l22) - (l00 + 2.0 * l01 + l02);
                    float gy = (l02 + 2.0 * l12 + l22) - (l00 + 2.0 * l10 + l20);
                    float edge = saturate(sqrt(gx * gx + gy * gy) * 2.2);

                    float shade = 1.0 - lum;                       // где темно — там штрихуем
                    float rule = (uv.x + uv.y) * _MainTex_TexelSize.w * 0.16
                               + hash(floor(uv * 220.0)) * 0.55;   // дрожь руки
                    float hatch = smoothstep(0.35, 0.75, shade)
                                * (0.5 + 0.5 * sin(rule * 3.14159));
                    float3 paper = float3(0.94, 0.92, 0.87)
                                 - hash(uv * 130.0) * 0.045;       // зерно бумаги
                    float3 pencil = paper * (1.0 - saturate(edge * 0.95 + hatch * 0.55));
                    col.rgb = lerp(col.rgb, pencil, _Sketch);
                }

                // Комиксный растр: точка тем крупнее, чем темнее место. Сетка
                // повёрнута на 15°, как в печати, — по осям она сразу читается
                // как пиксели, а не как типографский растр.
                if (_Halftone > 0.001)
                {
                    float cells = lerp(120.0, 46.0, saturate(_Halftone));
                    float2 aspectUv = float2(uv.x * screenAspect, uv.y);
                    float ca = cos(0.2618), sa = sin(0.2618);
                    float2 rot = float2(aspectUv.x * ca - aspectUv.y * sa,
                                        aspectUv.x * sa + aspectUv.y * ca) * cells;
                    float2 cellUv = frac(rot) - 0.5;
                    float dot_r = (1.0 - lum) * 0.72;              // радиус точки
                    float ink = 1.0 - smoothstep(dot_r - 0.12, dot_r + 0.06, length(cellUv));
                    float3 printed = lerp(float3(0.97, 0.96, 0.93), col.rgb * 0.35, ink);
                    col.rgb = lerp(col.rgb, printed, _Halftone);
                }

                // Скан-линии.
                if (_Scanlines > 0.001)
                    col.rgb *= 1.0 - _Scanlines * 0.5 * (0.5 + 0.5 * sin(uv.y * _MainTex_TexelSize.w * 3.14159));

                // Зерно (анимированное).
                if (_Grain > 0.001)
                    col.rgb += (hash(uv * (t + 1.0) * 601.0) - 0.5) * _Grain * 0.35;

                // Негатив (хоррор): плавный к инверсии.
                if (_Invert > 0.001)
                    col.rgb = lerp(col.rgb, 1.0 - col.rgb, _Invert);

                // Заморозка: бело-голубой иней ползёт с краёв, кромка шумная.
                if (_Frost > 0.001)
                {
                    float d2 = length(fromC) * 1.4142;
                    float edge = smoothstep(1.05 - _Frost * 0.9, 1.25, d2 + hash(uv * 90.0) * 0.12);
                    col.rgb = lerp(col.rgb, float3(0.83, 0.93, 1.0), edge * 0.9);
                }

                // Туман: два медленных слоя value-noise. Плотнее у земли,
                // но не закрывает верх кадра сплошной плашкой.
                if (_Fog > 0.001)
                {
                    float n1 = noise2(uv * 3.2 + float2(t * 0.035, t * 0.012));
                    float n2 = noise2(uv * 6.7 + float2(-t * 0.022, t * 0.018));
                    float mist = smoothstep(0.48, 0.76, n1 * 0.68 + n2 * 0.32);
                    mist *= lerp(0.55, 1.0, 1.0 - uv.y) * _Fog;
                    col.rgb = lerp(col.rgb, _FogColor.rgb, mist * 0.72);
                }

                // Дождь: тонкие диагональные штрихи с независимым мерцанием.
                if (_Rain > 0.001)
                {
                    float2 q = float2(uv.x * 45.0, uv.y * 34.0 + t * 16.0);
                    q.x += q.y * 0.20;
                    float2 cid = floor(q);
                    float2 cf = frac(q);
                    float seed = hash(cid);
                    float streak = (1.0 - smoothstep(0.016, 0.065, abs(cf.x - seed)))
                                 * (1.0 - smoothstep(0.18, 0.48, cf.y)) * step(0.62, seed);
                    col.rgb += float3(0.62, 0.76, 0.92) * streak * _Rain * 0.62;
                    col.rgb *= 1.0 - _Rain * 0.08;
                }

                // Снег: два масштаба круглых хлопьев, падающих с разной скоростью.
                if (_Snow > 0.001)
                {
                    float snow = 0;
                    float2 sq = float2(uv.x * 15.0, uv.y * 22.0 + t * 1.8);
                    float2 sid = floor(sq);
                    float2 sf = frac(sq) - float2(hash(sid), hash(sid + 17.3));
                    snow += (1.0 - smoothstep(0.03, 0.16, length(sf))) * step(0.35, hash(sid + 4.1));
                    sq = float2(uv.x * 27.0, uv.y * 35.0 + t * 3.1);
                    sid = floor(sq);
                    sf = frac(sq) - float2(hash(sid), hash(sid + 9.7));
                    snow += (1.0 - smoothstep(0.02, 0.11, length(sf))) * step(0.58, hash(sid + 2.4));
                    col.rgb = lerp(col.rgb, float3(0.92, 0.97, 1.0), saturate(snow) * _Snow * 0.9);
                }

                // Пыль в воздухе: мелкие крупинки, которые почти стоят и
                // мерцают, поймав свет. Медленнее снега на порядок — иначе
                // комната читается как метель.
                if (_Dust > 0.001)
                {
                    float2 dq = float2(uv.x * 34.0 + sin(t * 0.21) * 0.6,
                                       uv.y * 46.0 - t * 0.35);
                    float2 did = floor(dq);
                    float2 df = frac(dq) - float2(hash(did), hash(did + 5.7));
                    float mote = (1.0 - smoothstep(0.015, 0.075, length(df)))
                               * step(0.72, hash(did + 3.3));
                    float twinkle = 0.45 + 0.55 * sin(t * 1.7 + hash(did) * 6.28);
                    col.rgb += float3(1.0, 0.96, 0.88) * mote * twinkle * _Dust * 0.55;
                }

                // Искры/угли: частицы летят вверх, яркое ядро + красный ореол.
                if (_Embers > 0.001)
                {
                    float2 eq = float2(uv.x * 19.0, uv.y * 26.0 - t * 3.4);
                    eq.x += sin(eq.y * 0.37 + t) * 0.35;
                    float2 eid = floor(eq);
                    float2 ef = frac(eq) - float2(hash(eid), hash(eid + 13.1));
                    float spark = (1.0 - smoothstep(0.025, 0.13, length(ef)))
                                * step(0.64, hash(eid + 7.9));
                    float core = (1.0 - smoothstep(0.01, 0.045, length(ef))) * spark;
                    col.rgb += (_EmberColor.rgb * spark + core) * _Embers;
                }

                // Кровь и яд — читаемые статусы по краям, центр боя остаётся виден.
                if (_Blood > 0.001)
                {
                    float edge = smoothstep(0.40, 0.96, length(fromC) * 1.4142);
                    float pulse = 0.78 + 0.22 * sin(t * 5.2);
                    col.rgb = lerp(col.rgb, _BloodColor.rgb, edge * _Blood * pulse * 0.78);
                }
                if (_Poison > 0.001)
                {
                    float edge = smoothstep(0.32, 1.0, length(fromC) * 1.4142);
                    float crawl = noise2(uv * 5.0 + float2(t * 0.08, -t * 0.06));
                    col.rgb = lerp(col.rgb, _PoisonColor.rgb, edge * crawl * _Poison * 0.64);
                }

                // Линии скорости: радиальная штриховка только по периферии.
                if (_Speedlines > 0.001)
                {
                    float radius = length(fromC);
                    float angle = atan2(fromC.y, fromC.x) * 57.2958;
                    float ray = hash(float2(floor(angle * 1.7), floor(t * 18.0)));
                    float speedRay = step(0.78, ray) * smoothstep(0.18, 0.72, radius);
                    speedRay *= 0.55 + 0.45 * sin(radius * 110.0 - t * 20.0);
                    col.rgb += speedRay * _Speedlines * 0.55;
                }

                // Моргание: веки смыкаются сверху и снизу (мягкая кромка).
                // open=1 в видимой середине, 0 под веками; при _Blink=1 темно.
                if (_Blink > 0.001)
                {
                    float lid = _Blink * 0.52;
                    float open = smoothstep(lid - 0.08, lid, uv.y)
                               * smoothstep(lid - 0.08, lid, 1.0 - uv.y);
                    col.rgb *= open;
                }

                // Виньетка (естественное затухание к углам).
                if (_Vignette > 0.001)
                {
                    float d = length(fromC) * 1.4142;
                    col.rgb *= 1.0 - _Vignette * smoothstep(0.45, 1.05, d);
                }

                // Чернила: клякса расползается по кадру с рваным краем. Это
                // ПЕРЕХОД, поэтому 0 — чистый кадр, 1 — кадр залит целиком, а
                // край рвётся шумом, иначе видно ровный круг.
                if (_Ink > 0.001)
                {
                    float2 iq = uv - 0.5;
                    float edgeNoise = noise2(uv * 5.5) * 0.22 + noise2(uv * 13.0) * 0.10;
                    float reach = length(iq) * 1.4142 - edgeNoise;
                    float front = smoothstep(_Ink * 1.28 - 0.10, _Ink * 1.28, 1.0 - reach);
                    col.rgb = lerp(col.rgb, _InkColor.rgb, saturate(front));
                }

                // Кинематографические полосы. 0→1 увеличивает их до 18% кадра.
                if (_Letterbox > 0.001)
                {
                    float bar = _Letterbox * 0.18;
                    float visible = smoothstep(bar - 0.006, bar + 0.006, uv.y)
                                  * smoothstep(bar - 0.006, bar + 0.006, 1.0 - uv.y);
                    col.rgb *= visible;
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
