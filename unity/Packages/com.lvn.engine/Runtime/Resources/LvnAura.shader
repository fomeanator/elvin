// Аура — энергия вокруг тела: щит, сила, порча, благословение.
//
// Стиль здесь важнее физики. Настоящее свечение считают объёмом и рассеянием,
// а рисованное держится на трёх вещах, и все три дешёвые:
//  · Френель — край светится сильнее середины. Это даёт «оболочку», а не
//    закрашенную фигуру.
//  · Пульсация — свет дышит. Неподвижная аура читается как ошибка материала.
//  · Полосы — движущийся узор внутри свечения, чтобы это была ЭНЕРГИЯ, а не
//    цветной туман.
//
// Рисуется поверх тела чуть большего размера (оболочкой) либо на самом теле.
// Никакой записи в буфер глубины: аура не должна закрывать то, что за ней.
Shader "Lvn/Aura"
{
    Properties
    {
        _Color ("Цвет ауры", Color) = (0.45, 0.75, 1, 1)
        _Power ("Сила", Range(0, 8)) = 1.4
        _Edge ("Узость края", Range(0.5, 6)) = 2.2
        _Pulse ("Пульсация", Range(0, 4)) = 1.4
        _PulseDepth ("Глубина пульсации", Range(0, 1)) = 0.35
        _Bands ("Частота полос", Range(0, 40)) = 12
        _Flow ("Скорость полос", Range(-4, 4)) = 1.2
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        // Чистое сложение: яркость уже промодулирована формой в шейдере, и
        // умножать её ЕЩЁ РАЗ на прозрачность значит гасить свет квадратично.
        Blend One One
        ZWrite Off
        Cull Back
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            half4 _Color;
            half _Power, _Edge, _Pulse, _PulseDepth, _Bands, _Flow;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 view : TEXCOORD1;
                float3 obj : TEXCOORD2;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.view = normalize(_WorldSpaceCameraPos - world);
                o.obj = v.vertex.xyz;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // Френель: чем ближе поверхность к касательной к взгляду, тем
                // ярче. Отсюда ощущение оболочки вокруг фигуры.
                half fres = 1.0 - saturate(dot(normalize(i.normal), normalize(i.view)));
                half edge = pow(fres, _Edge);

                // Дыхание. Синус по времени — самое дешёвое, что читается как
                // «живое»; без него аура выглядит наклейкой.
                half pulse = 1.0 + _PulseDepth * sin(_Time.y * _Pulse * 6.2831);

                // Полосы вдоль вертикали тела: энергия течёт, а не стоит.
                half bands = 0.5 + 0.5 * sin(i.obj.y * _Bands - _Time.y * _Flow * 6.2831);
                bands = lerp(1.0, bands, 0.45);   // не в ноль: провалы читаются дырами

                // ФОРМА И ЯРКОСТЬ — РАЗНЫЕ ВЕЛИЧИНЫ, и раньше они были одной.
                //
                // Прозрачность обязана лежать в 0…1: по ней смешивают. А вот
                // яркость свечения потолка не имеет — аура на то и свет, чтобы
                // быть ярче белого. Пока обе считались одним saturate'ом, сила
                // выше единицы не значила ничего: `glow=8` давал ровно то же,
                // что `glow=1`, и шкала эмиссии показывала четыре одинаковых
                // шара.
                half shape = edge * pulse * bands;   // форма свечения, 0…1
                half a = saturate(shape);            // ею смешиваем
                half3 lit = _Color.rgb * shape * _Power;  // а ею светим, без потолка
                return half4(lit, a * _Color.a);
            }
            ENDCG
        }
    }
    FallBack Off
}
