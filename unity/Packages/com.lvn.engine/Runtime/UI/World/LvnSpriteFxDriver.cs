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
    ///   sfx id=ghost ghost=0.8 rim=0.5 shake=0.1            // призрак
    ///   sfx id=golem petrify=1                              // окаменение
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

        private float _outline, _glow, _dissolve, _flash, _dark, _tintFx,
                      _ghost, _petrify, _hologram, _burn, _rim, _shake;   // текущие
        private float _tOutline, _tGlow, _tDissolve, _tFlash, _tDark, _tTintFx,
                      _tGhost, _tPetrify, _tHologram, _tBurn, _tRim, _tShake; // цели
        private float _speed;                                  // 1/dur; 0 = мгновенно
        private Color _outlineColor = new Color(1f, 0.84f, 0.42f, 1f);
        private Color _glowColor = new Color(1f, 0.9f, 0.6f, 1f);
        private Color _tintColor = new Color(0.55f, 0.8f, 1f, 1f);
        private Color _ghostColor = new Color(0.53f, 0.86f, 1f, 1f);
        private Color _hologramColor = new Color(0.2f, 0.95f, 1f, 1f);
        private Color _burnColor = new Color(1f, 0.34f, 0.035f, 1f);
        private Color _rimColor = new Color(1f, 0.82f, 0.36f, 1f);

        public static void Apply(GameObject actorGo, JObject cmd)
        {
            var d = actorGo.GetComponent<LvnSpriteFxDriver>() ?? actorGo.AddComponent<LvnSpriteFxDriver>();
            d.ApplyCmd(cmd);
        }

        private void ApplyCmd(JObject cmd)
        {
            if (cmd["off"] != null)
            {
                _tOutline = _tGlow = _tDissolve = _tFlash = _tDark = _tTintFx =
                    _tGhost = _tPetrify = _tHologram = _tBurn = _tRim = _tShake = 0f;
                _speed = F(cmd, "dur", 0f) > 0f ? 1f / (float)cmd["dur"] : 0f;
                if (_speed <= 0f)
                {
                    _outline = _glow = _dissolve = _flash = _dark = _tintFx =
                        _ghost = _petrify = _hologram = _burn = _rim = _shake = 0f;
                    Unskin();
                }
                enabled = true;
                return;
            }

            _tOutline  = F(cmd, "outline", _tOutline);
            _tGlow     = F(cmd, "glow", _tGlow);
            _tDissolve = F(cmd, "dissolve", _tDissolve);
            _tFlash    = F(cmd, "flash", _tFlash);
            _tDark     = F(cmd, "dark", _tDark);
            _tTintFx   = F(cmd, "tint", _tTintFx);
            _tGhost    = F(cmd, "ghost", _tGhost);
            _tPetrify  = F(cmd, "petrify", _tPetrify);
            _tHologram = F(cmd, "hologram", _tHologram);
            _tBurn     = F(cmd, "burn", _tBurn);
            _tRim      = F(cmd, "rim", _tRim);
            _tShake    = F(cmd, "shake", _tShake);
            var tc = (string)cmd["tint_color"];
            if (!string.IsNullOrEmpty(tc) && ColorUtility.TryParseHtmlString(tc, out var c3)) _tintColor = c3;
            var oc = (string)cmd["outline_color"];
            if (!string.IsNullOrEmpty(oc) && ColorUtility.TryParseHtmlString(oc, out var c1)) _outlineColor = c1;
            var gc = (string)cmd["glow_color"];
            if (!string.IsNullOrEmpty(gc) && ColorUtility.TryParseHtmlString(gc, out var c2)) _glowColor = c2;
            ParseColor(cmd, "ghost_color", ref _ghostColor);
            ParseColor(cmd, "hologram_color", ref _hologramColor);
            ParseColor(cmd, "burn_color", ref _burnColor);
            ParseColor(cmd, "rim_color", ref _rimColor);

            float dur = F(cmd, "dur", 0f);
            _speed = dur > 0f ? 1f / dur : 0f;
            if (_speed <= 0f)
            {
                _outline = _tOutline; _glow = _tGlow; _dissolve = _tDissolve;
                _flash = _tFlash; _dark = _tDark; _tintFx = _tTintFx;
                _ghost = _tGhost; _petrify = _tPetrify; _hologram = _tHologram;
                _burn = _tBurn; _rim = _tRim; _shake = _tShake;
            }

            Skin();
            enabled = true;
        }

        private static float F(JObject cmd, string key, float cur)
            => cmd[key] != null ? (float)cmd[key] : cur;

        private static void ParseColor(JObject cmd, string key, ref Color current)
        {
            var text = (string)cmd[key];
            if (!string.IsNullOrEmpty(text) && ColorUtility.TryParseHtmlString(text, out var parsed))
                current = parsed;
        }

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
                _ghost = Mathf.MoveTowards(_ghost, _tGhost, k);
                _petrify = Mathf.MoveTowards(_petrify, _tPetrify, k);
                _hologram = Mathf.MoveTowards(_hologram, _tHologram, k);
                _burn = Mathf.MoveTowards(_burn, _tBurn, k);
                _rim = Mathf.MoveTowards(_rim, _tRim, k);
                _shake = Mathf.MoveTowards(_shake, _tShake, k);
            }
            bool идёт = !Mathf.Approximately(_outline, _tOutline) || !Mathf.Approximately(_glow, _tGlow)
                        || !Mathf.Approximately(_dissolve, _tDissolve) || !Mathf.Approximately(_flash, _tFlash)
                        || !Mathf.Approximately(_dark, _tDark) || !Mathf.Approximately(_tintFx, _tTintFx)
                        || !Mathf.Approximately(_ghost, _tGhost) || !Mathf.Approximately(_petrify, _tPetrify)
                        || !Mathf.Approximately(_hologram, _tHologram) || !Mathf.Approximately(_burn, _tBurn)
                        || !Mathf.Approximately(_rim, _tRim) || !Mathf.Approximately(_shake, _tShake);
            if (_mat != null)
            {
                _mat.SetFloat("_Outline", _outline);
                _mat.SetFloat("_Glow", _glow);
                _mat.SetFloat("_Dissolve", _dissolve);
                _mat.SetFloat("_Flash", _flash);
                _mat.SetFloat("_Dark", _dark);
                _mat.SetFloat("_TintFx", _tintFx);
                _mat.SetFloat("_Ghost", _ghost);
                _mat.SetFloat("_Petrify", _petrify);
                _mat.SetFloat("_Hologram", _hologram);
                _mat.SetFloat("_Burn", _burn);
                _mat.SetFloat("_Rim", _rim);
                _mat.SetFloat("_Shake", _shake);
                _mat.SetColor("_OutlineColor", _outlineColor);
                _mat.SetColor("_GlowColor", _glowColor);
                _mat.SetColor("_TintFxColor", _tintColor);
                _mat.SetColor("_GhostColor", _ghostColor);
                _mat.SetColor("_HologramColor", _hologramColor);
                _mat.SetColor("_BurnColor", _burnColor);
                _mat.SetColor("_RimColor", _rimColor);
            }
            if (!идёт)
            {
                // дошли до целей; полностью нулевые — вернуть дефолтный материал
                if (_outline <= 0f && _glow <= 0f && _dissolve <= 0f && _flash <= 0f &&
                    _dark <= 0f && _tintFx <= 0f && _ghost <= 0f && _petrify <= 0f &&
                    _hologram <= 0f && _burn <= 0f && _rim <= 0f && _shake <= 0f &&
                    _skinned.Count > 0)
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
