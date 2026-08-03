// Текстура БЕЗ РАЗВЁРТКИ — то, на чём держится всеядность движка.
//
// Мы принимаем любую модель: купленную, сгенерированную нейросетью, слепленную
// на скорую руку. У половины из них развёртки либо нет, либо она такая, что
// текстура растягивается кашей и швами. Обычный шейдер тут бессилен: он берёт
// UV, а брать нечего.
//
// Трипланар не спрашивает UV вообще. Он проецирует текстуру ТРИЖДЫ — по осям
// X, Y и Z — и смешивает три проекции по нормали: где поверхность смотрит
// вверх, работает верхняя проекция, где вбок — боковая. Шва нет, развёртка не
// нужна, масштаб задаётся в МЕТРАХ, а не в долях непонятно чего.
//
// Плата — три выборки текстуры вместо одной. Для задника новеллы это дёшево, а
// альтернатива — либо чинить развёртку руками у каждой модели, либо смириться
// с кашей.
Shader "Lvn/Triplanar"
{
    Properties
    {
        _Color ("Цвет", Color) = (1,1,1,1)
        _MainTex ("Текстура", 2D) = "white" {}
        _BumpMap ("Карта нормалей", 2D) = "bump" {}
        _BumpScale ("Сила рельефа", Range(0, 3)) = 1
        _Tiling ("Метров на повтор", Range(0.1, 20)) = 2
        _Blend ("Резкость стыка", Range(1, 16)) = 4
        _Variety ("Разброс повтора", Range(0, 1)) = 0.35
        _VertexAO ("Затенение из вершин", Range(0, 1)) = 0
        _ShadowTint ("Цвет тени", Color) = (0.35, 0.42, 0.6, 1)
        _Steps ("Ступеней света", Range(1, 4)) = 2
        _Softness ("Мягкость границы", Range(0.001, 0.3)) = 0.05
        // Те же поля стиля, что у Lvn/Toon, и с теми же именами: профиль сцены
        // раскладывается по материалам поиском по имени, и без них поверхность,
        // положенная трипланаром, выпадала бы из общего вида — без ободка и без
        // тёплой каймы рядом с моделями, у которых они есть.
        _RimColor ("Цвет ободка", Color) = (0.7, 0.85, 1, 1)
        _RimPower ("Узость ободка", Range(0.5, 8)) = 3
        _RimStrength ("Сила ободка", Range(0, 2)) = 0.6
        _WarmEdge ("Тёплая кайма у тени", Range(0, 1)) = 0.18
        _WarmColor ("Цвет каймы", Color) = (1, 0.72, 0.45, 1)
        _Outline ("Толщина обводки, м", Range(0, 0.1)) = 0
        _OutlineColor ("Цвет обводки", Color) = (0.06, 0.07, 0.11, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200

        CGPROGRAM
        // Освещение то же самое, что у Lvn/Toon: стиль игры один, меняется
        // только способ положить текстуру.
        // noambient — рассеянный свет считаем сами, чтобы затенение в углах
        // гасило только его (см. Lvn/Toon).
        #pragma surface surf Toon fullforwardshadows noambient
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color, _ShadowTint, _RimColor, _WarmColor;
        half _Tiling, _Blend, _Steps, _Softness, _BumpScale;
        half _RimPower, _RimStrength, _WarmEdge, _Variety, _VertexAO;

        half3 HemiAmbient(half3 n)
        {
            half u = n.y;
            return u > 0
                ? lerp(unity_AmbientEquator.rgb, unity_AmbientSky.rgb, u)
                : lerp(unity_AmbientEquator.rgb, unity_AmbientGround.rgb, -u);
        }

        struct Input
        {
            float3 worldPos;
            float3 worldNormal;
            float3 viewDir;
            float4 color : COLOR;
            INTERNAL_DATA          // нужен, раз мы сами пишем o.Normal
        };

        half4 LightingToon(SurfaceOutput s, half3 lightDir, half atten)
        {
            half ndl = dot(s.Normal, lightDir) * 0.5 + 0.5;
            half lit = 0;
            half steps = max(1, floor(_Steps));
            for (int i = 1; i <= 4; i++)
            {
                if (i > steps) break;
                half edge = (half)i / (steps + 1);
                lit += smoothstep(edge - _Softness, edge + _Softness, ndl);
            }
            lit = (lit / steps) * atten;

            // ДОПОЛНИТЕЛЬНЫЙ ПРОХОД ДОБАВЛЯЕТ, А НЕ ЗАМЕНЯЕТ.
            //
            // Каждый источник сверх первого рисуется отдельным проходом, и его
            // результат ПРИБАВЛЯЕТСЯ к уже нарисованному. Значит вернуть отсюда
            // «цвет поверхности в тени» нельзя: лампа, светящая в другом конце
            // сцены, подмешала бы тень ещё раз — и ещё раз с каждой следующей.
            // Замерили: три лампы в стороне поднимали дальний фон на 30 из 255.
            //
            // В дополнительном проходе отдаём ТОЛЬКО вклад своего источника; он
            // сам уходит в ноль там, куда свет не достаёт.
            #ifdef UNITY_PASS_FORWARDADD
                half4 add;
                add.rgb = s.Albedo * _LightColor0.rgb * lit;
                add.a = s.Alpha;
                return add;
            #endif

            half3 shaded = s.Albedo * _ShadowTint.rgb;
            half3 col = lerp(shaded, s.Albedo * _LightColor0.rgb, lit);

            // Тёплая кайма на границе света и тени — тот же приём и та же
            // формула, что в Lvn/Toon: стиль один, меняется только способ
            // положить текстуру.
            half band = saturate(1.0 - abs(lit - 0.5) * 2.4);
            band = band * band * band;
            col += _WarmColor.rgb * s.Albedo * band * _WarmEdge;

            half4 c;
            c.rgb = col;
            c.a = s.Alpha;
            return c;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            float3 p = IN.worldPos / max(_Tiling, 0.01);
            // ГЕОМЕТРИЧЕСКАЯ нормаль, а не итоговая: веса проекций считаются по
            // тому, как повёрнут сам треугольник. Брать IN.worldNormal после
            // записи o.Normal нельзя — Unity требует INTERNAL_DATA и всё равно
            // вернёт нормаль С УЧЁТОМ рельефа, то есть проекции поплывут.
            float3 n = normalize(WorldNormalVector(IN, float3(0, 0, 1)));

            // Веса проекций по нормали. Степень задаёт, насколько узкой будет
            // зона смешивания: мягко — размыто на скруглениях, резко — заметен
            // стык на них же. Четвёрка по умолчанию — компромисс.
            float3 w = pow(abs(n), _Blend);
            w /= (w.x + w.y + w.z);

            fixed4 cx = tex2D(_MainTex, p.zy);
            fixed4 cy = tex2D(_MainTex, p.xz);
            fixed4 cz = tex2D(_MainTex, p.xy);
            fixed4 c = (cx * w.x + cy * w.y + cz * w.z) * _Color;

            // РАЗРУШЕНИЕ ПОВТОРА. Земля на сто метров при повторе в три метра —
            // это три десятка одинаковых квадратов; в статике их не замечаешь, а
            // стоит камере поехать, как проступает решётка, и место сразу
            // читается как декорация.
            //
            // Лечим не вторым материалом, а КРУПНОЙ ОКТАВОЙ той же текстуры:
            // берём её на масштабе в несколько десятков метров и модулируем ею
            // яркость. Получаются широкие пятна — где посветлее, где потемнее,
            // как выгоревшая и вытоптанная трава, — и глаз перестаёт собирать
            // повтор в сетку. Цена — одна выборка, и множитель некратный
            // основному, иначе два узора совпали бы и решётка вернулась.
            if (_Variety > 0.001)
            {
                half m = Luminance(tex2D(_MainTex, p.xz * 0.117).rgb);
                c.rgb *= lerp(1.0, 0.62 + m * 0.86, _Variety);
            }

            o.Albedo = c.rgb;
            o.Alpha = c.a;

            // Рельеф теми же тремя проекциями. Строго это не «правильный»
            // трипланарный нормал-маппинг (он требует пересборки базиса на
            // каждой оси), но в стилизованном свете разница не читается, а
            // цена — те же три выборки вместо шести.
            fixed3 bx = UnpackNormal(tex2D(_BumpMap, p.zy));
            fixed3 by = UnpackNormal(tex2D(_BumpMap, p.xz));
            fixed3 bz = UnpackNormal(tex2D(_BumpMap, p.xy));
            fixed3 bump = normalize(bx * w.x + by * w.y + bz * w.z);
            o.Normal = normalize(lerp(fixed3(0, 0, 1), bump, _BumpScale));

                        // ТОЛЬКО В ОСНОВНОМ ПРОХОДЕ. Поверхностный шейдер вызывается по
            // разу на КАЖДЫЙ источник света, и всё, что попадает в Emission,
            // складывается столько раз, сколько в сцене ламп. Замерили: три
            // лампы в стороне поднимали яркость дальнего фона на 30 из 255 —
            // фон светлел от источников, которые светят в другую сторону.
            //
            // Рассеянный свет и контровой ободок к источникам отношения не
            // имеют: они существуют один раз на пиксель.
            #ifndef UNITY_PASS_FORWARDADD
    half rim = 1.0 - saturate(dot(normalize(IN.viewDir), o.Normal));
                o.Emission = _RimColor.rgb * pow(rim, _RimPower) * _RimStrength;

                // Земля тоже получает небо сверху и отражённый свет снизу — иначе
                // она выпадает из той же погоды, что и всё остальное в кадре.
                half ao = lerp(1.0, IN.color.a, _VertexAO);
                o.Emission += HemiAmbient(n) * o.Albedo * ao;
            #endif

        }
        ENDCG

        // Обводка — тот же вывернутый контур, что у Lvn/Toon: стиль общий,
        // значит и линия у всех материалов одна. Толщина 0 по умолчанию —
        // пасс стоит второго вызова отрисовки и включается штучно.
        Pass
        {
            Name "OUTLINE"
            Cull Front
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            half _Outline;
            fixed4 _OutlineColor;

            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata_base v)
            {
                v2f o;
                // Раздуваем в МИРОВОМ пространстве. Объектное ломается
                // масштабом (у клинка 0.09×1.5×0.02 линия расползлась бы по
                // толщине), а пространство вида подтягивает оболочку к камере —
                // на шаре она вылезала ВПЕРЁД и закрывала его целиком чёрным.
                // Мировое даёт ровную толщину в метрах и всегда снаружи.
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 n = UnityObjectToWorldNormal(v.normal);
                world += normalize(n) * _Outline;
                o.pos = UnityWorldToClipPos(float4(world, 1.0));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return _OutlineColor; }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
