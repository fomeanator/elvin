// Растворение — появление и исчезновение чего угодно: призрак проявился,
// дверь рассыпалась, враг сгорел, предмет собрался из искр.
//
// Один шейдер закрывает половину всех переходов в сцене, и в этом его смысл:
// иначе каждый такой момент требует своей анимации, своего арта и своего кода.
// Здесь достаточно гнать одно число от 0 до 1.
//
// Как устроено: порог по псевдослучайному узору. Пиксели, чей узор ниже порога,
// отбрасываются; узкая полоса вокруг порога светится — это и есть «край
// горения», без которого растворение выглядит просто дырявым.
Shader "Lvn/Dissolve"
{
    Properties
    {
        // Кромка распада светится: в единицах света 2–4.
        _EdgePower ("Яркость кромки", Range(0, 8)) = 2
        _Color ("Цвет", Color) = (1,1,1,1)
        _MainTex ("Текстура", 2D) = "white" {}
        _Amount ("Растворение", Range(0, 1)) = 0
        _EdgeColor ("Цвет края", Color) = (1, 0.55, 0.15, 1)
        _EdgeWidth ("Ширина края", Range(0.001, 0.3)) = 0.06
        _Scale ("Крупность узора", Range(1, 40)) = 12
        _ShadowTint ("Цвет тени", Color) = (0.35, 0.42, 0.6, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        LOD 200
        Cull Off

        CGPROGRAM
        #pragma surface surf Lambert addshadow
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color, _EdgeColor, _ShadowTint;
        half _Amount, _EdgeWidth, _Scale, _EdgePower;

        struct Input
        {
            float2 uv_MainTex;
            float3 worldPos;
        };

        // Дешёвый псевдошум: три синуса в разных направлениях. Настоящий
        // Perlin красивее, но стоит текстуры в каждом наборе — а разницу на
        // растворении длиной в полсекунды никто не увидит.
        half noise(float3 p)
        {
            half n = sin(p.x * 1.7) * sin(p.y * 2.3) * sin(p.z * 1.9)
                   + sin(p.x * 4.1 + 1.3) * sin(p.z * 3.7) * 0.5
                   + sin(p.y * 5.3 + 2.1) * 0.25;
            return saturate(n * 0.5 + 0.5);
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            half n = noise(IN.worldPos * _Scale * 0.1);

            // Порог идёт от 0 к 1: при 0 объект целый, при 1 исчез полностью.
            clip(n - _Amount);

            // Полоса у самого порога светится — «горящий край». Без него объект
            // просто дырявится, и переход читается как артефакт.
            half edge = smoothstep(_Amount, _Amount + _EdgeWidth, n);
            o.Albedo = lerp(_EdgeColor.rgb, c.rgb, edge);
            o.Emission = _EdgeColor.rgb * (1.0 - edge) * _EdgePower;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
