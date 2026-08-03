// Вода — река, лужа, море, пол храма после дождя.
//
// Настоящую воду считают отражениями и преломлением; в нашем стиле она держится
// на трёх дешёвых признаках, которых глазу достаточно:
//  · рябь ВЕРШИНАМИ — поверхность живёт, а не просто блестит;
//  · два слоя бликов, ползущих в разные стороны — это читается как течение;
//  · цвет глубины: у берега светлее, дальше темнее и насыщеннее.
//
// Прозрачность — обычный альфа-бленд без чтения экрана: чтение кадра на
// мобильных стоит дороже всей воды, а разницы в стилизованной картинке нет.
Shader "Lvn/Water"
{
    Properties
    {
        _Color ("Цвет мелководья", Color) = (0.30, 0.55, 0.62, 0.75)
        _DeepColor ("Цвет глубины", Color) = (0.08, 0.20, 0.32, 0.92)
        _Wave ("Высота ряби (м)", Range(0, 0.5)) = 0.035
        _WaveScale ("Частота ряби", Range(0.1, 8)) = 1.6
        _Speed ("Скорость", Range(0, 4)) = 0.7
        _Glint ("Блики", Range(0, 2)) = 0.6
        _GlintScale ("Частота бликов", Range(1, 60)) = 18
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        LOD 200

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            half4 _Color, _DeepColor;
            half _Wave, _WaveScale, _Speed, _Glint, _GlintScale;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float depth01 : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                half t = _Time.y * _Speed;
                // Две волны под углом друг к другу: одна вдоль, другая поперёк.
                // Одна-единственная даёт стиральную доску, а не воду.
                half h = sin(world.x * _WaveScale + t) * 0.6
                       + sin(world.z * _WaveScale * 1.37 - t * 0.85) * 0.4;
                v.vertex.y += h * _Wave;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = world;
                // Условная «глубина»: расстояние от края меша. Берём из uv по
                // высоте объекта — достаточно, чтобы берег читался светлее.
                o.depth01 = saturate(v.texcoord.y);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = lerp(_Color, _DeepColor, i.depth01);
                half t = _Time.y * _Speed;
                // Блики: две сетки синусов, ползущие навстречу. Резкий порог
                // превращает их из «пятен» в искры на гребнях.
                half g1 = sin(i.world.x * _GlintScale + t * 1.7) * sin(i.world.z * _GlintScale * 0.9 - t * 1.1);
                half g2 = sin(i.world.x * _GlintScale * 0.6 - t * 0.9) * sin(i.world.z * _GlintScale * 1.3 + t * 1.4);
                half glint = saturate(pow(saturate(max(g1, g2)), 8)) * _Glint;
                c.rgb += glint;
                return c;
            }
            ENDCG
        }
    }
    FallBack "Transparent/Diffuse"
}
