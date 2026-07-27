using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.UI.World
{
    /// <summary>
    /// Спрайтовые эффекты одного актёра/объекта — op <c>sfx</c>:
    ///
    ///   sfx id=niharis outline=0.6 outline_color=#ffd76a   // обводка
    ///   sfx id=niharis glow=0.8 glow_color=#7ad0ff         // свечение
    ///   sfx id=fp_enemy dissolve=1 dur=1.5                 // растворить за 1.5с
    ///   sfx id=niharis off                                 // снять всё
    ///
    /// Поля липкие; dur плавно ведёт значения к цели (0 — мгновенно).
    /// Вешается на GameObject актёра канвас-сцены и заменяет материал всех
    /// его Image на Hidden/LvnSpriteFx; off возвращает материал по умолчанию.
    /// </summary>
    public sealed class LvnSpriteFxDriver : MonoBehaviour
    {
        private static Shader _shader;
        private Material _mat;
        private readonly List<Graphic> _skinned = new List<Graphic>();

        private float _outline, _glow, _dissolve, _flash, _dark, _tintFx;   // текущие
        private float _tOutline, _tGlow, _tDissolve, _tFlash, _tDark, _tTintFx; // цели
        private float _speed;                                  // 1/dur; 0 = мгновенно
        private Color _outlineColor = new Color(1f, 0.84f, 0.42f, 1f);
        private Color _glowColor = new Color(1f, 0.9f, 0.6f, 1f);
        private Color _tintColor = new Color(0.55f, 0.8f, 1f, 1f);

        public static void Apply(GameObject actorGo, JObject cmd)
        {
            var d = actorGo.GetComponent<LvnSpriteFxDriver>() ?? actorGo.AddComponent<LvnSpriteFxDriver>();
            d.ApplyCmd(cmd);
        }

        private void ApplyCmd(JObject cmd)
        {
            if (cmd["off"] != null)
            {
                _tOutline = _tGlow = _tDissolve = _tFlash = _tDark = _tTintFx = 0f;
                _speed = F(cmd, "dur", 0f) > 0f ? 1f / (float)cmd["dur"] : 0f;
                if (_speed <= 0f) { _outline = _glow = _dissolve = _flash = _dark = _tintFx = 0f; Unskin(); }
                enabled = true;
                return;
            }

            _tOutline  = F(cmd, "outline", _tOutline);
            _tGlow     = F(cmd, "glow", _tGlow);
            _tDissolve = F(cmd, "dissolve", _tDissolve);
            _tFlash    = F(cmd, "flash", _tFlash);
            _tDark     = F(cmd, "dark", _tDark);
            _tTintFx   = F(cmd, "tint", _tTintFx);
            var tc = (string)cmd["tint_color"];
            if (!string.IsNullOrEmpty(tc) && ColorUtility.TryParseHtmlString(tc, out var c3)) _tintColor = c3;
            var oc = (string)cmd["outline_color"];
            if (!string.IsNullOrEmpty(oc) && ColorUtility.TryParseHtmlString(oc, out var c1)) _outlineColor = c1;
            var gc = (string)cmd["glow_color"];
            if (!string.IsNullOrEmpty(gc) && ColorUtility.TryParseHtmlString(gc, out var c2)) _glowColor = c2;

            float dur = F(cmd, "dur", 0f);
            _speed = dur > 0f ? 1f / dur : 0f;
            if (_speed <= 0f) { _outline = _tOutline; _glow = _tGlow; _dissolve = _tDissolve; _flash = _tFlash; _dark = _tDark; _tintFx = _tTintFx; }

            Skin();
            enabled = true;
        }

        private static float F(JObject cmd, string key, float cur)
            => cmd[key] != null ? (float)cmd[key] : cur;

        // Подменить материал всем Image-слоям актёра (и запомнить, кому).
        private void Skin()
        {
            if (_shader == null) _shader = Resources.Load<Shader>("LvnSpriteFx");
            if (_shader == null || !_shader.isSupported)
            {
                Debug.LogWarning("[LvnSpriteFx] шейдер не найден/не поддержан — sfx выключен");
                return;
            }
            if (_mat == null) _mat = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            _skinned.Clear();
            foreach (var g in GetComponentsInChildren<Image>(true))
            {
                g.material = _mat;
                _skinned.Add(g);
            }
        }

        private void Unskin()
        {
            foreach (var g in _skinned) if (g != null) g.material = null;
            _skinned.Clear();
        }

        private void Update()
        {
            if (_speed > 0f)
            {
                float k = Time.unscaledDeltaTime * _speed;
                _outline = Mathf.MoveTowards(_outline, _tOutline, k);
                _glow = Mathf.MoveTowards(_glow, _tGlow, k);
                _dissolve = Mathf.MoveTowards(_dissolve, _tDissolve, k);
                _flash = Mathf.MoveTowards(_flash, _tFlash, k);
                _dark = Mathf.MoveTowards(_dark, _tDark, k);
                _tintFx = Mathf.MoveTowards(_tintFx, _tTintFx, k);
            }
            bool идёт = !Mathf.Approximately(_outline, _tOutline) || !Mathf.Approximately(_glow, _tGlow)
                        || !Mathf.Approximately(_dissolve, _tDissolve) || !Mathf.Approximately(_flash, _tFlash)
                        || !Mathf.Approximately(_dark, _tDark) || !Mathf.Approximately(_tintFx, _tTintFx);
            if (_mat != null)
            {
                _mat.SetFloat("_Outline", _outline);
                _mat.SetFloat("_Glow", _glow);
                _mat.SetFloat("_Dissolve", _dissolve);
                _mat.SetFloat("_Flash", _flash);
                _mat.SetFloat("_Dark", _dark);
                _mat.SetFloat("_TintFx", _tintFx);
                _mat.SetColor("_OutlineColor", _outlineColor);
                _mat.SetColor("_GlowColor", _glowColor);
                _mat.SetColor("_TintFxColor", _tintColor);
            }
            if (!идёт)
            {
                // дошли до целей; полностью нулевые — вернуть дефолтный материал
                if (_outline <= 0f && _glow <= 0f && _dissolve <= 0f && _flash <= 0f && _dark <= 0f && _tintFx <= 0f && _skinned.Count > 0)
                    Unskin();
                enabled = _skinned.Count > 0; // остаёмся живыми, пока скин надет
            }
        }

        private void OnDestroy()
        {
            Unskin();
            if (_mat != null) Destroy(_mat);
        }
    }
}
