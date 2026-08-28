using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// THE app-boot surface — one continuous screen from the first rendered
    /// frame to the first interactive screen. <see cref="NovelApp"/> raises it
    /// on its FIRST frame (before any network round-trip) and keeps it up over
    /// the whole boot: connect → manifest → shell build → boot prefetch, then
    /// fades it into the menu. There is deliberately NO second loading screen
    /// behind it (the shell's boot splash is suppressed) — the user sees one
    /// bar that only moves forward, then one cross-fade into the app.
    ///
    /// It is manifest-independent by design, so it carries the ENGINE's
    /// identity: a steel ELVIN wordmark and the engine version at the bottom.
    /// A game's own branding takes over on the themed shell screens after it.
    /// </summary>
    internal static class BootVeil
    {
        private static GameObject _go;
        private static VisualElement _root;
        private static Label _pct;
        private static Label _status;
        private static VisualElement _fill;
        private static readonly LoadingProgressModel _model = new LoadingProgressModel(3.2f);
        private static float _target; // 0..1, milestones + real prefetch bytes
        // Veil generation: a stale FadeOutAsync (host destroyed/recreated the
        // NovelApp mid-fade) must never touch the NEXT boot's veil.
        private static int _gen;

        public static void Show()
        {
            if (_go != null)
            {
                // A new boot adopting a still-fading veil: cancel the stale fade
                // (generation bump) and reset it to a fresh, opaque start.
                _gen++;
                _target = 0f;
                _model.Reset();
                if (_root != null) _root.style.opacity = 1f;
                Status("");
                return;
            }
            _gen++;
            _target = 0f;
            _model.Reset();
            // The empty boot scene's camera clears to the DEFAULT SKYBOX — a
            // grey wash for any pixel the UI hasn't covered. Pin it to our own
            // dark so even frame 0's uncovered edges are the right colour.
            // (Fallback scan: an embedding host's camera may not be tagged
            // MainCamera.)
            var cam = Camera.main;
            if (cam == null) cam = Object.FindFirstObjectByType<Camera>();
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.063f, 0.063f, 0.082f); // #101015
            }

            _go = new GameObject("LvnBootVeil");
            var doc = _go.AddComponent<UIDocument>();
            doc.panelSettings = LvnPanel.Shared;
            doc.sortingOrder = 100; // above everything until the hand-off fade

            _root = doc.rootVisualElement;
            _root.style.flexGrow = 1;
            _root.style.backgroundColor = new Color(0.063f, 0.063f, 0.082f);
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;
            // A UIDocument root defaults to PickingMode.Ignore — without this
            // the "opaque" veil lets taps fall through to the screens under it.
            _root.pickingMode = PickingMode.Position;

            _pct = new Label("0%");
            _pct.style.fontSize = 30;
            _pct.style.color = new Color(0.81f, 0.78f, 0.74f); // #cfc8bd
            _pct.style.unityFontStyleAndWeight = FontStyle.Bold;
            _root.Add(_pct);

            // A thin steel progress track — the one indicator of the whole boot.
            var track = new VisualElement();
            track.style.width = 300; track.style.height = 3;
            track.style.marginTop = 14;
            track.style.backgroundColor = new Color(1f, 1f, 1f, 0.10f);
            _fill = new VisualElement();
            _fill.style.height = Length.Percent(100);
            _fill.style.width = Length.Percent(0);
            _fill.style.backgroundColor = new Color(0.83f, 0.87f, 0.91f); // сталь, в тон ELVIN
            track.Add(_fill);
            _root.Add(track);

            _status = new Label("");
            _status.style.fontSize = 14;
            _status.style.marginTop = 10;
            _status.style.color = new Color(0.60f, 0.58f, 0.54f); // #9a948a
            _root.Add(_status);

            // The engine brand: steel ELVIN + dimmed version, pinned to the bottom.
            var brand = new VisualElement();
            brand.style.position = Position.Absolute;
            brand.style.left = 0; brand.style.right = 0; brand.style.bottom = 46;
            brand.style.alignItems = Align.Center;
            brand.pickingMode = PickingMode.Ignore;

            var word = new Label(Lvn.LvnEngine.Name);
            word.style.fontSize = 30;
            word.style.unityFontStyleAndWeight = FontStyle.Bold;
            word.style.letterSpacing = 9;
            word.style.color = new Color(0.83f, 0.87f, 0.91f); // полированная сталь
            word.style.textShadow = new TextShadow
            {
                offset = new Vector2(0f, 2f),
                blurRadius = 5f,
                color = new Color(0f, 0f, 0f, 0.85f),
            };
            brand.Add(word);

            var ver = new Label("v" + Lvn.LvnEngine.Version);
            ver.style.fontSize = 12;
            ver.style.marginTop = 3;
            ver.style.letterSpacing = 3;
            ver.style.color = new Color(0.38f, 0.40f, 0.43f); // блёклый серый
            brand.Add(ver);
            _root.Add(brand);

            // The glide: the shown percent approaches the milestone/byte target
            // smoothly and NEVER goes backwards — no lurching numbers. Text and
            // width only touch the tree when the integer percent moves (a dirty
            // layout every 16ms for a whole boot would be pure waste).
            int lastShown = -1;
            _root.schedule.Execute(ts =>
            {
                if (_pct == null) return;
                _model.TickToward(_target, ts.deltaTime / 1000f);
                int p = _model.Percent;
                if (p == lastShown) return;
                lastShown = p;
                _pct.text = p + "%";
                if (_fill != null) _fill.style.width = Length.Percent(_model.FillPercent);
            }).Every(16);
        }

        /// <summary>Advance the target ("30" = 30%). Optional status line.
        /// The displayed value glides toward it monotonically.</summary>
        public static void Progress(int percent, string status = null)
        {
            float t = Mathf.Clamp01(percent / 100f);
            if (t > _target) _target = t;
            if (_status != null && status != null) _status.text = status;
        }

        /// <summary>Status text only (e.g. reconnect notices) — never moves the bar.</summary>
        public static void Status(string status)
        {
            if (_status != null && status != null) _status.text = status;
        }

        /// <summary>Вуаль ещё на экране (первый вход держит её до одетой сцены).</summary>
        public static bool IsVisible => _go != null;

        private static Label _brandTitle;

        /// <summary>Брендовый режим первого входа: ни процентов, ни полосы —
        /// только имя продукта, проявляющееся фейдом. Загрузка идёт под вуалью;
        /// гасит её хост, когда сцена одета (RevealFromLoadingAsync) — один
        /// кроссфейд из имени прямо в игру, юзер не видит «загрузку» вовсе.</summary>
        public static void Brand(string title)
        {
            if (_root == null) return;
            if (_pct != null) _pct.style.display = DisplayStyle.None;
            if (_fill?.parent != null) _fill.parent.style.display = DisplayStyle.None;
            if (_status != null) _status.style.display = DisplayStyle.None;
            _model.SnapToFull(); // FadeOutAsync не должен ждать «доезда» скрытой полосы
            _target = 1f;
            if (_brandTitle == null)
            {
                _brandTitle = new Label(title ?? "")
                {
                    pickingMode = PickingMode.Ignore,
                };
                _brandTitle.style.fontSize = 42;
                _brandTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
                _brandTitle.style.letterSpacing = 6;
                _brandTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
                _brandTitle.style.color = new Color(0.91f, 0.89f, 0.86f);
                _brandTitle.style.opacity = 0f;
                _brandTitle.style.textShadow = new TextShadow
                {
                    offset = new Vector2(0f, 2f),
                    blurRadius = 6f,
                    color = new Color(0f, 0f, 0f, 0.85f),
                };
                _root.Insert(0, _brandTitle);
                // ВЕСЬ ЭТОТ ФАЙЛ считает время реальным, а не часами интерфейса
                // (Lvn.LvnClock). Бут — единственное место, где кадры рвутся
                // и подолгу стоят: загрузка манифеста, разбор атласов, первый
                // шейдер. Кадровые часы вместе с ними встают, и вуаль вместе с
                // ними висит — а она обязана уйти по часам, а не по кадрам.
                float t0 = Lvn.LvnClock.Wall();
                int gen = _gen;
                _root.schedule.Execute(() =>
                {
                    if (_brandTitle == null || _gen != gen) return;
                    float k = Mathf.Clamp01((Lvn.LvnClock.Wall() - t0) / 1.4f);
                    _brandTitle.style.opacity = k * k * (3f - 2f * k);
                }).Every(16).Until(() => _brandTitle == null || _gen != gen
                    || Lvn.LvnClock.Wall() - t0 > 1.6f);
            }
            _brandTitle.text = title ?? "";
        }

        /// <summary>Glide to 100%, hold it one beat, then cross-fade out and
        /// destroy — the one screen hand-off of the whole boot. A stale call
        /// (the veil it was fading got replaced) exits without touching the
        /// newer veil.</summary>
        public static async Task FadeOutAsync(float seconds = 0.4f)
        {
            if (_go == null) return;
            int gen = _gen;
            _target = 1f;
            // Let the bar glide most of the way, then SNAP so the user actually
            // sees "100%" (the asymptote alone never reaches it in time).
            // Страховка по РЕАЛЬНОМУ времени: она на то и страховка, чтобы
            // сработать, когда кадры встали, — кадровые часы в этот момент
            // стоят вместе с ними.
            float safety = Lvn.LvnClock.Wall() + 0.9f;
            while (_model.Display < 0.98f && Lvn.LvnClock.Wall() < safety
                   && _go != null && _gen == gen)
                await Task.Yield();
            if (_go == null || _gen != gen) return;
            _model.SnapToFull();
            if (_pct != null) _pct.text = "100%";
            if (_fill != null) _fill.style.width = Length.Percent(100f);
            float hold = Lvn.LvnClock.Wall() + 0.12f;
            while (Lvn.LvnClock.Wall() < hold && _go != null && _gen == gen)
                await Task.Yield();

            float start = Lvn.LvnClock.Wall();
            while (_go != null && _gen == gen)
            {
                float k = seconds <= 0f ? 1f : (Lvn.LvnClock.Wall() - start) / seconds;
                if (_root != null) _root.style.opacity = 1f - Mathf.Clamp01(k);
                if (k >= 1f) break;
                await Task.Yield();
            }
            if (_gen == gen) Hide();
        }

        public static void Hide()
        {
            if (_go != null) Object.Destroy(_go);
            _go = null; _root = null; _pct = null; _status = null; _fill = null;
            _brandTitle = null;
            _target = 0f;
            _model.Reset();
        }
    }
}
