// СТВОР ПОРТАЛА КАК ОБЪЕКТ СЦЕНЫ, а не постэффект кадра.
//
// Первая версия жила в полноэкранном стеке (LvnFx, OnRenderImage) — и оттуда
// росла вся её ненадёжность: без камеры в сцене эффект молча не рисуется, чужая
// уборка сбрасывает стек, а лечь ПОД героиню постэффект не может в принципе,
// потому что работает с уже готовым кадром.
//
// Здесь створ — обычный прозрачный слой канваса: рисуется всегда, живёт между
// фоном и актёрами, и никакая уборка эффектов его не касается.
//
// ПРАВИЛО ЭТОГО ФАЙЛА: НИ ОДНОГО РАННЕГО return И НИ ОДНОГО tex2D.
// Живой розовый экран 28.08 родился именно так: чтение текстуры стояло в ветке
// `if (_HasCore > 0.5)` после двух ранних выходов, а tex2D требует градиентов
// (производных по соседним пикселям), которых в дивергентном потоке нет. На
// Metal такой вариант не собирается, Unity молча подставляет встроенный
// error-шейдер — и створ радиусом во весь экран заливает кадр ядовито-розовым.
// Проверка `-batchmode -nographics` этого не ловит: там Metal-вариант просто не
// компилируется. Поэтому здесь один выход, а текстура читается через tex2Dlod,
// которому градиенты не нужны.
Shader "Hidden/LvnPortalDisk"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Open ("Раскрытие 0..1", Float) = 0
        _Color ("Свечение", Color) = (0.48, 0.84, 1, 1)
        _CoreTex ("Ядро (картинка)", 2D) = "black" {}
        _HasCore ("Ядро картинкой 0/1", Float) = 0
        _Bolts ("Молнии по кромке 0..1", Float) = 1
        _Twist ("Скручивание вовнутрь 0..1", Float) = 1
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            float _Open;
            float4 _Color;
            sampler2D _CoreTex;
            float _HasCore;
            float _Bolts;
            float _Twist;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            // Дешёвый повторяемый шум — рябь горловины. Полноценный noise здесь
            // не нужен: створ смотрят полсекунды, а лишние текстурные чтения на
            // телефоне стоят дороже, чем выигрыш в красоте.
            float hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float open = saturate(_Open);
                float t = _Time.y;

                // Круг в собственных координатах слоя: слой квадратный, поэтому
                // овала не будет ни на каком экране.
                float2 d = i.uv - 0.5;
                float dist = length(d) * 2.0;          // 0 в центре, 1 у края
                float ang = atan2(d.y, d.x);

                float edge = max(open, 0.0001);         // текущий радиус створа
                // Маски вместо ранних выходов: закрытый створ и всё за кромкой
                // просто обнуляют альфу в самом конце.
                float alive = step(0.001, open) * step(dist, edge);

                // Горловина: к центру — свет, к краю — прозрачность.
                float throat = 1.0 - smoothstep(edge * 0.15, edge * 0.98, dist);
                // Кромка: тонкое яркое кольцо по самому краю.
                float rim = 1.0 - smoothstep(0.0, edge * 0.16, abs(dist - edge));

                // ── МОЛНИИ ПО КРОМКЕ ─────────────────────────────────────────
                // Разряд — это не «полоса по кругу», а несколько изломов,
                // которые живут доли секунды и гаснут. Отсюда рецепт: изломанная
                // линия (сумма синусов разной частоты по углу), узкое окно
                // вокруг кромки и мерцание, у которого свой ритм — иначе все
                // разряды вспыхивают разом и читаются как пульс лампы.
                float jag = sin(ang * 9.0 + t * 5.3) * 0.5
                          + sin(ang * 23.0 - t * 8.1) * 0.3
                          + sin(ang * 41.0 + t * 13.7) * 0.2;
                float path = edge * (0.90 + 0.055 * jag);        // где идёт разряд
                // ИМЯ `bolt`, а НЕ `line`: `line` — зарезервированное слово HLSL
                // (топология примитива). Локальная переменная с таким именем
                // роняла компиляцию варианта на Metal («unexpected token 'line'»),
                // Unity подставляла error-шейдер, и створ во весь экран заливал
                // кадр розовым — тот самый живой репорт 28.08.
                float bolt = 1.0 - smoothstep(0.0, edge * 0.022, abs(dist - path));
                // Мерцание по секторам: у каждого свой отсчёт, поэтому
                // соседние разряды не совпадают по фазе.
                float sector = floor(ang * 4.0 + 8.0);
                float flick = step(0.55, hash(float2(sector, floor(t * 7.0))));
                float bolts = bolt * flick * saturate(_Bolts);

                // ЯДРО КАРТИНКОЙ. Процедурный вихрь дешёв и не требует ни
                // одного файла, но полосы по углу читаются «ломаными линиями»
                // (живой отзыв Ильи 28.08). Когда новелла принесла картинку —
                // рисуем её: она РАСТЁТ вместе с раскрытием (uv делится на
                // текущий радиус, поэтому шар не «выезжает», а раздувается) и
                // медленно вращается, то есть ведёт себя как ядро, а не как
                // наклейка поверх круга.
                //
                // СКРУЧИВАНИЕ ВОВНУТРЬ (просьба Ильи: «у нас есть шейдер с
                // эффектом скручивания вовнутрь — его можно применить») —
                // тот же приём, что в блоке `_Portal` полноэкранного LvnFx:
                // чем ближе к центру, тем сильнее пиксель тянет к горловине
                // (радиально) и подкручивает по кругу (тангенциально). Здесь
                // это живёт в UV ядра, а не в готовом кадре, поэтому работает
                // и без камеры, и под актёрами.
                float inner = 1.0 - saturate(dist / edge);
                float pull = inner * inner;
                float twist = saturate(_Twist);
                // Сила закрутки — ВТРОЕ против первой пробы (Илья 28.08):
                // при 1.6 рад у горловины воронка читалась как лёгкий наклон
                // картинки, а не как затягивание внутрь.
                float a = t * 0.35                                // оборот ~18 с
                        + pull * twist * (4.8 + 1.5 * sin(t * 1.6));
                float2 r = float2(d.x * cos(a) - d.y * sin(a),
                                  d.x * sin(a) + d.y * cos(a));
                r *= 1.0 + pull * twist * 0.54;                   // затягивание к центру
                float2 cuv = saturate(r / edge + 0.5);
                // tex2Dlod, а НЕ tex2D: нулевой мип задан явно, градиенты не
                // нужны — вариант собирается на любом бэкенде.
                fixed4 core = tex2Dlod(_CoreTex, float4(cuv, 0, 0));

                // Дыхание: ядро чуть пульсирует, иначе на длинном переходе
                // картинка читается как замерший скриншот.
                float pulse = 0.90 + 0.10 * sin(t * 2.3);
                float3 rgbCore = core.rgb * pulse + _Color.rgb * (rim * 1.2 + bolts * 1.8);
                float alphaCore = saturate(core.a * pulse + rim * 0.8 + bolts);

                // Вихрь: полосы, закрученные вокруг центра. Запасной вид, когда
                // новелла картинку не дала.
                float swirl = 0.62 + 0.38 * sin(ang * 3.0 - t * 2.1 + dist * 14.0);
                float grain = 0.85 + 0.15 * hash(floor(float2(ang * 6.0, t * 6.0)));
                float3 rgbSwirl = _Color.rgb * (throat * swirl * grain + rim * 1.4 + bolts * 1.8);
                float alphaSwirl = saturate(throat * 0.92 + rim + bolts);

                // Выбор — арифметикой, не ветвлением: обе половины уже посчитаны,
                // и ни одна не читает текстуру в дивергентном потоке.
                float useCore = step(0.5, _HasCore);
                float3 rgb = lerp(rgbSwirl, rgbCore, useCore);
                float alpha = lerp(alphaSwirl, alphaCore, useCore) * open * i.color.a * alive;

                return fixed4(rgb, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
