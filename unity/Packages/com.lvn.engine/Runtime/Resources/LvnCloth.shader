// Ткань — плащ, штора, знамя, платье, обивка.
//
// Ткань отличается от металла ровно противоположным поведением света: блик
// широкий, мягкий и сидит НЕ там, где отражение, а по краю силуэта. Ворс
// (бархат, сукно, шерсть) ловит свет кромкой — потому плащ на просвет всегда
// светлее по контуру, а не в середине. Металлический блик на плаще выдаёт
// подделку мгновенно, поэтому под ткань нужен свой материал, а не «toon
// потемнее».
Shader "Lvn/Cloth"
{
    Properties
    {
        _Color ("Цвет", Color) = (0.45, 0.16, 0.18, 1)
        _MainTex ("Текстура", 2D) = "white" {}
        _BumpMap ("Карта нормалей", 2D) = "bump" {}
        _BumpScale ("Сила рельефа", Range(0, 3)) = 1
        _ShadowTint ("Цвет тени", Color) = (0.32, 0.30, 0.45, 1)
        _Steps ("Ступеней света", Range(1, 4)) = 3
        _Softness ("Мягкость границы", Range(0.001, 0.4)) = 0.18
        _Sheen ("Ворс по краю", Range(0, 2)) = 0.7
        _SheenColor ("Цвет ворса", Color) = (1, 0.86, 0.78, 1)
        _SheenPower ("Узость ворса", Range(0.5, 6)) = 1.8
        _Outline ("Толщина обводки, м", Range(0, 0.1)) = 0
        _OutlineColor ("Цвет обводки", Color) = (0.06, 0.07, 0.11, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Cloth fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color, _ShadowTint, _SheenColor;
        half _Steps, _Softness, _Sheen, _SheenPower, _BumpScale;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_BumpMap;
            float3 viewDir;
        };

        half4 LightingCloth(SurfaceOutput s, half3 lightDir, half3 viewDir, half atten)
        {
            // Ступеней больше и границы мягче, чем у металла: ткань драпируется,
            // и жёсткие полосы на складках читаются как ошибка, а не как стиль.
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

            // Ворс: свет ловится КРОМКОЙ, поэтому яркость растёт там, где
            // поверхность уходит от взгляда, и только со стороны источника.
            half rim = pow(1.0 - saturate(dot(s.Normal, viewDir)), _SheenPower);
            half sheen = rim * saturate(dot(s.Normal, lightDir) * 0.5 + 0.5) * _Sheen * atten;

            half3 shaded = s.Albedo * _ShadowTint.rgb;
            half4 c;
            c.rgb = lerp(shaded, s.Albedo * _LightColor0.rgb, lit)
                  + _SheenColor.rgb * s.Albedo * sheen;
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
