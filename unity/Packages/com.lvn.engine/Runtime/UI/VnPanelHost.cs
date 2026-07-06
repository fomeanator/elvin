using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The ONE window: a bottom-docked frame wearing the game's dialogue skin
    /// (VnTheme panel colour/art/9-slice/radius — pixel-identical to the
    /// DialogueBox), hosting ANY content. It lives in the stage's own document
    /// on the dialogue layer, so a wardrobe/shop/minigame replaces the dialogue
    /// inside the SAME frame: the first <see cref="ShowAsync"/> slides the
    /// panel up, showing different content cross-fades it and morphs the
    /// frame's height — the frame itself never blinks. This is the native
    /// transition the ad-hoc overlay sheets (each its own copy of the skin in
    /// another UIDocument) could not give.
    /// </summary>
    public sealed class VnPanelHost : VisualElement
    {
        private readonly VisualElement _frame;
        private readonly float _padY;
        private readonly float _minH;
        private VisualElement _content;
        private int _gen; // a newer Show/Hide supersedes any in-flight transition

        /// <summary>Transition length in seconds; tests set 0 for instant.</summary>
        public float TransitionSeconds = 0.22f;

        public bool IsOpen { get; private set; }
        public VisualElement Content => _content;

        public VnPanelHost(VnTheme theme)
        {
            var t = theme ?? new VnTheme();
            _padY = t.PanelPaddingY;
            _minH = t.PanelMinHeight;

            // full-screen carrier, click-through everywhere except the frame
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            pickingMode = PickingMode.Ignore;
            style.display = DisplayStyle.None;

            _frame = new VisualElement { name = "vn-panel-host-frame" };
            _frame.style.position = Position.Absolute;
            _frame.style.left = t.EdgePadding;
            _frame.style.right = t.EdgePadding;
            _frame.style.bottom = t.BottomPadding;
            _frame.style.paddingLeft = t.PanelPaddingX;
            _frame.style.paddingRight = t.PanelPaddingX;
            _frame.style.paddingTop = t.PanelPaddingY;
            _frame.style.paddingBottom = t.PanelPaddingY;
            _frame.style.minHeight = t.PanelMinHeight;
            _frame.style.backgroundColor = t.PanelColor;
            _frame.style.borderTopLeftRadius = t.PanelCornerRadius;
            _frame.style.borderTopRightRadius = t.PanelCornerRadius;
            _frame.style.borderBottomLeftRadius = t.PanelCornerRadius;
            _frame.style.borderBottomRightRadius = t.PanelCornerRadius;
            UiStyle.ApplyBackground(_frame, t.PanelSprite, t.PanelSlice); // the dialogue's own art
            Add(_frame);
        }

        /// <summary>Show <paramref name="content"/> in the shared frame. Closed →
        /// slide the panel up around it; already open → cross-fade the content
        /// and morph the frame's height, the frame itself stays put.</summary>
        public async Task ShowAsync(VisualElement content)
        {
            if (content == null) return;
            int gen = ++_gen;

            if (!IsOpen)
            {
                IsOpen = true;
                _content = content;
                _frame.Clear();
                _frame.Add(content);
                style.display = DisplayStyle.Flex;
                if (TransitionSeconds <= 0f)
                {
                    _frame.style.opacity = 1f;
                    _frame.style.translate = new Translate(0f, 0f, 0f);
                    return;
                }
                await Animate(TransitionSeconds, gen, k =>
                {
                    _frame.style.opacity = k;
                    _frame.style.translate = new Translate(0f, (1f - k) * 28f, 0f);
                });
                return;
            }

            if (content == _content) return;

            // ── cross-fade inside the standing frame ──
            var old = _content;
            _content = content;
            float dur = TransitionSeconds;
            if (dur <= 0f)
            {
                _frame.Clear();
                _frame.Add(content);
                content.style.opacity = 1f;
                return;
            }

            if (old != null)
                await Animate(dur * 0.4f, gen, k => old.style.opacity = 1f - k);
            if (gen != _gen) return;

            // Pin the frame at its current height so the swap can't pop, lay the
            // new content out inside (clipped), then glide to its natural height.
            float fromH = _frame.resolvedStyle.height;
            _frame.style.height = fromH;
            _frame.style.overflow = Overflow.Hidden;
            _frame.Clear();
            content.style.opacity = 0f;
            _frame.Add(content);
            await Task.Yield(); // one layout pass for the new content
            if (gen != _gen) return;

            float wantH = content.resolvedStyle.height;
            float toH = float.IsNaN(wantH) ? fromH : Mathf.Max(wantH + _padY * 2f, _minH);
            if (!float.IsNaN(fromH) && Mathf.Abs(toH - fromH) > 1f)
                await Animate(dur * 0.6f, gen, k => _frame.style.height = Mathf.Lerp(fromH, toH, k));
            if (gen == _gen)
            {
                _frame.style.height = StyleKeyword.Auto;
                _frame.style.overflow = Overflow.Visible; // floating bits (balance pills) show again
                await Animate(dur * 0.4f, gen, k => content.style.opacity = k);
            }
        }

        /// <summary>Slide the panel away and release the content.</summary>
        public async Task HideAsync()
        {
            if (!IsOpen) return;
            int gen = ++_gen;
            if (TransitionSeconds > 0f)
                await Animate(TransitionSeconds, gen, k =>
                {
                    _frame.style.opacity = 1f - k;
                    _frame.style.translate = new Translate(0f, k * 28f, 0f);
                });
            if (gen != _gen) return;
            IsOpen = false;
            _frame.Clear();
            _content = null;
            style.display = DisplayStyle.None;
            _frame.style.opacity = 1f;
            _frame.style.translate = new Translate(0f, 0f, 0f);
            _frame.style.height = StyleKeyword.Auto;
            _frame.style.overflow = Overflow.Visible;
        }

        // Smoothstep driver off unscaled time; a newer generation aborts.
        private async Task Animate(float seconds, int gen, System.Action<float> apply)
        {
            if (seconds <= 0f) { apply(1f); return; }
            float t0 = Time.unscaledTime;
            while (true)
            {
                if (gen != _gen) return;
                float k = Mathf.Clamp01((Time.unscaledTime - t0) / seconds);
                k = k * k * (3f - 2f * k);
                apply(k);
                if (k >= 1f) return;
                try { await Task.Yield(); }
                catch (System.OperationCanceledException) { return; }
            }
        }
    }
}
