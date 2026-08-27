// СТВОР ПОРТАЛА КАК ОБЪЕКТ СЦЕНЫ, а не постэффект кадра.
//
// Первая версия жила в полноэкранном стеке (LvnFx, OnRenderImage) — и оттуда
// росла вся её ненадёжность: без камеры в сцене эффект молча не рисуется, чужая
// уборка сбрасывает стек, а лечь ПОД героиню постэффект не может в принципе,
// потому что работает с уже готовым кадром.
//
// Здесь створ — обычный прозрачный слой канваса: рисуется всегда, живёт между
// фоном и актёрами, и никакая уборка эффектов его не касается.
Shader "Hidden/LvnPortalDisk"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Open ("Раскрытие 0..1", Float) = 0
        _Color ("Свечение", Color) = (0.48, 0.84, 1, 1)
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
                if (open <= 0.001) return fixed4(0, 0, 0, 0);

                // Круг в собственных координатах слоя: слой квадратный, поэтому
                // овала не будет ни на каком экране.
                float2 d = i.uv - 0.5;
                float dist = length(d) * 2.0;          // 0 в центре, 1 у края
                float ang = atan2(d.y, d.x);
                float t = _Time.y;

                float edge = open;                      // текущий радиус створа
                if (dist > edge) return fixed4(0, 0, 0, 0);

                // Горловина: к центру — свет, к краю — прозрачность.
                float throat = 1.0 - smoothstep(edge * 0.15, edge * 0.98, dist);
                // Кромка: тонкое яркое кольцо по самому краю.
                float rim = 1.0 - smoothstep(0.0, edge * 0.16, abs(dist - edge));
                // Вихрь: полосы, закрученные вокруг центра.
                float swirl = 0.62 + 0.38 * sin(ang * 3.0 - t * 2.1 + dist * 14.0);
                float grain = 0.85 + 0.15 * hash(floor(float2(ang * 6.0, t * 6.0)));

                float3 rgb = _Color.rgb * (throat * swirl * grain + rim * 1.4);
                float alpha = saturate(throat * 0.92 + rim) * open * i.color.a;
                return fixed4(rgb, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
