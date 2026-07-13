using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI
{
    /// <summary>
    /// The full-screen effects overlay (top z-order): screen fades, dim,
    /// flash, tint washes, and blur.
    ///
    /// Rendering model: the veil is TWO stacked children cross-faded by
    /// <c>style.opacity</c>. The target colour (with its alpha) is written to a
    /// layer's background ONCE per effect start; every animated frame after
    /// that only moves opacity — the composited fast-path — so a running fade
    /// never re-tessellates geometry the way animating backgroundColor does.
    /// A pleasant side effect: a <see cref="Flash"/> rides the staging layer
    /// and no longer wipes out a persistent dim/tint underneath it.
    /// </summary>
    public sealed class FxLayer : VisualElement
    {
        // _front holds the CURRENT persistent veil (dim/tint/fade colour);
        // _back is the staging layer the next effect fades in on (and the
        // flash layer). They swap roles after each cross-fade.
        private VisualElement _front;
        private VisualElement _back;
        private VisualElement _blurOverlay;

        // The one live veil tween. Every veil effect must kill the previous
        // tween before starting (or setting the state instantly): two tweens
        // driving the same layers race, and the LOSER's target shows — a
        // chapter-end fade-to-black could keep repainting over the next
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
            pickingMode = PickingMode.Ignore;

            _front = MakeVeilLayer();
            _back = MakeVeilLayer();
            Add(_front);
            Add(_back);
        }

        private static VisualElement MakeVeilLayer()
        {
            var v = new VisualElement();
            v.style.position = Position.Absolute;
            v.style.left = 0;
            v.style.right = 0;
            v.style.top = 0;
            v.style.bottom = 0;
            v.style.backgroundColor = Color.clear;
            v.style.opacity = 0f;
            v.pickingMode = PickingMode.Ignore;
            return v;
        }

        /// <summary>Animate the veil to <paramref name="target"/> over
        /// <paramref name="seconds"/> (0 = instant). The target's alpha is baked
        /// into the staging layer's colour; only opacity animates.</summary>
        public void FadeTo(Color target, float seconds)
        {
            StopVeil();
            if (seconds <= 0f)
            {
                _front.style.backgroundColor = target;
                _front.style.opacity = 1f;
                _back.style.opacity = 0f;
                return;
            }

            var incoming = _back;
            var outgoing = _front;
            incoming.style.backgroundColor = target;
            incoming.style.opacity = 0f;
            // Inline style, not resolvedStyle: every veil write goes through
            // style.opacity, and resolvedStyle lags a frame behind mid-flight.
            float fromOut = outgoing.style.opacity.value;

            int ms = Mathf.Max(1, Mathf.RoundToInt(seconds * 1000f));
            _veilAnim = experimental.animation
                .Start(0f, 1f, ms, (_, t) =>
                {
                    incoming.style.opacity = t;
                    outgoing.style.opacity = fromOut * (1f - t);
                })
                .Ease(Easing.InOutSine)
                .KeepAlive(); // pooled otherwise — a recycled handle could Stop() someone else's tween

            // Swap roles now, not OnCompleted: if another effect interrupts the
            // tween mid-flight, "front" must already mean the layer carrying the
            // newest target so its state is what the next cross-fade fades out.
            _front = incoming;
            _back = outgoing;
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
        /// Common for lightning, impacts, camera flashes. Rides the staging
        /// layer, so a persistent dim/tint survives underneath.</summary>
        public void Flash(Color colour, float duration)
        {
            StopVeil();
            var layer = _back;
            layer.style.backgroundColor = new Color(colour.r, colour.g, colour.b, 0.8f);
            layer.style.opacity = 1f;
            int ms = Mathf.Max(1, Mathf.RoundToInt(duration * 1000f));
            _veilAnim = experimental.animation
                .Start(0f, 1f, ms, (_, t) => layer.style.opacity = 1f - t)
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
                _blurOverlay = MakeVeilLayer();
                _blurOverlay.style.backgroundColor = Color.white; // alpha via opacity
                Add(_blurOverlay); // above the veil layers
            }
            StopBlur();
            float target = Mathf.Clamp01(alpha);
            if (seconds <= 0f)
            {
                _blurOverlay.style.opacity = target;
                return;
            }
            float from = _blurOverlay.style.opacity.value;
            int ms = Mathf.Max(1, Mathf.RoundToInt(seconds * 1000f));
            _blurAnim = _blurOverlay.experimental.animation
                .Start(0f, 1f, ms, (_, t) => _blurOverlay.style.opacity = Mathf.Lerp(from, target, t))
                .Ease(Easing.InOutSine)
                .KeepAlive();
        }

        /// <summary>Clear the blur overlay.</summary>
        public void ClearBlur(float seconds)
        {
            if (_blurOverlay == null) return; // nothing to clear — don't mint a layer
            Blur(0f, seconds);
        }
    }
}
