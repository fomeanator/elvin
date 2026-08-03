// Небо сцены — то, что занимает половину почти каждого кадра.
//
// Стандартный процедурный skybox Unity узнаётся мгновенно: по нему видно, что
// игра сделана на Unity и что небом никто не занимался. При этом небо новеллы
// не обязано быть физически верным — оно должно задавать ВРЕМЯ СУТОК и
// настроение, а это три цвета и одно светило.
//
// Что здесь есть:
//  · градиент горизонт → зенит с управляемой резкостью перехода;
//  · светило (солнце или луна) с мягким ореолом, стоящее ТАМ ЖЕ, откуда светит
//    направленный свет сцены — иначе тени идут в одну сторону, а солнце висит
//    в другой, и кадр разваливается;
//  · звёзды, проступающие тем сильнее, чем темнее небо: ночь без них пустая;
//  · полоса у горизонта — дымка, которая смыкает небо с туманом сцены.
Shader "Lvn/Sky"
{
    Properties
    {
        _Top ("Цвет зенита", Color) = (0.04, 0.06, 0.13, 1)
        _Horizon ("Цвет горизонта", Color) = (0.17, 0.23, 0.30, 1)
        _Ground ("Цвет под горизонтом", Color) = (0.06, 0.07, 0.09, 1)
        _Sharp ("Резкость перехода", Range(0.5, 8)) = 2.2
        _SunColor ("Цвет светила", Color) = (1, 0.95, 0.85, 1)
        _SunSize ("Размер светила", Range(0.0005, 0.05)) = 0.004
        _SunGlow ("Ореол", Range(0, 1)) = 0.12
        _Stars ("Звёзды", Range(0, 1)) = 0.5
        _Haze ("Дымка у горизонта", Range(0, 1)) = 0.35
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"

            fixed4 _Top, _Horizon, _Ground, _SunColor;
            half _Sharp, _SunSize, _SunGlow, _Stars, _Haze;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            // Звёзды: хеш по направлению, порог — только редкие точки. Текстуру
            // ради этого таскать незачем, а рисунок всё равно случайный.
            half stars(float3 d)
            {
                float3 p = floor(d * 900.0);   // мельче: крупная сетка читалась квадратами
                float n = frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                return smoothstep(0.9992, 1.0, n);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                half up = d.y;

                // Небо и «земля» (то, что ниже горизонта): у сцены свой пол, но
                // за его краем всё равно должно быть не чёрное ничто.
                half t = pow(saturate(up), 1.0 / _Sharp);
                fixed3 sky = lerp(_Horizon.rgb, _Top.rgb, t);
                fixed3 col = up >= 0 ? sky : lerp(_Horizon.rgb, _Ground.rgb, saturate(-up * 3));

                // Дымка ровно у линии горизонта — она смыкает небо с туманом
                // сцены, иначе видно шов между задником и туманом.
                half band = exp(-abs(up) * 22.0) * _Haze;
                col = lerp(col, _Horizon.rgb * 1.35, band);

                // Светило — там же, откуда светит направленный источник.
                float3 sunDir = normalize(_WorldSpaceLightPos0.xyz);
                half cosA = dot(d, sunDir);
                half disc = smoothstep(1.0 - _SunSize, 1.0 - _SunSize * 0.35, cosA);
                half glow = pow(saturate(cosA), 220.0 * (1.0 - _SunGlow) + 8.0) * _SunGlow;
                col += _SunColor.rgb * (disc + glow);

                // Звёзды проступают по темноте неба — днём их не видно само собой.
                half night = saturate(1.0 - Luminance(sky) * 3.0);
                col += stars(d) * night * _Stars * saturate(up * 2);

                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
