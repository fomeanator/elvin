using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
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
    ///   sfx id=hero aura=0.9 aura_style=guard                // защитная аура
    ///   sfx id=hero aura=0.9 aura_style=fire aura_color=#ff6a20 aura_color2=#ffd46a
    ///   sfx id=hero aura=1 aura_style=distortion             // чёрно-красный разлом
    ///   sfx id=hero part=weapon blade=1 blade_color=#c9f5ff // аура только меча
    ///   sfx id=niharis off                                 // снять всё
    ///
    /// Поля липкие; dur плавно ведёт значения к цели (0 — мгновенно).
    /// Вешается на GameObject актёра канвас-сцены и заменяет материал всех
    /// его Image на Hidden/LvnSpriteFx; off возвращает материал по умолчанию.
    /// </summary>
    public sealed class LvnSpriteFxDriver : MonoBehaviour
    {
        private static Shader _shader;
        private static int _nextStencilRef = 1;
        private Material _mat;
        private Material _haloMat;
        private readonly List<Graphic> _skinned = new List<Graphic>();
        private readonly List<HaloLayer> _haloLayers = new List<HaloLayer>();
        private bool _compositeHalo;
        private int _stencilRef;

        private sealed class HaloLayer
        {
            public Image Source;
            public Image Halo;
        }

        private float _outline, _glow, _dissolve, _flash, _dark, _tintFx,
                      _ghost, _petrify, _hologram, _burn, _rim, _shake,
                      _aura, _blade, _lightning, _runes;   // текущие
        private float _tOutline, _tGlow, _tDissolve, _tFlash, _tDark, _tTintFx,
                      _tGhost, _tPetrify, _tHologram, _tBurn, _tRim, _tShake,
                      _tAura, _tBlade, _tLightning, _tRunes; // цели
        private float _speed;                                  // 1/dur; 0 = мгновенно
        private bool _scopedPart;
        private float _auraStyle;
        private Color _outlineColor = new Color(1f, 0.84f, 0.42f, 1f);
        private Color _glowColor = new Color(1f, 0.9f, 0.6f, 1f);
        private Color _tintColor = new Color(0.55f, 0.8f, 1f, 1f);
        private Color _ghostColor = new Color(0.53f, 0.86f, 1f, 1f);
        private Color _hologramColor = new Color(0.2f, 0.95f, 1f, 1f);
        private Color _burnColor = new Color(1f, 0.34f, 0.035f, 1f);
        private Color _rimColor = new Color(1f, 0.82f, 0.36f, 1f);
        private Color _auraColor = new Color(0.36f, 0.82f, 1f, 1f);
        private Color _auraColor2 = new Color(0.67f, 0.35f, 1f, 1f);
        private Color _bladeColor = new Color(0.66f, 0.92f, 1f, 1f);
        private Color _lightningColor = new Color(0.78f, 0.91f, 1f, 1f);
        private Color _runesColor = new Color(1f, 0.78f, 0.31f, 1f);

        public static void Apply(GameObject actorGo, JObject cmd)
        {
            if (actorGo == null || cmd == null) return;
            var part = (string)cmd["part"];

            // `off` без part остаётся привычным полным сбросом и заодно снимает
            // независимые материалы с body/weapon, если автор их комбинировал.
            if (cmd["off"] != null && string.IsNullOrEmpty(part))
            {
                var all = actorGo.GetComponentsInChildren<LvnSpriteFxDriver>(true);
                if (all.Length == 0)
                    actorGo.AddComponent<LvnSpriteFxDriver>().ApplyCmd(cmd);
                else
                    foreach (var driver in all) driver.ApplyCmd(cmd);
                return;
            }

            var target = actorGo;
            if (!string.IsNullOrEmpty(part))
            {
                target = FindPart(actorGo, part);
                if (target == null)
                {
                    Debug.LogWarning($"[LvnSpriteFx] у '{actorGo.name}' нет слоя part={part}");
                    return;
                }
            }

            var d = target.GetComponent<LvnSpriteFxDriver>() ?? target.AddComponent<LvnSpriteFxDriver>();
            d._scopedPart = target != actorGo;
            d.ApplyCmd(cmd);
        }

        private static GameObject FindPart(GameObject actorGo, string part)
        {
            var wanted = "layer:" + part;
            foreach (var tr in actorGo.GetComponentsInChildren<Transform>(true))
                if (tr.name == wanted || tr.name == part)
                    return tr.gameObject;
            return null;
        }

        private void ApplyCmd(JObject cmd)
        {
            if (cmd["off"] != null)
            {
                _tOutline = _tGlow = _tDissolve = _tFlash = _tDark = _tTintFx =
                    _tGhost = _tPetrify = _tHologram = _tBurn = _tRim = _tShake =
                    _tAura = _tBlade = _tLightning = _tRunes = 0f;
                _speed = F(cmd, "dur", 0f) > 0f ? 1f / (float)cmd["dur"] : 0f;
                if (_speed <= 0f)
                {
                    _outline = _glow = _dissolve = _flash = _dark = _tintFx =
                        _ghost = _petrify = _hologram = _burn = _rim = _shake =
                        _aura = _blade = _lightning = _runes = 0f;
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
            _tAura     = F(cmd, "aura", _tAura);
            _tBlade    = F(cmd, "blade", _tBlade);
            _tLightning = F(cmd, "lightning", _tLightning);
            _tRunes    = F(cmd, "runes", _tRunes);
            ParseAuraStyle(cmd);
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
            ParseColor(cmd, "aura_color", ref _auraColor);
            ParseColor(cmd, "aura_color2", ref _auraColor2);
            ParseColor(cmd, "blade_color", ref _bladeColor);
            ParseColor(cmd, "lightning_color", ref _lightningColor);
            ParseColor(cmd, "runes_color", ref _runesColor);

            float dur = F(cmd, "dur", 0f);
            _speed = dur > 0f ? 1f / dur : 0f;
            if (_speed <= 0f)
            {
                _outline = _tOutline; _glow = _tGlow; _dissolve = _tDissolve;
                _flash = _tFlash; _dark = _tDark; _tintFx = _tTintFx;
                _ghost = _tGhost; _petrify = _tPetrify; _hologram = _tHologram;
                _burn = _tBurn; _rim = _tRim; _shake = _tShake;
                _aura = _tAura; _blade = _tBlade;
                _lightning = _tLightning; _runes = _tRunes;
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

        private void ParseAuraStyle(JObject cmd)
        {
            var style = ((string)cmd["aura_style"])?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(style)) return;

            Color primary;
            Color secondary;
            switch (style)
            {
                case "guard":
                case "ward":
                case "protection":
                    _auraStyle = 1f;
                    primary = Html("#79cfff");
                    secondary = Html("#e8fbff");
                    break;
                case "fire":
                    _auraStyle = 2f;
                    primary = Html("#ff5b20");
                    secondary = Html("#ffd45e");
                    break;
                case "frost":
                case "ice":
                    _auraStyle = 3f;
                    primary = Html("#69ddff");
                    secondary = Html("#e5fbff");
                    break;
                case "storm":
                case "thunder":
                    _auraStyle = 4f;
                    primary = Html("#6f83ff");
                    secondary = Html("#d5f6ff");
                    break;
                case "shadow":
                case "dark":
                    _auraStyle = 5f;
                    primary = Html("#7545bd");
                    secondary = Html("#25183f");
                    break;
                case "holy":
                case "light":
                    _auraStyle = 6f;
                    primary = Html("#ffd15c");
                    secondary = Html("#fff5cb");
                    break;
                case "space":
                case "void":
                    _auraStyle = 7f;
                    primary = Html("#05010a");
                    secondary = Html("#9348ff");
                    break;
                case "distortion":
                case "rift":
                    _auraStyle = 8f;
                    primary = Html("#030000");
                    secondary = Html("#ff2118");
                    break;
                case "basic":
                case "neutral":
                case "plain":
                    _auraStyle = 0f;
                    primary = Html("#d6deea");
                    secondary = Html("#ffffff");
                    break;
                default:
                    Debug.LogWarning($"[LvnSpriteFx] неизвестный aura_style={style}; использую basic");
                    _auraStyle = 0f;
                    primary = Html("#d6deea");
                    secondary = Html("#ffffff");
                    break;
            }

            // Палитра стиля — только удобный пресет. Явные цвета команды,
            // разобранные следом, всегда имеют приоритет.
            if (cmd["aura_color"] == null) _auraColor = primary;
            if (cmd["aura_color2"] == null) _auraColor2 = secondary;
        }

        private static Color Html(string value)
        {
            ColorUtility.TryParseHtmlString(value, out var parsed);
            return parsed;
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
            ClearCompositeHalo();
            _skinned.Clear();
            var images = new List<Image>();
            foreach (var g in GetComponentsInChildren<Image>(true))
            {
                if (g.name.StartsWith("__lvn-composite-halo:")) continue;
                var owner = g.GetComponentInParent<LvnSpriteFxDriver>();
                if (owner != null && owner != this) continue;
                images.Add(g);
            }

            _compositeHalo = !_scopedPart && images.Count > 1
                && (_tAura > 0f || _tOutline > 0f || _tGlow > 0f);
            if (_compositeHalo)
            {
                if (_stencilRef == 0)
                {
                    _stencilRef = _nextStencilRef++;
                    if (_nextStencilRef > 254) _nextStencilRef = 1;
                }
                _haloMat = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            foreach (var g in images)
            {
                g.material = _mat;
                _skinned.Add(g);
                if (_compositeHalo) AddCompositeHalo(g);
            }
            SyncCompositeHaloGeometry();
            SyncMaterial(_mat, haloOnly: false);
            if (_haloMat != null) SyncMaterial(_haloMat, haloOnly: true);
        }

        private void Unskin()
        {
            foreach (var g in _skinned) if (g != null) g.material = null;
            _skinned.Clear();
            ClearCompositeHalo();
            _compositeHalo = false;
        }

        private void AddCompositeHalo(Image source)
        {
            var go = new GameObject("__lvn-composite-halo:" + source.name,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var halo = go.GetComponent<Image>();
            halo.raycastTarget = false;
            halo.material = _haloMat;
            halo.maskable = source.maskable;
            halo.preserveAspect = source.preserveAspect;
            halo.type = source.type;
            halo.fillCenter = source.fillCenter;
            halo.fillMethod = source.fillMethod;
            halo.fillAmount = source.fillAmount;
            halo.fillClockwise = source.fillClockwise;
            halo.fillOrigin = source.fillOrigin;
            halo.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
            go.transform.SetParent(source.transform.parent, false);
            go.transform.SetAsLastSibling();
            _haloLayers.Add(new HaloLayer { Source = source, Halo = halo });
        }

        private void SyncCompositeHaloGeometry()
        {
            foreach (var layer in _haloLayers)
            {
                if (layer.Source == null || layer.Halo == null) continue;
                var source = layer.Source;
                var halo = layer.Halo;
                var src = (RectTransform)source.transform;
                var dst = (RectTransform)halo.transform;
                dst.anchorMin = src.anchorMin;
                dst.anchorMax = src.anchorMax;
                dst.anchoredPosition = src.anchoredPosition;
                dst.sizeDelta = src.sizeDelta;
                dst.pivot = src.pivot;
                dst.localRotation = src.localRotation;
                dst.localScale = src.localScale;
                halo.sprite = source.sprite;
                halo.overrideSprite = source.overrideSprite;
                halo.color = source.color;
                halo.enabled = source.enabled;
            }
        }

        private void ClearCompositeHalo()
        {
            foreach (var layer in _haloLayers)
            {
                if (layer.Halo == null) continue;
                var go = layer.Halo.gameObject;
                go.SetActive(false);
                if (Application.isPlaying) Destroy(go);
                else DestroyImmediate(go);
            }
            _haloLayers.Clear();
            if (_haloMat != null)
            {
                if (Application.isPlaying) Destroy(_haloMat);
                else DestroyImmediate(_haloMat);
                _haloMat = null;
            }
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
                _aura = Mathf.MoveTowards(_aura, _tAura, k);
                _blade = Mathf.MoveTowards(_blade, _tBlade, k);
                _lightning = Mathf.MoveTowards(_lightning, _tLightning, k);
                _runes = Mathf.MoveTowards(_runes, _tRunes, k);
            }
            bool идёт = !Mathf.Approximately(_outline, _tOutline) || !Mathf.Approximately(_glow, _tGlow)
                        || !Mathf.Approximately(_dissolve, _tDissolve) || !Mathf.Approximately(_flash, _tFlash)
                        || !Mathf.Approximately(_dark, _tDark) || !Mathf.Approximately(_tintFx, _tTintFx)
                        || !Mathf.Approximately(_ghost, _tGhost) || !Mathf.Approximately(_petrify, _tPetrify)
                        || !Mathf.Approximately(_hologram, _tHologram) || !Mathf.Approximately(_burn, _tBurn)
                        || !Mathf.Approximately(_rim, _tRim) || !Mathf.Approximately(_shake, _tShake)
                        || !Mathf.Approximately(_aura, _tAura) || !Mathf.Approximately(_blade, _tBlade)
                        || !Mathf.Approximately(_lightning, _tLightning) || !Mathf.Approximately(_runes, _tRunes);
            if (_mat != null) SyncMaterial(_mat, haloOnly: false);
            if (_haloMat != null) SyncMaterial(_haloMat, haloOnly: true);
            if (!идёт)
            {
                // дошли до целей; полностью нулевые — вернуть дефолтный материал
                if (_outline <= 0f && _glow <= 0f && _dissolve <= 0f && _flash <= 0f &&
                    _dark <= 0f && _tintFx <= 0f && _ghost <= 0f && _petrify <= 0f &&
                    _hologram <= 0f && _burn <= 0f && _rim <= 0f && _shake <= 0f &&
                    _aura <= 0f && _blade <= 0f && _lightning <= 0f && _runes <= 0f &&
                    _skinned.Count > 0)
                    Unskin();
                enabled = _skinned.Count > 0; // остаёмся живыми, пока скин надет
            }
        }

        private void LateUpdate()
        {
            if (_compositeHalo) SyncCompositeHaloGeometry();
        }

        private void SyncMaterial(Material mat, bool haloOnly)
        {
            bool splitExterior = _compositeHalo;
            mat.SetFloat("_Outline", haloOnly ? _outline : splitExterior ? 0f : _outline);
            mat.SetFloat("_Glow", haloOnly ? _glow : splitExterior ? 0f : _glow);
            mat.SetFloat("_Dissolve", haloOnly ? 0f : _dissolve);
            mat.SetFloat("_Flash", haloOnly ? 0f : _flash);
            mat.SetFloat("_Dark", haloOnly ? 0f : _dark);
            mat.SetFloat("_TintFx", haloOnly ? 0f : _tintFx);
            mat.SetFloat("_Ghost", haloOnly ? 0f : _ghost);
            mat.SetFloat("_Petrify", haloOnly ? 0f : _petrify);
            mat.SetFloat("_Hologram", haloOnly ? 0f : _hologram);
            mat.SetFloat("_Burn", haloOnly ? 0f : _burn);
            mat.SetFloat("_Rim", haloOnly ? 0f : _rim);
            mat.SetFloat("_Shake", _shake);
            mat.SetFloat("_Aura", haloOnly ? _aura : splitExterior ? 0f : _aura);
            mat.SetFloat("_Blade", haloOnly ? 0f : _blade);
            mat.SetFloat("_Lightning", haloOnly ? 0f : _lightning);
            mat.SetFloat("_Runes", haloOnly ? 0f : _runes);
            mat.SetFloat("_ScopedPart", _scopedPart ? 1f : 0f);
            mat.SetFloat("_AuraStyle", _auraStyle);
            mat.SetFloat("_CompositeSource", splitExterior && !haloOnly ? 1f : 0f);
            mat.SetFloat("_CompositeOnly", haloOnly ? 1f : 0f);
            mat.SetFloat("_StencilRef", _stencilRef);
            mat.SetFloat("_StencilComp", haloOnly
                ? (float)CompareFunction.NotEqual
                : (float)CompareFunction.Always);
            mat.SetFloat("_StencilOp", splitExterior && !haloOnly
                ? (float)StencilOp.Replace
                : (float)StencilOp.Keep);
            mat.SetFloat("_StencilWriteMask", splitExterior && !haloOnly ? 255f : 0f);
            mat.SetColor("_OutlineColor", _outlineColor);
            mat.SetColor("_GlowColor", _glowColor);
            mat.SetColor("_TintFxColor", _tintColor);
            mat.SetColor("_GhostColor", _ghostColor);
            mat.SetColor("_HologramColor", _hologramColor);
            mat.SetColor("_BurnColor", _burnColor);
            mat.SetColor("_RimColor", _rimColor);
            mat.SetColor("_AuraColor", _auraColor);
            mat.SetColor("_AuraColor2", _auraColor2);
            mat.SetColor("_BladeColor", _bladeColor);
            mat.SetColor("_LightningColor", _lightningColor);
            mat.SetColor("_RunesColor", _runesColor);
        }

        private void OnDestroy()
        {
            Unskin();
            if (_mat != null)
            {
                if (Application.isPlaying) Destroy(_mat);
                else DestroyImmediate(_mat);
            }
        }
    }
}
