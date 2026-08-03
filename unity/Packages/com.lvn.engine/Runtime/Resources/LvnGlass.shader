// Стекло и кристалл — окно, витраж, лёд, самоцвет, магический барьер.
//
// Преломление честным чтением кадра стоит на мобильных дороже, чем вся
// остальная сцена, и в рисованном стиле оно почти не читается. Вместо него —
// три признака, которых глазу достаточно: край плотнее середины (Френель),
// холодный подсвет изнутри и узкий блик. Так стекло выглядит стеклом даже
// будучи одним прозрачным конусом.
Shader "Lvn/Glass"
{
    Properties
    {
        _Color ("Цвет стекла", Color) = (0.62, 0.82, 0.95, 0.35)
        _EdgeColor ("Цвет края", Color) = (0.85, 0.95, 1, 1)
        _Edge ("Плотность края", Range(0.5, 6)) = 2.5
        _Inner ("Внутренний подсвет", Range(0, 2)) = 0.35
        _Gloss ("Блик", Range(0, 2)) = 0.8
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
            #include "UnityLightingCommon.cginc"

            half4 _Color, _EdgeColor;
            half _Edge, _Inner, _Gloss;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 view : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.view = normalize(_WorldSpaceCameraPos - world);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float3 v = normalize(i.view);
                // Френель: под прямым взглядом стекло прозрачно, вскользь —
                // почти зеркало. Это единственное, что действительно нужно.
                half fres = pow(1.0 - saturate(dot(n, v)), _Edge);

                // Узкий блик от главного источника — «полировка».
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                half spec = pow(saturate(dot(reflect(-l, n), v)), 48) * _Gloss;

                half3 col = lerp(_Color.rgb, _EdgeColor.rgb, fres) + spec + _Inner * fres;
                half a = saturate(_Color.a + fres * 0.55 + spec);
                return half4(col, a);
            }
            ENDCG
        }
    }
    FallBack "Transparent/Diffuse"
}
