using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The ONE PanelSettings every LVN UIDocument shares. A single panel keeps
    /// focus/gamepad navigation working across documents, shares one dynamic
    /// atlas, and gives the whole UI one consistent scale; per-document layering
    /// is <see cref="UIDocument.sortingOrder"/> within the panel.
    ///
    /// Scaling: ScaleWithScreenSize against the 1080×1920 portrait reference —
    /// every size in the engine is authored in reference pixels and scales with
    /// the device. Match follows orientation (width-driven in portrait,
    /// height-driven in landscape) so neither axis ever crops the chrome;
    /// registered uGUI CanvasScalers (the world stage) are kept on the SAME
    /// match so scene and chrome never scale apart.
    /// </summary>
    public static class LvnPanel
    {
        // ОПОРНОЕ РАЗРЕШЕНИЕ — КОНТРАКТ С КОНТЕНТОМ, а не настройка вкуса.
        //
        // В нём заданы ВСЕ абсолютные размеры: `text size=13` в сценарии
        // попадает прямо в fontSize панели, кегли экранов оболочки — тоже.
        // Сдвинуть это число значит пересчитать каждую новеллу, когда-либо
        // написанную, и пересчитать молча: ничего не сломается, всё просто
        // станет другого размера.
        //
        // Однажды я его сдвинул (1080×1920 → 720×1280), чтобы разом укрупнить
        // мелкий шрифт в хабе. Хаб стал лучше, а вёрстка дуэли — рассчитанная
        // под 1080 — поехала: подписи выросли в полтора раза и налезли друг на
        // друга. Мелкий шрифт в хабе лечится в хабе, а не переносом мира.
        public const int ReferenceWidth = 1080;
        public const int ReferenceHeight = 1920;

        private static PanelSettings _shared;
        private static readonly List<CanvasScaler> _scalers = new List<CanvasScaler>();
        private static LvnPanelWatcher _watcher;

        /// <summary>The shared PanelSettings (created on first use).</summary>
        public static PanelSettings Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = ScriptableObject.CreateInstance<PanelSettings>();
                    _shared.name = "LvnPanel";
                    _shared.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                    _shared.referenceResolution = new Vector2Int(ReferenceWidth, ReferenceHeight);
                    _shared.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                    _shared.match = CurrentMatch;
                    // Above the world-stage canvas (sortingOrder 0); documents
                    // inside the panel layer by their own sortingOrder.
                    _shared.sortingOrder = 10;
                    // Mobile atlas budget: 4096² RGBA is 64 MB — halve the
                    // ceiling. (Textures over maxSubTextureSize — the default
                    // 64px — never enter the atlas, so story art stays out.)
                    if (_shared.dynamicAtlasSettings != null)
                        _shared.dynamicAtlasSettings.maxAtlasSize = 2048;
                    ApplyUiScale();
                    LvnPrefs.Changed -= ApplyUiScale;
                    LvnPrefs.Changed += ApplyUiScale;
                    EnsureWatcher();
                }
                return _shared;
            }
        }

        /// <summary>
        /// МАСШТАБ ИНТЕРФЕЙСА ИГРОКА — через опорное разрешение панели.
        ///
        /// <para>Опора меньше — элементы крупнее: интерфейс считается в долях
        /// от неё, поэтому одна цифра увеличивает ВСЁ разом (шрифты, отступы,
        /// иконки, зоны нажатия), а не только текст. Растянуть один кегль было
        /// бы дешевле, но подписи выехали бы из своих кнопок.</para>
        ///
        /// <para>Сцена НЕ масштабируется вместе с оболочкой: её опора остаётся
        /// на месте (<see cref="RegisterScaler"/>), потому что фон и люди — это
        /// картина, а не интерфейс. Увеличить её значило бы обрезать кадр,
        /// который автор построил.</para>
        /// </summary>
        public static void ApplyUiScale()
        {
            if (_shared == null) return;
            float k = Mathf.Clamp(LvnPrefs.UiScale, 0.85f, 1.3f);
            _shared.referenceResolution = new Vector2Int(
                Mathf.RoundToInt(ReferenceWidth / k), Mathf.RoundToInt(ReferenceHeight / k));
        }

        /// <summary>Width-driven in portrait, height-driven in landscape — the
        /// stable axis drives the scale, flex absorbs the variable one.</summary>
        public static float CurrentMatch => Screen.width > Screen.height ? 1f : 0f;

        /// <summary>Set the runtime theme once (both the shell and the stage call
        /// this; last non-null wins — they pass the same asset).</summary>
        public static void SetTheme(ThemeStyleSheet theme)
        {
            if (theme != null) Shared.themeStyleSheet = theme;
        }

        /// <summary>Keep a uGUI CanvasScaler (the world-stage canvas) on the same
        /// reference/match as the panel so scene and chrome scale together.</summary>
        public static void RegisterScaler(CanvasScaler scaler)
        {
            if (scaler == null) return;
            _scalers.RemoveAll(s => s == null);
            if (!_scalers.Contains(scaler)) _scalers.Add(scaler);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = CurrentMatch;
            EnsureWatcher();
        }

        private static void EnsureWatcher()
        {
            if (_watcher != null) return;
            var go = new GameObject("LvnPanelWatcher") { hideFlags = HideFlags.HideAndDontSave };
            // DontDestroyOnLoad is play-mode-only — an editor test building the
            // shell must not throw here (HideAndDontSave already keeps it).
            if (Application.isPlaying) Object.DontDestroyOnLoad(go);
            _watcher = go.AddComponent<LvnPanelWatcher>();
        }

        // Re-applies the orientation match when the screen turns/resizes. Cheap:
        // two int compares per frame, writes only on change.
        private sealed class LvnPanelWatcher : MonoBehaviour
        {
            private int _w, _h;

            private void Update()
            {
                if (Screen.width == _w && Screen.height == _h) return;
                _w = Screen.width; _h = Screen.height;
                float match = CurrentMatch;
                if (_shared != null) _shared.match = match;
                for (int i = _scalers.Count - 1; i >= 0; i--)
                {
                    if (_scalers[i] == null) { _scalers.RemoveAt(i); continue; }
                    _scalers[i].matchWidthOrHeight = match;
                }
            }
        }
    }
}
