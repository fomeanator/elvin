using UnityEngine;

namespace Lvn.Spine
{
    /// <summary>
    /// A tiny per-skeleton fader: eases a CanvasGroup alpha UP over a short
    /// window so Spine actors fade in instead of popping. Hiding is instant —
    /// no fade-out, by design.
    ///
    /// It also owns the GPU WARM PULSE. CanvasGroup.alpha = 0 makes the Canvas
    /// cull the children entirely — zero draw calls — so a "prebuilt hidden"
    /// skeleton never reached the GPU: the shader's pipeline-state compile and
    /// the texture's first VRAM upload both landed in the REVEAL frame (the
    /// first-show hitch), and SkeletonGraphic.MeshScale never settled, so the
    /// deferred fit couldn't land either. The fix is the industry's oldest
    /// trick: draw it for real a few frames at alpha 1/255 — imperceptible,
    /// but a genuine draw call — then hide. Repeat shows skip the pulse: the
    /// pipeline state and texture stay resident for the session.
    ///
    /// MUST live in its own file matching the class name: a MonoBehaviour Unity
    /// can't resolve to a MonoScript logs "referenced script (Unknown) missing"
    /// and never runs.
    /// </summary>
    internal sealed class LvnSpineFader : MonoBehaviour
    {
        private const float FadeSeconds = 0.14f;
        internal const float WarmAlpha = 1f / 255f;
        // Draw this many nearly-invisible frames before the first hide: 1–2 for
        // MeshScale to settle + the fit to land (the mesh is degenerate at the
        // pre-fit scale 0), then real-geometry frames for the pipeline compile
        // and texture upload.
        private const int WarmFrames = 5;

        private CanvasGroup _cg;
        private int _warmLeft = WarmFrames;
        private bool _shown;

        private void Awake()
        {
            _cg = GetComponent<CanvasGroup>();
            if (_cg == null) _cg = gameObject.AddComponent<CanvasGroup>();
        }

        public void Show(bool visible)
        {
            if (_cg == null) _cg = gameObject.GetComponent<CanvasGroup>();
            _shown = visible;
            if (!visible)
            {
                if (_warmLeft > 0)
                {
                    // Still warming: keep drawing the pulse; Update hides us
                    // when it completes.
                    if (!gameObject.activeSelf) gameObject.SetActive(true);
                    if (_cg != null) _cg.alpha = WarmAlpha;
                    enabled = true;
                    return;
                }
                if (_cg != null) _cg.alpha = 0f; // instant hide, no fade-out
                enabled = false;
                gameObject.SetActive(false);
                return;
            }
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            enabled = true; // resume the fade-in lerp
        }

        private void Update()
        {
            if (_cg == null) { enabled = false; return; }
            if (!_shown)
            {
                if (_warmLeft > 0) { _cg.alpha = WarmAlpha; _warmLeft--; return; }
                _cg.alpha = 0f;
                enabled = false;
                gameObject.SetActive(false);
                return;
            }
            _warmLeft = 0; // a real show renders everything the pulse would
            _cg.alpha = Mathf.MoveTowards(_cg.alpha, 1f, Time.unscaledDeltaTime / FadeSeconds);
            if (_cg.alpha >= 1f) enabled = false;
        }
    }
}
