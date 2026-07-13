using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI
{
    /// <summary>
    /// The full-screen effects overlay (top z-order): screen fades, dim,
    /// flash, tint washes, and blur. Each effect layers on the same element.
    /// </summary>
    public sealed class FxLayer : VisualElement
    {
        private VisualElement _blurOverlay;

        // The one live veil tween. Every veil effect must kill the previous
        // tween before starting (or setting the colour instantly): two tweens
        // lerping the same backgroundColor race, and the LOSER's target shows —
        // a chapter-end fade-to-black could keep repainting over the next
        // chapter's instant Clear(0) and leave the stage veiled black.
        private ValueAnimation<float> _veilAnim;
        private ValueAnimation<float> _blurAnim;

        private void StopVeil() { _veilAnim?.Stop(); _veilAnim = null; }
        private void StopBlur() { _blurAnim?.Stop(); _blurAnim = null; }

        public FxLayer()
        {
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            style.backgroundColor = Color.clear;
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>Animate the veil to <paramref name="target"/> over
        /// <paramref name="seconds"/> (0 = instant).</summary>
        public void FadeTo(Color target, float seconds)
        {
            StopVeil();
            var from = resolvedStyle.backgroundColor;
            if (seconds <= 0f)
            {
                style.backgroundColor = target;
                return;
            }
            int ms = Mathf.Max(1, Mathf.RoundToInt(seconds * 1000f));
            _veilAnim = experimental.animation
                .Start(0f, 1f, ms, (e, t) => e.style.backgroundColor = Color.Lerp(from, target, t))
                .Ease(Easing.InOutSine)
                .KeepAlive(); // pooled otherwise — a recycled handle could Stop() someone else's tween
        }

        /// <summary>Fade to an opaque colour (default black). Common before a
        /// background swap.</summary>
        public void Fade(Color to, float seconds) => FadeTo(new Color(to.r, to.g, to.b, 1f), seconds);

        /// <summary>Clear the veil, revealing the scene.</summary>
        public void Clear(float seconds) => FadeTo(Color.clear, seconds);

        /// <summary>A partial black veil for a focus pull (0 = none, 1 = black).</summary>
        public void Dim(float alpha, float seconds) =>
            FadeTo(new Color(0f, 0f, 0f, Mathf.Clamp01(alpha)), seconds);

        /// <summary>Quick white (or coloured) flash that fades back to clear.
        /// Common for lightning, impacts, camera flashes.</summary>
        public void Flash(Color colour, float duration)
        {
            StopVeil();
            var from = new Color(colour.r, colour.g, colour.b, 0.8f);
            style.backgroundColor = from;
            int ms = Mathf.Max(1, Mathf.RoundToInt(duration * 1000f));
            _veilAnim = experimental.animation
                .Start(0f, 1f, ms, (e, t) =>
                {
                    float a = Mathf.Lerp(0.8f, 0f, t);
                    e.style.backgroundColor = new Color(from.r, from.g, from.b, a);
                })
                .Ease(Easing.OutCubic)
                .KeepAlive();
        }

        /// <summary>Coloured tint wash over the screen (cold/warm/sepia).
        /// Animates to the target alpha then holds until <see cref="Clear"/>.</summary>
        public void Tint(Color colour, float alpha, float seconds)
        {
            var target = new Color(colour.r, colour.g, colour.b, Mathf.Clamp01(alpha));
            FadeTo(target, seconds);
        }

        /// <summary>Apply a Gaussian-like blur overlay. Creates a semi-transparent
        /// white veil to simulate depth-of-field; real blur requires a render
        /// texture and is left to project-specific shaders.</summary>
        public void Blur(float alpha, float seconds)
        {
            if (_blurOverlay == null)
            {
                _blurOverlay = new VisualElement();
                _blurOverlay.style.position = Position.Absolute;
                _blurOverlay.style.left = 0;
                _blurOverlay.style.right = 0;
                _blurOverlay.style.top = 0;
                _blurOverlay.style.bottom = 0;
                _blurOverlay.style.backgroundColor = new Color(1f, 1f, 1f, 0f);
                _blurOverlay.pickingMode = PickingMode.Ignore;
                Add(_blurOverlay);
            }
            StopBlur();
            var target = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
            var from = _blurOverlay.resolvedStyle.backgroundColor;
            if (seconds <= 0f)
            {
                _blurOverlay.style.backgroundColor = target;
                return;
            }
            int ms = Mathf.Max(1, Mathf.RoundToInt(seconds * 1000f));
            _blurAnim = _blurOverlay.experimental.animation
                .Start(0f, 1f, ms, (e, t) => e.style.backgroundColor = Color.Lerp(from, target, t))
                .Ease(Easing.InOutSine)
                .KeepAlive();
        }

        /// <summary>Clear the blur overlay.</summary>
        public void ClearBlur(float seconds)
        {
            if (_blurOverlay == null) return;
            StopBlur();
            var from = _blurOverlay.resolvedStyle.backgroundColor;
            if (seconds <= 0f)
            {
                _blurOverlay.style.backgroundColor = Color.clear;
                return;
            }
            int ms = Mathf.Max(1, Mathf.RoundToInt(seconds * 1000f));
            _blurAnim = _blurOverlay.experimental.animation
                .Start(0f, 1f, ms, (e, t) => e.style.backgroundColor = Color.Lerp(from, Color.clear, t))
                .Ease(Easing.InOutSine)
                .KeepAlive();
        }
    }
}
