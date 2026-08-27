using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ЭФФЕКТЫ КАДРА — вуаль, затемнение, вспышка, тон, размытие, темп текста
    /// и камера.
    ///
    /// <para>Всё это опы, которые не трогают состав кадра: в нём не появляется
    /// и не исчезает ни один человек — меняется то, КАК кадр показан. Отдельный
    /// дом ровно по этой границе: состав ведёт Партитура, вид — эти семь.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        // ── stage command helpers ─────────────────────────────────────────────

        private void ApplyFade(JObject cmd)
        {
            var to = (string)cmd["to"] ?? "black";
            float dur = NumOr(cmd["duration"], 0.5f);
            if (to == "clear" || to == "none") _fx.Clear(dur);
            else _fx.Fade(to == "white" ? Color.white : Color.black, dur);
        }

        private void ApplyDim(JObject cmd)
        {
            float alpha = NumOr(cmd["alpha"], 0.4f);
            float dur = NumOr(cmd["duration"], 0.5f);
            _fx.Dim(alpha, dur);
        }

        private void ApplyFlash(JObject cmd)
        {
            if (LvnPrefs.ReduceMotion) return; // vestibular/photosensitivity comfort
            var colour = ParseColor((string)cmd["color"], Color.white);
            float dur = NumOr(cmd["duration"], 0.2f);
            _fx.Flash(colour, dur);
        }

        private void ApplyTint(JObject cmd)
        {
            var colour = ParseColor((string)cmd["color"], Color.white);
            float alpha = NumOr(cmd["alpha"], 0.3f);
            float dur = NumOr(cmd["duration"], 0.5f);
            _fx.Tint(colour, alpha, dur);
        }

        private void ApplyBlur(JObject cmd)
        {
            float alpha = NumOr(cmd["alpha"], 0.5f);
            float dur = NumOr(cmd["duration"], 0.5f);
            // Real gaussian of the scene frame when the renderer can (canvas
            // path + built-in pipeline); the FxLayer white veil is the fallback
            // for platforms without a camera hook. Never both.
            if (_renderer != null && _renderer.TryBlur(Mathf.Clamp01(alpha), dur))
            {
                _fx.ClearBlur(0f);
                return;
            }
            if (alpha <= 0f) _fx.ClearBlur(dur);
            else _fx.Blur(alpha, dur);
        }

        private void ApplyTextPace(JObject cmd)
        {
            float cps = NumOr(cmd["cps"], 0f);
            TypewriterClock.GlobalCps = cps;
        }


        private void ApplyCamera(JObject cmd)
        {
            float dur = NumOr(cmd["duration"], 0.3f);
            switch ((string)cmd["action"])
            {
                case "shake":
                {
                    if (LvnPrefs.ReduceMotion) break; // comfort setting: no screen shake
                    float amp = NumOr(cmd["amplitude"], 8f);
                    _renderer?.Shake(amp, dur);
                    break;
                }
                case "zoom":
                {
                    float factor = NumOr(cmd["factor"], 1.2f);
                    _renderer?.Zoom(factor, dur);
                    break;
                }
                case "pan":
                {
                    float px = NumOr(cmd["x"], 0f);
                    float py = NumOr(cmd["y"], 0f);
                    _renderer?.Pan(px, py, dur);
                    break;
                }
                case "reset":
                    _renderer?.ResetCamera(dur);
                    break;
            }
        }
    }
}
