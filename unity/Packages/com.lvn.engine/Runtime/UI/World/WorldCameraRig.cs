using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Camera effects for the Canvas scene — the uGUI mirror of
    /// <see cref="CameraRig"/>. Shakes / zooms / pans a target
    /// <see cref="RectTransform"/> (the GameRoot holding background + actors) in
    /// the Update loop with unscaled time, so the scene moves while the UITK
    /// dialogue/choice chrome above it stays put. No real camera needed — it is
    /// pure transform work, exactly like the UITK rig.
    /// </summary>
    public sealed class WorldCameraRig : MonoBehaviour
    {
        private RectTransform _t;

        // shake
        private float _shakeAmp, _shakeDur, _shakeStart = -1f;
        // zoom
        private float _zoomFrom = 1f, _zoomTo = 1f, _zoomDur, _zoomStart = -1f, _scale = 1f;
        // pan
        private Vector2 _panFrom, _panTo, _panBase;
        private float _panDur, _panStart = -1f;

        private static float Now => Time.realtimeSinceStartup;

        public void Bind(RectTransform target) { _t = target; }

        public void Shake(float amplitude, float seconds)
        {
            if (_t == null || amplitude <= 0f || seconds <= 0f) return;
            _shakeAmp = amplitude; _shakeDur = seconds; _shakeStart = Now;
        }

        public void Zoom(float factor, float seconds)
        {
            if (_t == null) return;
            _zoomFrom = _scale; _zoomTo = Mathf.Max(0.1f, factor); _scale = _zoomTo;
            if (seconds <= 0f) { _t.localScale = new Vector3(_zoomTo, _zoomTo, 1f); _zoomStart = -1f; return; }
            _zoomDur = seconds; _zoomStart = Now;
        }

        public void Pan(float targetX, float targetY, float seconds)
        {
            if (_t == null) return;
            _panTo = new Vector2(targetX, targetY);
            _panBase = _t.anchoredPosition - ShakeOffset(); // pan base excludes live shake
            _panFrom = _panBase;
            if (seconds <= 0f) { _panBase = _panTo; _panStart = -1f; ApplyPosition(); return; }
            _panDur = seconds; _panStart = Now;
        }

        public void Reset(float seconds)
        {
            _shakeStart = -1f;
            Pan(0f, 0f, seconds);
            Zoom(1f, seconds);
        }

        private Vector2 ShakeOffset()
        {
            if (_shakeStart < 0f) return Vector2.zero;
            float k = 1f - Mathf.Clamp01((Now - _shakeStart) / _shakeDur);
            if (k <= 0f) { _shakeStart = -1f; return Vector2.zero; }
            return new Vector2((Random.value * 2f - 1f) * _shakeAmp * k,
                               (Random.value * 2f - 1f) * _shakeAmp * k);
        }

        private void ApplyPosition() => _t.anchoredPosition = _panBase + ShakeOffset();

        private void Update()
        {
            if (_t == null) return;

            if (_panStart >= 0f)
            {
                float p = Mathf.Clamp01((Now - _panStart) / Mathf.Max(0.0001f, _panDur));
                _panBase = Vector2.LerpUnclamped(_panFrom, _panTo, p);
                if (p >= 1f) _panStart = -1f;
            }

            if (_zoomStart >= 0f)
            {
                float p = Mathf.Clamp01((Now - _zoomStart) / Mathf.Max(0.0001f, _zoomDur));
                float s = Mathf.LerpUnclamped(_zoomFrom, _zoomTo, p);
                _t.localScale = new Vector3(s, s, 1f);
                if (p >= 1f) _zoomStart = -1f;
            }

            // Reapply position every frame while shaking or panning.
            if (_shakeStart >= 0f || _panStart >= 0f) ApplyPosition();
            else if (_t.anchoredPosition != _panBase) _t.anchoredPosition = _panBase;
        }
    }
}
