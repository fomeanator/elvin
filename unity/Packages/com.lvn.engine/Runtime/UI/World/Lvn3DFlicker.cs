using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Дрожание источника света — костёр, факел, свеча, неисправная лампа.
    ///
    /// <para>Ровно горящий огонь читается лампочкой: глаз ждёт от пламени
    /// неровности. Дрожь берётся суммой двух синусов разной частоты, а не
    /// случайным числом: случайное мерцание выглядит электрическим сбоем, а
    /// огонь дышит — быстро, но плавно.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Lvn3DFlicker : MonoBehaviour
    {
        private Light _light;
        private Lvn3DBackdrop _owner;
        private float _base = 1f;
        private float _amp = 0.25f;
        private float _phase;

        public void Bind(Lvn3DBackdrop owner, float baseIntensity, float amplitude)
        {
            _owner = owner;
            _light = GetComponent<Light>();
            _base = baseIntensity;
            _amp = Mathf.Clamp01(amplitude);
            // Своя фаза у каждого огня: два костра рядом не должны дышать в такт.
            if (_phase == 0f) _phase = (GetInstanceID() % 997) * 0.01f;
            enabled = true;
        }

        private void LateUpdate()
        {
            if (_light == null) { enabled = false; return; }
            float t = Time.unscaledTime * 6.3f + _phase;
            float w = Mathf.Sin(t) * 0.6f + Mathf.Sin(t * 2.7f + 1.3f) * 0.4f;
            _light.intensity = Mathf.Max(0f, _base * (1f + w * _amp));
        }
    }
}
