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
    ///   sfx id=hero aura=1 aura_style=spirit                 // двухслойная шумовая оболочка
    ///   sfx id=hero aura=1 aura_style=ascendant aura_size=1.3 aura_speed=0.8 aura_density=1.5
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
        private Material _maskMat;
        private Material _haloMat;
        private readonly List<Graphic> _skinned = new List<Graphic>();
        private readonly List<HaloLayer> _maskLayers = new List<HaloLayer>();
        private readonly List<HaloLayer> _haloLayers = new List<HaloLayer>();
        private bool _compositeHalo;
        private int _stencilRef;

        private sealed class HaloLayer
        {
            public Image Source;
            public Image Halo;
        }

        // ГАШЕНИЕ ЦЕЛИКОМ: 1 — виден, 0 — исчез. Отдельно от прочих эффектов и
        // с обратным нулём: у остальных 0 значит «выключено», у этого — «пусто».
        private float _fade = 1f, _tFade = 1f;
        // All nested part drivers must sample the SAME actor-sized screen ramp.
        // Using each part's own RectTransform makes a shirt, body and hair cross
        // the fade front at different moments — the classic wardrobe x-ray.
        private RectTransform _fadeBoundsRoot;
        // Направление волны проявления (−1 слева, +1 справа, 0 — ближайший край).
        // Ставится переходом drift, к полям опа sfx не относится.
        private float _fadeDir;
        private float _outline, _glow, _dissolve, _flash, _dark, _tintFx,
                      _ghost, _petrify, _hologram, _burn, _rim, _shake,
                      _aura, _blade, _lightning, _runes;   // текущие
        private float _tOutline, _tGlow, _tDissolve, _tFlash, _tDark, _tTintFx,
                      _tGhost, _tPetrify, _tHologram, _tBurn, _tRim, _tShake,
                      _tAura, _tBlade, _tLightning, _tRunes; // цели
        private float _speed;                                  // 1/dur; 0 = мгновенно
        private bool _scopedPart;
        private float _auraStyle;
        private float _auraSize = 1f;
        private float _auraSpeed = 1f;
        private float _auraDensity = 1f;
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

            // «actor …» и «sfx …» идут в сценарии подряд, и переход стартует
            // на команду РАНЬШЕ эффекта: гард в BeginTransitionVisual ещё не
            // видит sfx и честно уходит в композит, который рисует сырые слои.
            // Эффект, применённый сейчас, одел спрятанный риг — вернём его на
            // сцену, прокси в отставку; фейд перехода продолжается группой.
            if (d.AnyAuthoredFxActive())
            {
                var worldActor = actorGo.GetComponentInParent<WorldActor>()
                                 ?? actorGo.GetComponent<WorldActor>();
                worldActor?.DropCompositeForFx();
            }
        }

        /// <summary>
        /// Гашение героя целиком: 1 — виден, 0 — исчез. Отдельный вход, потому
        /// что это не эффект сцены, а служебная величина перехода: у неё
        /// обратный ноль (у остальных 0 значит «выключено»), и ведёт её движок,
        /// а не автор. Значение 1 снимает материал, если больше ничего не
        /// включено, — исчезнувшая надобность не должна оставлять за собой
        /// лишний материал на каждом слое.
        /// </summary>
        /// <summary>Направление чистой боковой маски проявления (см. drift).
        /// Нулевое выбирает ближайший край автоматически.</summary>
        public static void SetFadeDir(GameObject actorGo, float dir)
        {
            if (actorGo == null) return;
            var root = actorGo.GetComponent<LvnSpriteFxDriver>();
            if (root == null && dir != 0f)
                root = actorGo.AddComponent<LvnSpriteFxDriver>();
            var bounds = actorGo.transform as RectTransform;
            foreach (var d in actorGo.GetComponentsInChildren<LvnSpriteFxDriver>(true))
            {
                d._fadeDir = dir;
                d._fadeBoundsRoot = bounds;
            }
        }

        public static void SetFade(GameObject actorGo, float k)
        {
            if (actorGo == null) return;
            var root = actorGo.GetComponent<LvnSpriteFxDriver>();
            if (root == null)
            {
                if (k >= 0.999f) return;   // нечего гасить — и незачем заводить драйвер
                root = actorGo.AddComponent<LvnSpriteFxDriver>();
            }
            var bounds = actorGo.transform as RectTransform;
            float dir = root._fadeDir;
            // A part-scoped sfx owns its Image material, so the root driver
            // deliberately skips that subtree. Propagate the service fade to
            // every such owner instead of leaving clothes/body on different paths.
            foreach (var d in actorGo.GetComponentsInChildren<LvnSpriteFxDriver>(true))
            {
                d._fadeBoundsRoot = bounds;
                d._fadeDir = dir;
                d.ApplyCmd(new JObject { ["fade"] = k, ["dur"] = 0f });
            }
        }

        /// <summary>Finish the service fade and immediately return transition-
        /// only images to UI/Default. Explicit story sfx remain skinned and keep
        /// their own driver; an idle actor pays no per-frame transition cost.</summary>
        public static void ReleaseFade(GameObject actorGo)
        {
            if (actorGo == null) return;
            foreach (var d in actorGo.GetComponentsInChildren<LvnSpriteFxDriver>(true))
            {
                d.ApplyCmd(new JObject { ["fade"] = 1f, ["dur"] = 0f });
                d.Update();
            }
        }

        /// <summary>Параметры волны для шейдера: левый край и ширина героя в
        /// ЭКРАННЫХ пикселях (SV_POSITION фрагмента — они же), вес подмеса и
        /// направление. Считается заново на каждом кадре перехода: герой может
        /// ехать (снос drift двигает слот прямо во время гашения).</summary>
        private Vector4 FadeRamp()
        {
            if (_fade >= 0.999f) return Vector4.zero;
            var rt = _fadeBoundsRoot != null ? _fadeBoundsRoot : transform as RectTransform;
            if (rt == null) return Vector4.zero;
            var canvas = GetComponentInParent<Canvas>();
            var cam = canvas != null ? canvas.worldCamera : null;
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            float x0 = float.MaxValue, x1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                float sx = cam != null ? cam.WorldToScreenPoint(c[i]).x : c[i].x;
                if (sx < x0) x0 = sx;
                if (sx > x1) x1 = sx;
            }
            if (x1 - x0 < 2f) return Vector4.zero;
            float dir = _fadeDir;
            if (dir == 0f)
                dir = (x0 + x1) * 0.5f >= Screen.width * 0.5f ? 1f : -1f;
            return new Vector4(x0, 1f / (x1 - x0), 1f, dir);
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
                _tFade = 1f;
                _speed = F(cmd, "dur", 0f) > 0f ? 1f / F(cmd, "dur", 1f) : 0f;
                if (_speed <= 0f)
                {
                    _outline = _glow = _dissolve = _flash = _dark = _tintFx =
                        _ghost = _petrify = _hologram = _burn = _rim = _shake =
                        _aura = _blade = _lightning = _runes = 0f;
                    _fade = 1f;
                    Unskin();
                }
                enabled = true;
                return;
            }

            _tFade     = F(cmd, "fade", _tFade);
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
            _auraSize = ClampedF(cmd, "aura_size", _auraSize, 0.4f, 2.5f);
            _auraSpeed = ClampedF(cmd, "aura_speed", _auraSpeed, 0f, 4f);
            _auraDensity = ClampedF(cmd, "aura_density", _auraDensity, 0.25f, 4f);
            _tBlade    = F(cmd, "blade", _tBlade);
            _tLightning = F(cmd, "lightning", _tLightning);
            _tRunes    = F(cmd, "runes", _tRunes);
            ParseAuraStyle(cmd);
            ParseColor(cmd, "tint_color", ref _tintColor);
            ParseColor(cmd, "outline_color", ref _outlineColor);
            ParseColor(cmd, "glow_color", ref _glowColor);
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
                _fade = _tFade;
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
            => LvnNum.Parse(cmd[key], cur);

        private static float ClampedF(JObject cmd, string key, float current,
            float min, float max)
            => cmd[key] != null
                ? Mathf.Clamp(LvnNum.Parse(cmd[key], current), min, max)
                : current;

        // Разбор цвета — из общего дома (UiColor). Своя копия в каждом слое и
        // была тем, из-за чего одно понятие расходилось по движку.
        private static void ParseColor(JObject cmd, string key, ref Color current)
            => current = UiColor.FromCmd(cmd, key, current);

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
                case "spirit":
                case "soul":
                case "aether":
                    _auraStyle = 9f;
                    primary = Html("#3657ff");
                    secondary = Html("#d8fbff");
                    break;
                case "ascendant":
                case "monarch":
                case "overlord":
                    _auraStyle = 10f;
                    primary = Html("#1748ff");
                    secondary = Html("#d8f7ff");
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

        private static Color Html(string value) => UiColor.Parse(value, Color.white);

        /// <summary>Слои актёра пересобрали ПОСЛЕ применения sfx (ленивая
        /// догрузка слоя, смена облика): материал эффекта был надет только на
        /// прежние Image, а свежесозданные рисуются дефолтным — на тёмном
        /// силуэте это буквально «вспышка» светлого слоя (живой случай: у
        /// героя-голограммы доехал слой лица, и он посветлел посреди сцены).
        /// Зовёт пересборщик слоёв (<see cref="WorldActor.Configure"/>).</summary>
        public static void Reskin(GameObject actorGo)
        {
            if (actorGo == null) return;
            foreach (var d in actorGo.GetComponentsInChildren<LvnSpriteFxDriver>(true))
                if (d.AnyFxActive())
                {
                    d.Skin();
                    d.enabled = true; // мог сам себя выключить, когда снимал скин
                    LvnLog.Trace($"[lvn-sfx] {actorGo.name}: слои пересобраны — эффект надет заново ({d._skinned.Count} слоёв)");
                }
        }

        private bool AnyFxActive() => _tFade < 0.999f || AnyAuthoredFxActive();

        // Авторские эффекты — без служебного фейда переходов: тот живёт на
        // каждом входе/уходе и не означает, что актёр «что-то носит».
        private bool AnyAuthoredFxActive()
            => _tOutline > 0.001f || _tGlow > 0.001f || _tDissolve > 0.001f
               || _tFlash > 0.001f || _tDark > 0.001f || _tTintFx > 0.001f
               || _tGhost > 0.001f || _tPetrify > 0.001f || _tHologram > 0.001f
               || _tBurn > 0.001f || _tRim > 0.001f || _tShake > 0.001f
               || _tAura > 0.001f || _tBlade > 0.001f || _tLightning > 0.001f
               || _tRunes > 0.001f;

        /// <summary>Носит ли актёр сейчас авторский спрайтовый эффект. Прокси-
        /// композит переходов рисует СЫРЫЕ текстуры слоёв — надетый sfx (тёмный
        /// силуэт, голограмма, аура) он воспроизвести не может, и актёр с
        /// эффектом обязан играть переходы живыми слоями, иначе он
        /// «раздевается» до чистого арта на время каждого фейда.</summary>
        public static bool WearsAuthoredFx(GameObject actorGo)
        {
            if (actorGo == null) return false;
            foreach (var d in actorGo.GetComponentsInChildren<LvnSpriteFxDriver>(true))
                if (d.AnyAuthoredFxActive()) return true;
            return false;
        }

        // Подменить материал всем Image-слоям актёра (и запомнить, кому).
        private void Skin()
        {
            // Про пропажу говорит LvnShaders — одинаково для всех эффектов и
            // один раз, а не своим текстом в каждом файле.
            if (_shader == null) _shader = LvnShaders.Load("LvnSpriteFx");
            if (_shader == null) return;   // грим не сыграет, сцена останется целой
            if (_mat == null) _mat = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            ClearCompositeHalo();
            _skinned.Clear();
            var images = new List<Image>();
            foreach (var g in GetComponentsInChildren<Image>(true))
            {
                if (g.name.StartsWith("__lvn-composite-")) continue;
                var owner = g.GetComponentInParent<LvnSpriteFxDriver>();
                if (owner != null && owner != this) continue;
                images.Add(g);
            }

            // Wide ascendant fields need their own enlarged exterior pass even
            // for a flat one-layer actor; ordinary effects only pay this cost
            // when several layers must share one silhouette.
            _compositeHalo = !_scopedPart && images.Count > 0
                && (images.Count > 1 || (_tAura > 0f && _auraStyle > 9.5f))
                && (_tAura > 0f || _tOutline > 0f || _tGlow > 0f);
            if (_compositeHalo)
            {
                if (_stencilRef == 0)
                {
                    _stencilRef = _nextStencilRef++;
                    if (_nextStencilRef > 254) _nextStencilRef = 1;
                }
                _maskMat = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
                _haloMat = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            foreach (var g in images)
            {
                g.material = _mat;
                _skinned.Add(g);
            }
            if (_compositeHalo)
            {
                // Paint order matters: all visible parts → expanded union mask
                // → halo copies tested against that finished union.
                foreach (var g in images) AddCompositeLayer(g, _maskMat, _maskLayers, "mask");
                if (_auraStyle > 9.5f)
                {
                    // Ascendant is one large atmospheric field, not an outline
                    // repeated for every face/body/weapon layer. Anchor it to
                    // the largest layer (normally the full-body canvas).
                    var fieldSource = images[0];
                    var fieldArea = RectArea(fieldSource);
                    for (var i = 1; i < images.Count; i++)
                    {
                        var candidateArea = RectArea(images[i]);
                        if (candidateArea <= fieldArea) continue;
                        fieldSource = images[i];
                        fieldArea = candidateArea;
                    }
                    AddCompositeLayer(fieldSource, _haloMat, _haloLayers, "halo");
                }
                else
                {
                    foreach (var g in images)
                        AddCompositeLayer(g, _haloMat, _haloLayers, "halo");
                }
            }
            SyncCompositeHaloGeometry();
            SyncMaterial(_mat, haloOnly: false);
            if (_maskMat != null) SyncMaskMaterial(_maskMat);
            if (_haloMat != null) SyncMaterial(_haloMat, haloOnly: true);
        }

        private void Unskin()
        {
            foreach (var g in _skinned) if (g != null) g.material = null;
            _skinned.Clear();
            ClearCompositeHalo();
            _compositeHalo = false;
        }

        private void AddCompositeLayer(Image source, Material material,
            List<HaloLayer> destination, string kind)
        {
            var go = new GameObject("__lvn-composite-" + kind + ":" + source.name,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var copy = go.GetComponent<Image>();
            copy.raycastTarget = false;
            copy.material = material;
            copy.maskable = source.maskable;
            copy.preserveAspect = source.preserveAspect;
            copy.type = source.type;
            copy.fillCenter = source.fillCenter;
            copy.fillMethod = source.fillMethod;
            copy.fillAmount = source.fillAmount;
            copy.fillClockwise = source.fillClockwise;
            copy.fillOrigin = source.fillOrigin;
            copy.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
            go.transform.SetParent(source.transform.parent, false);
            go.transform.SetAsLastSibling();
            destination.Add(new HaloLayer { Source = source, Halo = copy });
        }

        private static float RectArea(Image image)
        {
            if (image == null) return 0f;
            var rect = ((RectTransform)image.transform).rect;
            return Mathf.Abs(rect.width * rect.height);
        }

        private void SyncCompositeHaloGeometry()
        {
            SyncCompositeLayerGeometry(_maskLayers, 1f);
            SyncCompositeLayerGeometry(_haloLayers, AuraCanvasScale());
        }

        private float AuraCanvasScale()
            => _auraStyle > 9.5f ? Mathf.Max(1.05f, 1.42f * _auraSize) : 1f;

        private static void SyncCompositeLayerGeometry(List<HaloLayer> layers, float visualScale)
        {
            foreach (var layer in layers)
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
                dst.localScale = src.localScale * visualScale;
                halo.sprite = source.sprite;
                halo.overrideSprite = source.overrideSprite;
                halo.color = source.color;
                halo.enabled = source.enabled;
            }
        }

        private void ClearCompositeHalo()
        {
            ClearCompositeLayers(_maskLayers);
            ClearCompositeLayers(_haloLayers);
            DestroyMaterial(ref _maskMat);
            DestroyMaterial(ref _haloMat);
        }

        private static void ClearCompositeLayers(List<HaloLayer> layers)
        {
            foreach (var layer in layers)
            {
                if (layer.Halo == null) continue;
                var go = layer.Halo.gameObject;
                go.SetActive(false);
                if (Application.isPlaying) Destroy(go);
                else DestroyImmediate(go);
            }
            layers.Clear();
        }

        private static void DestroyMaterial(ref Material material)
        {
            if (material != null)
            {
                if (Application.isPlaying) Destroy(material);
                else DestroyImmediate(material);
                material = null;
            }
        }

        private void Update()
        {
            if (_speed > 0f)
            {
                float k = Time.unscaledDeltaTime * _speed;
                _fade = Mathf.MoveTowards(_fade, _tFade, k);
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
            bool идёт = !Mathf.Approximately(_fade, _tFade)
                        || !Mathf.Approximately(_outline, _tOutline) || !Mathf.Approximately(_glow, _tGlow)
                        || !Mathf.Approximately(_dissolve, _tDissolve) || !Mathf.Approximately(_flash, _tFlash)
                        || !Mathf.Approximately(_dark, _tDark) || !Mathf.Approximately(_tintFx, _tTintFx)
                        || !Mathf.Approximately(_ghost, _tGhost) || !Mathf.Approximately(_petrify, _tPetrify)
                        || !Mathf.Approximately(_hologram, _tHologram) || !Mathf.Approximately(_burn, _tBurn)
                        || !Mathf.Approximately(_rim, _tRim) || !Mathf.Approximately(_shake, _tShake)
                        || !Mathf.Approximately(_aura, _tAura) || !Mathf.Approximately(_blade, _tBlade)
                        || !Mathf.Approximately(_lightning, _tLightning) || !Mathf.Approximately(_runes, _tRunes);
            if (_mat != null) SyncMaterial(_mat, haloOnly: false);
            if (_maskMat != null) SyncMaskMaterial(_maskMat);
            if (_haloMat != null) SyncMaterial(_haloMat, haloOnly: true);
            if (!идёт)
            {
                // дошли до целей; полностью нулевые — вернуть дефолтный материал
                if (_fade >= 0.999f &&
                    _outline <= 0f && _glow <= 0f && _dissolve <= 0f && _flash <= 0f &&
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
            // Гашение — ВСЕМ проходам, включая ореол и маску силуэта: иначе
            // исчезающий герой оставит на экране свою обводку.
            mat.SetFloat("_Fade", _fade);
            mat.SetVector("_FadeRamp", FadeRamp());
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
            mat.SetFloat("_AuraSize", _auraSize);
            mat.SetFloat("_AuraSpeed", _auraSpeed);
            mat.SetFloat("_AuraDensity", _auraDensity);
            mat.SetFloat("_CompositeSource", splitExterior && !haloOnly ? 1f : 0f);
            mat.SetFloat("_CompositeOnly", haloOnly ? 1f : 0f);
            mat.SetFloat("_StencilOnly", 0f);
            mat.SetFloat("_CompositeDilate", 0f);
            mat.SetFloat("_CompositeUvScale",
                haloOnly && _auraStyle > 9.5f ? AuraCanvasScale() : 1f);
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

        private void SyncMaskMaterial(Material mat)
        {
            // The mask pass returns transparent colour but replaces stencil for
            // the union expanded by ~0.9% of the layer UV box.
            mat.SetFloat("_Fade", _fade);   // гаснущий герой сужает и свой силуэт
            mat.SetVector("_FadeRamp", FadeRamp());
            mat.SetFloat("_CompositeSource", 0f);
            mat.SetFloat("_CompositeOnly", 0f);
            mat.SetFloat("_StencilOnly", 1f);
            mat.SetFloat("_CompositeDilate", 0.009f);
            mat.SetFloat("_CompositeUvScale", 1f);
            mat.SetFloat("_AuraSize", _auraSize);
            mat.SetFloat("_AuraSpeed", _auraSpeed);
            mat.SetFloat("_AuraDensity", _auraDensity);
            mat.SetFloat("_StencilRef", _stencilRef);
            mat.SetFloat("_StencilComp", (float)CompareFunction.Always);
            mat.SetFloat("_StencilOp", (float)StencilOp.Replace);
            mat.SetFloat("_StencilWriteMask", 255f);
            mat.SetFloat("_Outline", 0f);
            mat.SetFloat("_Glow", 0f);
            mat.SetFloat("_Aura", 0f);
            mat.SetFloat("_Blade", 0f);
            mat.SetFloat("_Lightning", 0f);
            mat.SetFloat("_Runes", 0f);
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
