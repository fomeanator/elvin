// Металл — доспех, клинок, золото, котёл, рельс.
//
// Почему одного `toon` мало: он рисует доспех и плащ одинаково, а глаз
// различает их не по цвету, а по ХАРАКТЕРУ БЛИКА. У ткани блик широкий и
// мягкий, у металла — узкий, яркий и ВЫТЯНУТЫЙ вдоль поверхности (полосы на
// шлифованном железе, «жилка» на клинке). Без этого любая броня выглядит
// крашеным пластиком, сколько цвет ни подбирай.
//
// Здесь три признака металла, все дешёвые:
//  · анизотропный блик — вытянутый поперёк направления шлифовки;
//  · отражение НЕБА по Френелю: металл забирает цвет окружения, а не только
//    свой; без этого он мёртвый;
//  · ступенчатая база от нашего toon, чтобы стиль остался общим.
Shader "Lvn/Metal"
{
    Properties
    {
        _Color ("Цвет", Color) = (0.62, 0.65, 0.70, 1)
        _MainTex ("Текстура", 2D) = "white" {}
        _BumpMap ("Карта нормалей", 2D) = "bump" {}
        _BumpScale ("Сила рельефа", Range(0, 3)) = 1
        _ShadowTint ("Цвет тени", Color) = (0.30, 0.36, 0.52, 1)
        _Steps ("Ступеней света", Range(1, 4)) = 2
        _Softness ("Мягкость границы", Range(0.001, 0.3)) = 0.06
        _Gloss ("Узость блика", Range(4, 256)) = 64
        _Spec ("Сила блика", Range(0, 4)) = 0.7
        _Aniso ("Вытянутость блика", Range(0, 1)) = 0.6
        _SkyTint ("Цвет отражённого неба", Color) = (0.55, 0.68, 0.85, 1)
        _Reflect ("Отражение", Range(0, 1)) = 0.16
        _Outline ("Толщина обводки, м", Range(0, 0.1)) = 0
        _OutlineColor ("Цвет обводки", Color) = (0.06, 0.07, 0.11, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Metal fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color, _ShadowTint, _SkyTint;
        half _Steps, _Softness, _Gloss, _Spec, _Aniso, _Reflect, _BumpScale;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_BumpMap;
            float3 viewDir;
        };

        half4 LightingMetal(SurfaceOutput s, half3 lightDir, half3 viewDir, half atten)
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

            // Анизотропия: блик сужаем по одной оси и растягиваем по другой.
            // Дёшево и достаточно — «жилка» на клинке появляется именно так.
            half3 h = normalize(lightDir + viewDir);
            half ndh = saturate(dot(s.Normal, h));
            // Анизотропия ВЫТЯГИВАЕТ блик, но не растворяет его: экспонента
            // падает самое большее вдвое. Первая версия роняла её вшестеро, и
            // «узкая жилка» превращалась в заливку всей поверхности — металл
            // выходил белым пятном при любом солнце.
            half stretch = lerp(1.0, 0.55, _Aniso * abs(dot(s.Normal, float3(0, 1, 0))));
            // И только на освещённой стороне: блик с теневой — не металл, а
            // ошибка модели освещения.
            half spec = pow(ndh, max(16.0, _Gloss * stretch)) * _Spec * atten * lit;

            half3 shaded = s.Albedo * _ShadowTint.rgb;
            half3 col = lerp(shaded, s.Albedo * _LightColor0.rgb, lit);
            col += _LightColor0.rgb * spec;

            half4 c;
            c.rgb = col;
            c.a = s.Alpha;
            return c;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Alpha = c.a;

            fixed3 n = UnpackNormal(tex2D(_BumpMap, IN.uv_BumpMap));
            o.Normal = normalize(lerp(fixed3(0, 0, 1), n, _BumpScale));

            // Отражение неба по Френелю: вскользь металл почти зеркало. Без
            // этого он выглядит как серый пластик даже с идеальным бликом.
            // Отражение УМНОЖАЕМ на собственный цвет, а не прибавляем ровным
            // светом: иначе тёмный металл светится так же, как светлый, и любая
            // яркая сцена выжигает его в белое пятно.
            half fres = pow(1.0 - saturate(dot(normalize(IN.viewDir), o.Normal)), 4);
            o.Emission = _SkyTint.rgb * c.rgb * fres * _Reflect;
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
