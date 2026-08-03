using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Плавная смена света: цвет и сила источника переезжают за N секунд.
    ///
    /// <para>Время суток — самый частый повод менять свет, и меняться оно
    /// должно НА ГЛАЗАХ: рассвет, который случается за один кадр, читается как
    /// сбой, а не как рассвет. Мгновенная установка остаётся поведением по
    /// умолчанию (`dur` не задан) — плавность нужна там, где её просят.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Lvn3DLightFade : MonoBehaviour
    {
        private Lvn3DBackdrop _owner;
        private Light _light;
        private Color _fromColor, _toColor;
        private float _fromPower, _toPower;
        private float _time, _duration;

        public static void Run(Lvn3DBackdrop owner, Light light, Color? color, float? power, float seconds)
        {
            if (light == null) return;
            var f = light.GetComponent<Lvn3DLightFade>() ?? light.gameObject.AddComponent<Lvn3DLightFade>();
            f._owner = owner;
            f._light = light;
            f._fromColor = light.color;
            f._toColor = color ?? light.color;
            f._fromPower = light.intensity;
            f._toPower = power ?? light.intensity;
            f._duration = Mathf.Max(0.01f, seconds);
            f._time = 0f;
            f.enabled = true;
            owner?.SetLive(true);   // пока свет едет, кадр обязан обновляться
        }

        private void LateUpdate()
        {
            if (_light == null) { enabled = false; return; }
            _time += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(_time / _duration);
            k = k * k * (3f - 2f * k);      // мягко на концах, как и переезд тел
            _light.color = Color.Lerp(_fromColor, _toColor, k);
            _light.intensity = Mathf.Lerp(_fromPower, _toPower, k);
            if (k >= 1f) enabled = false;
        }
    }
}
