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
    /// Available before the manifest: the product title comes from the build,
    /// then the cached catalogue. A quiet ELVIN signature stays at the bottom.
    /// </summary>
    internal static partial class BootVeil
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
                ResetPresentation();
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
                cam.backgroundColor = LvnDawn.Ground;
            }

            (_go, _root) = LvnFloor.Open("LvnBootVeil", LvnFloor.BootVeil);
            _root.style.backgroundColor = LvnDawn.Ground;
            // A UIDocument root defaults to PickingMode.Ignore — without this
            // the "opaque" veil lets taps fall through to the screens under it.
            _root.pickingMode = PickingMode.Position;

            BuildLayout();
            ResetPresentation();

            // The glide: the shown percent approaches the milestone/byte target
            // smoothly and NEVER goes backwards — no lurching numbers. Text and
            // width only touch the tree when the integer percent moves (a dirty
            // layout every 16ms for a whole boot would be pure waste).
            int lastShown = -1;
            _root.schedule.Execute(ts =>
            {
                if (_pct == null) return;
                // A stalled network request may report no milestones at all.
                // The wait indicator still needs to appear on its own timer.
                RevealIfWaiting();
                _model.TickToward(CreepTarget(), ts.deltaTime / 1000f);
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
            if (t > _target)
            {
                _target = t;
                // ВЕХА — НЕ ОСТАНОВКА. Между вехами идёт настоящая работа с
                // непредсказуемым сроком: связь, индекс версий, манифест. Полоса
                // доезжала до вехи и ЗАМИРАЛА — «на тридцати процентах встаёт на
                // полсекунды-секунду» (Илья 01.09), и это читается как зависание,
                // хотя всё идёт. Дальше вехи она продолжает ползти к мягкому
                // потолку — медленно и всегда меньше следующей вехи, поэтому
                // обмана нет: доехать до конца ползком нельзя.
                _creepFrom = _model.Percent / 100f;
                _creepCeil = Mathf.Min(t + CreepGap, 0.95f);
                _creepStarted = Lvn.LvnClock.Now();
            }
            if (_status != null && status != null) _status.text = status;
            // ЗАТЯНУЛОСЬ — ПОКАЗЫВАЕМ РАБОТУ. Молчаливое имя дольше трёх секунд
            // читается как зависание: на первой установке качается содержимое, и
            // там полоса нужна. Обычный запуск до этого места не доживает.
            RevealIfWaiting();
        }

        // Насколько полоса вправе уползти за веху и как быстро. Треть пути до
        // следующей вехи за пару секунд: заметно, что живое, и не обгоняет
        // настоящую работу.
        private const float CreepGap = 0.18f;
        private const float CreepTau = 2.2f;
        private static float _creepFrom, _creepCeil, _creepStarted;

        /// <summary>Куда полосе ползти прямо сейчас: веха, а после неё —
        /// медленный подъём к мягкому потолку.</summary>
        private static float CreepTarget()
        {
            if (_creepCeil <= _target) return _target;
            float k = 1f - Mathf.Exp(-(Lvn.LvnClock.Now() - _creepStarted) / CreepTau);
            return Mathf.Max(_target, Mathf.Lerp(Mathf.Max(_creepFrom, _target), _creepCeil, k));
        }

        /// <summary>Status text only (e.g. reconnect notices) — never moves the bar.</summary>
        public static void Status(string status)
        {
            if (_status != null && status != null) _status.text = status;
        }

        /// <summary>Вуаль ещё на экране (первый вход держит её до одетой сцены).</summary>
        public static bool IsVisible => _go != null;

        private static Label _brandTitle;
        /// <summary>СКОЛЬКО ИМЯ СТОИТ НА ЭКРАНЕ. Заставка — не ожидание, а
        /// вступление: тёмный экран, имя игры, ровный уход. Две секунды — тот
        /// срок, за который успевает всё остальное (витрина рисуется по
        /// вчерашнему каталогу, полотно встаёт), и заставка перестаёт быть
        /// платой за запуск: она и есть запуск.</summary>
        public const float BrandHoldSeconds = 2.0f;

        /// <summary>НЕ УСПЕЛИ — ПОКАЗЫВАЕМ РАБОТУ. Молчаливое имя дольше этого
        /// читается как зависание: первая установка качает содержимое минутами,
        /// и там полоса нужна. Тогда она и возвращается.</summary>
        public const float BarAfterSeconds = 3.0f;

        private static float _splashAt = -1f;   // когда показали имя (реальные часы)
        private static bool _barBack;           // полоса вернулась: ждём дольше обычного

        /// <summary>Имя ещё держит свой срок — гасить рано.</summary>
        public static bool BrandHolding =>
            _splashAt >= 0f && Lvn.LvnClock.Wall() - _splashAt < BrandHoldSeconds;

        /// <summary>
        /// ЗАСТАВКА С ПЕРВОГО КАДРА: тёмный экран и имя игры вместо процентов.
        ///
        /// <para>Отличается от <see cref="Brand"/> тем, что не объявляет работу
        /// законченной: под именем всё ещё идёт запуск, и если он затянется
        /// дольше <see cref="BarAfterSeconds"/>, полоса вернётся сама.</para>
        /// </summary>
        public static void Splash(string title)
        {
            if (_root == null) return;
            if (_splashAt < 0f) _splashAt = Lvn.LvnClock.Wall();
            _barBack = false;
            ShowBrandLabel(title);
            HideBar();
        }

        private static void HideBar()
        {
            if (_progress != null) _progress.style.visibility = Visibility.Hidden;
        }

        private static void RevealBar()
        {
            if (_barBack) return;
            _barBack = true;
            if (_progress != null) _progress.style.visibility = Visibility.Visible;
        }

        private static void RevealIfWaiting()
        {
            if (_splashAt >= 0f && !_barBack && _target < 1f
                && Lvn.LvnClock.Wall() - _splashAt > BarAfterSeconds) RevealBar();
        }

        /// <summary>Брендовый режим первого входа: ни процентов, ни полосы —
        /// только имя продукта, проявляющееся фейдом. Загрузка идёт под вуалью;
        /// гасит её хост, когда сцена одета (RevealFromLoadingAsync) — один
        /// кроссфейд из имени прямо в игру, юзер не видит «загрузку» вовсе.</summary>
        public static void Brand(string title)
        {
            if (_root == null) return;
            HideBar();
            _model.SnapToFull(); // FadeOutAsync не должен ждать «доезда» скрытой полосы
            _target = 1f;
            ShowBrandLabel(title);
        }

        /// <summary>Само имя: появляется фейдом и живёт, пока живёт вуаль.
        /// Общее у брендового режима первого входа и у заставки запуска —
        /// иначе «как выглядит имя» пришлось бы описывать дважды.</summary>
        private static void ShowBrandLabel(string title)
        {
            if (_brandTitle == null) return;
            if (!_brandShown)
            {
                _brandShown = true;
                // Legible on the first painted frame, then a single quiet
                // reveal. No movement, pulsing or extra minimum boot delay.
                _identity.style.opacity = LvnPrefs.ReduceMotion ? 1f : 0.35f;
                // ВЕСЬ ЭТОТ ФАЙЛ считает время реальным, а не часами интерфейса
                // (Lvn.LvnClock). Бут — единственное место, где кадры рвутся
                // и подолгу стоят: загрузка манифеста, разбор атласов, первый
                // шейдер. Кадровые часы вместе с ними встают, и вуаль вместе с
                // ними висит — а она обязана уйти по часам, а не по кадрам.
                float t0 = Lvn.LvnClock.Wall();
                int gen = _gen;
                IVisualElementScheduledItem reveal = null;
                reveal = _root.schedule.Execute(() =>
                {
                    if (_brandTitle == null || _gen != gen)
                    {
                        reveal.Pause();
                        return;
                    }
                    float k = LvnPrefs.ReduceMotion ? 1f
                        : Mathf.Clamp01((Lvn.LvnClock.Wall() - t0) / 0.65f);
                    _identity.style.opacity = Mathf.Lerp(0.35f, 1f, k * k * (3f - 2f * k));
                    // Complete the last paint BEFORE stopping, even if boot
                    // blocked the main thread past the entire reveal duration.
                    if (k >= 1f) reveal.Pause();
                }).Every(16);
            }
            _brandTitle.text = string.IsNullOrWhiteSpace(title) ? Lvn.LvnEngine.Name : title;
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
            // Заставка без полосы: ждать её «доезда» не на чем — снимаем сразу.
            if (_splashAt >= 0f && !_barBack) _model.SnapToFull();
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
            _identity = null; _progress = null;
            _target = 0f;
            _model.Reset();
        }
    }
}
