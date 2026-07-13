using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Real gaussian blur of the world camera's frame (the `blur` op), driven
    /// by the LvnBlur shader. Lives on the CAMERA that renders the world
    /// canvas: Unity calls <see cref="OnRenderImage"/> with the finished frame
    /// (src) and we must fill the screen (dst) — the one hook in the built-in
    /// render pipeline where a frame can be post-processed. The UITK chrome
    /// panel draws AFTER the camera, so dialogue and choices stay sharp.
    ///
    /// Cost model: the frame is first downsampled 4× (blurring a quarter-res
    /// copy is 16× cheaper and a downsample IS already a slight blur), then
    /// ping-ponged through the separable gaussian a few times, then mixed with
    /// the sharp frame by strength. At strength 0 the component disables
    /// itself — zero cost while no `blur` op is active.
    /// </summary>
    public sealed class LvnBlurEffect : MonoBehaviour
    {
        private Material _mat;
        private bool _shaderMissing;

        // Self-contained tween (realtime, like the stage's other effects).
        private float _cur, _from, _to = -1f, _dur;
        private float _start = -1f;

        /// <summary>The effect on <paramref name="cam"/> (added on first use).</summary>
        public static LvnBlurEffect Ensure(Camera cam) =>
            cam.GetComponent<LvnBlurEffect>() ?? cam.gameObject.AddComponent<LvnBlurEffect>();

        /// <summary>Animate blur strength (0 = sharp … 1 = full) over
        /// <paramref name="seconds"/> (0 = instant).</summary>
        public void FadeTo(float target, float seconds)
        {
            target = Mathf.Clamp01(target);
            if (seconds <= 0f)
            {
                _cur = target;
                _start = -1f;
            }
            else
            {
                _from = _cur;
                _to = target;
                _dur = seconds;
                _start = Time.realtimeSinceStartup;
            }
            enabled = true; // wakes OnRenderImage; it re-disables once idle at 0
        }

        private void Advance()
        {
            if (_start < 0f) return;
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _start) / _dur);
            _cur = Mathf.Lerp(_from, _to, Mathf.SmoothStep(0f, 1f, t));
            if (t >= 1f) _start = -1f;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            Advance();

            if (_mat == null && !_shaderMissing)
            {
                var shader = Resources.Load<Shader>("LvnBlur");
                if (shader == null || !shader.isSupported) _shaderMissing = true;
                else _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (_shaderMissing || _cur <= 0.004f)
            {
                Graphics.Blit(src, dst); // pass the frame through untouched
                if (_start < 0f) enabled = false; // idle at zero — stop paying for the hook
                return;
            }

            // Quarter-res working copies; ping-pong H→V passes between them.
            int w = Mathf.Max(1, src.width / 4), h = Mathf.Max(1, src.height / 4);
            var a = RenderTexture.GetTemporary(w, h, 0, src.format);
            var b = RenderTexture.GetTemporary(w, h, 0, src.format);
            Graphics.Blit(src, a);

            int iterations = 1 + Mathf.RoundToInt(_cur * 2f);       // 1..3
            float radius = 0.75f + _cur * 1.75f;                    // texel spread per pass
            _mat.SetFloat("_Radius", radius);
            for (int i = 0; i < iterations; i++)
            {
                _mat.SetVector("_Dir", new Vector4(1f, 0f, 0f, 0f));
                Graphics.Blit(a, b, _mat, 0);
                _mat.SetVector("_Dir", new Vector4(0f, 1f, 0f, 0f));
                Graphics.Blit(b, a, _mat, 0);
            }

            _mat.SetTexture("_BlurTex", a);
            _mat.SetFloat("_Mix", Mathf.Clamp01(_cur * 1.25f)); // definite blur well before 1.0
            Graphics.Blit(src, dst, _mat, 1);

            RenderTexture.ReleaseTemporary(a);
            RenderTexture.ReleaseTemporary(b);
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }
    }
}
