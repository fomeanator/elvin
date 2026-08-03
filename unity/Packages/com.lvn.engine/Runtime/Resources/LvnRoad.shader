// Грунтовая дорога для плоскости: один материал решает четыре причины
// «наклеенного ковра» — прямой край, растянутую UV, одинаковый повтор и
// равномерный блеск. Дополнительных масок нет: край и колеи выводятся из
// координат самой дороги, крупная неоднородность — из той же текстуры.
Shader "Lvn/Road"
{
    Properties
    {
        _Color ("Цвет", Color) = (1,1,1,1)
        _MainTex ("Текстура", 2D) = "white" {}
        _BumpMap ("Карта нормалей", 2D) = "bump" {}
        _BumpScale ("Сила рельефа", Range(0, 3)) = 0.5
        _Tiling ("Метров на повтор", Range(0.1, 20)) = 1.8
        _Edge ("Неровность края", Range(0, 1)) = 0.28
        _Ruts ("Колея", Range(0, 1)) = 0.65
        _Wet ("Влажность колеи", Range(0, 1)) = 0.15
        _Variety ("Крупные пятна", Range(0, 1)) = 0.42
        _VertexAO ("Затенение из вершин", Range(0, 1)) = 0
        _ShadowTint ("Цвет тени", Color) = (0.35, 0.42, 0.6, 1)
        _Softness ("Мягкость границы", Range(0.001, 0.3)) = 0.05
        _WarmEdge ("Тёплая кайма у тени", Range(0, 1)) = 0.18
        _WarmColor ("Цвет каймы", Color) = (1, 0.72, 0.45, 1)
    }

    SubShader
    {
        // Рисуем сразу после непрозрачного грунта, смешиваем только узкую
        // кромку и при этом пишем глубину. Это не обычный transparent-объект:
        // сортировать дорогу с деревьями не нужно, а мягкий край не мерцает.
        Tags { "RenderType" = "TransparentCutout" "Queue" = "Geometry+1" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        LOD 180

        CGPROGRAM
        #pragma surface surf Road fullforwardshadows noambient vertex:vert
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        half4 _Color, _ShadowTint, _WarmColor;
        half _BumpScale, _Tiling, _Edge, _Ruts, _Wet, _Variety, _VertexAO;
        half _Softness, _WarmEdge;

        struct Input
        {
            float2 uv_MainTex;
            float3 worldPos;
            float3 worldNormal;
            float3 viewDir;
            float4 color : COLOR;
            INTERNAL_DATA
        };

        // У примитивной плоскости движка тангенты не записаны. Дорога знает,
        // что U идёт по локальному X, а V — по локальному Z, поэтому задаёт
        // базис сама и карта нормалей действительно работает.
        void vert(inout appdata_full v)
        {
            v.tangent = float4(1, 0, 0, -1);
        }

        half3 HemiAmbient(half3 n)
        {
            half u = n.y;
            return u > 0
                ? lerp(unity_AmbientEquator.rgb, unity_AmbientSky.rgb, u)
                : lerp(unity_AmbientEquator.rgb, unity_AmbientGround.rgb, -u);
        }

        half4 LightingRoad(SurfaceOutput s, half3 lightDir, half3 viewDir, half atten)
        {
            // Мелкий normal map на плоской земле пересекал ступени toon-света
            // крупными островами. На камне это стиль, на дороге — светлые
            // многоугольные лоскуты. Здесь переход непрерывный, но цвет тени и
            // тёплая граница остаются теми же, что у всей сцены.
            half ndl = dot(s.Normal, lightDir) * 0.5h + 0.5h;
            half lit = smoothstep(0.24h - _Softness, 0.76h + _Softness, ndl) * atten;

            // Gloss хранит локальную влажность. Блик узкий и слабый: мокрая
            // земля должна отвечать на фонарь, но не выглядеть пластиком.
            half3 h = normalize(lightDir + viewDir);
            half spec = pow(saturate(dot(s.Normal, h)), lerp(18.0h, 54.0h, s.Gloss));
            half3 wetSpec = _LightColor0.rgb * spec * s.Gloss * 0.045h * atten;

            #ifdef UNITY_PASS_FORWARDADD
                half4 add;
                // Плоскость дороги состоит всего из двух огромных треугольников.
                // Полный вклад маленькой лампы на такой поверхности читается
                // отдельным светлым полигоном. Локальный свет оставляем как
                // подсказку места, но не даём ему перекрасить всю тропу.
                // ForwardAdd складывается через Blend One One и потому сам по
                // себе не учитывает альфу мягкой кромки. Умножаем вклад явно,
                // иначе лампа снова очертит невидимый прямоугольник плоскости.
                add.rgb = (s.Albedo * _LightColor0.rgb * lit + wetSpec) * 0.42h * s.Alpha;
                add.a = s.Alpha;
                return add;
            #endif

            half3 shaded = s.Albedo * _ShadowTint.rgb;
            half3 col = lerp(shaded, s.Albedo * _LightColor0.rgb, lit) + wetSpec;
            half band = saturate(1.0h - abs(lit - 0.5h) * 2.4h);
            band = band * band * band;
            col += _WarmColor.rgb * s.Albedo * band * _WarmEdge;

            half4 c;
            c.rgb = col;
            c.a = s.Alpha;
            return c;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            // UV примитива равны 0…1, но сам он растянут масштабом объекта.
            // Переводим их в метры, чтобы tiling=1.8 всегда значил повтор через
            // 1.8 м — и на трёхметровой тропе, и на дороге длиной 30 м.
            float3 axisX = float3(unity_ObjectToWorld[0][0], unity_ObjectToWorld[1][0], unity_ObjectToWorld[2][0]);
            float3 axisZ = float3(unity_ObjectToWorld[0][2], unity_ObjectToWorld[1][2], unity_ObjectToWorld[2][2]);
            float widthM = max(length(axisX), 0.01);
            float lengthM = max(length(axisZ), 0.01);
            float2 centered = IN.uv_MainTex * 2.0 - 1.0;
            float2 localM = float2(centered.x * widthM, centered.y * lengthM) * 0.5;
            float2 uv = localM / max(_Tiling, 0.02);

            half4 baseSample = tex2D(_MainTex, uv);
            half4 macroSample = tex2D(_MainTex, uv * 0.113 + float2(0.37, 0.71));
            half macro = Luminance(macroSample.rgb);

            // Разные фазы слева и справа убирают зеркальность. Две длинные
            // синусоиды дают округлые земляные выступы и выемки, а не шумную
            // пилу из случайных пикселей.
            half phase = centered.x < 0 ? -1.9h : 2.7h;
            half edgeNoise = 0.50h
                + sin(localM.y * 0.71h + phase) * 0.20h
                + sin(localM.y * 1.83h - phase * 0.63h) * 0.11h
                + (macro - 0.5h) * 0.38h;
            edgeNoise = saturate(edgeNoise);
            half sideLimit = 1.0h - _Edge * lerp(0.22h, 0.88h, edgeNoise);
            half signedEdge = sideLimit - abs(centered.x);

            // Физическая кромка: 7–10 см в зависимости от edge=. В отличие от
            // screen-space dither её ширина не превращается на телефоне в
            // шахматную линию и не меняется от разрешения рендера.
            half featherM = lerp(0.07h, 0.10h, _Edge);
            half feather = max(featherM * 2.0h / widthM, fwidth(signedEdge) * 1.25h);
            half coverage = smoothstep(-feather, feather, signedEdge);
            // Невидимый хвост не должен записывать глубину всей исходной
            // прямоугольной плоскости. Порог 2% лежит уже за видимой кромкой.
            clip(coverage - 0.02h);

            // Две колеи слегка блуждают по длине, иначе идеальные параллельные
            // линии выдают процедуру сильнее, чем прямой край выдавал плоскость.
            half wobble = sin(localM.y * 0.31h) * 0.025h + sin(localM.y * 0.83h) * 0.012h;
            half trackDistance = abs(abs(centered.x + wobble) - 0.39h);
            half tracks = (1.0h - smoothstep(0.055h, 0.15h, trackDistance)) * _Ruts;
            tracks *= saturate(coverage * 1.4h);
            half wet = tracks * _Wet * lerp(0.55h, 1.0h, macro);

            half3 albedo = baseSample.rgb * _Color.rgb;
            albedo *= lerp(1.0h, 0.68h + macro * 0.66h, _Variety);
            albedo *= 1.0h - tracks * 0.18h - wet * 0.28h;
            o.Albedo = albedo;
            o.Alpha = coverage;
            o.Gloss = wet;
            o.Specular = 0;

            half3 bump = UnpackNormal(tex2D(_BumpMap, uv));
            o.Normal = normalize(lerp(half3(0, 0, 1), bump, _BumpScale));

            #ifndef UNITY_PASS_FORWARDADD
                half3 worldN = normalize(WorldNormalVector(IN, o.Normal));
                half ao = lerp(1.0h, IN.color.a, _VertexAO);
                o.Emission = HemiAmbient(worldN) * o.Albedo * ao;
                // Влажная колея под скользящим углом чуть отражает небо.
                half fresnel = pow(1.0h - saturate(dot(normalize(IN.viewDir), o.Normal)), 4.0h);
                o.Emission += unity_AmbientSky.rgb * wet * fresnel * 0.035h;
            #endif
        }
        ENDCG
    }

    FallBack "Diffuse"
}
