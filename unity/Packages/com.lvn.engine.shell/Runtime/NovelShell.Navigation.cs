using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// НАВИГАЦИЯ ОБОЛОЧКИ — часть <see cref="NovelShell"/>: вкладки нижней
    /// панели, стопка модальных экранов и правило «кто сейчас на экране».
    ///
    /// <para>Здесь живёт ответ на «назад»: у стопки модалок он свой, у вкладок
    /// свой, и путать их нельзя — из открытой истории «назад» обязан закрыть
    /// историю, а не увести на другую вкладку.</para>
    /// </summary>
    public sealed partial class NovelShell
    {
        /// <summary>Страница за вкладкой. Номера — у набора вкладок
        /// (<see cref="LvnTabs"/>), здесь только ответ «чей это экран»: у
        /// галереи страницы нет вовсе, она открывает модаль.</summary>
        private (VisualElement el, LvnOverlayScreen scr) TabPage(int i) => i switch
        {
            LvnTabs.Home => (Hub?.ContentRoot, null),
            LvnTabs.Titles => (Titles, Titles),
            LvnTabs.Store => (PackShop, PackShop),
            LvnTabs.Wardrobe => (WardrobeTab, WardrobeTab),
            LvnTabs.Profile => (Profile, Profile),
            _ => (null, null),
        };

        // ── РОУТЕР МОДАЛЕЙ (решение Ильи 27.08: «стейт как в реакте») ──
        // ДОКТРИНА ДВУХ СЛОТОВ: страница — ровно ОДНА, живёт в tabsLayer и
        // меняется только TabGoTo; модаль — СТЕК в popupLayer, каждая обязана
        // нести свой фон (скрим+лист), открывается только через ShowModalAsync.
        // Тогда «одно поверх другого» невозможно физически: страницы не
        // складываются, модали не просвечивают.
        private readonly List<LvnOverlayScreen> _modals = new List<LvnOverlayScreen>();

        /// <summary>Единственная дверь модалей: ведёт стек (системная «назад»
        /// закрывает верхнюю) и глушит Escape-обработчик сцены на время показа.</summary>
        public async Task<bool> ShowModalAsync(LvnOverlayScreen screen, CancellationToken ct = default)
        {
            if (screen == null) return false;
            _modals.Add(screen);
            Lvn.UI.LvnModalGuard.Depth = _modals.Count;
            try { return await screen.ShowAsync(ct); }
            finally
            {
                _modals.Remove(screen);
                Lvn.UI.LvnModalGuard.Depth = _modals.Count;
            }
        }

        /// <summary>Закрыть верхнюю модаль (системная «назад»). false — стек пуст.</summary>
        public bool CloseTopModal()
        {
            if (_modals.Count == 0) return false;
            _modals[_modals.Count - 1].RequestCancel();
            return true;
        }

        // Системная «назад»: КТО НАВЕРХУ — не наше решение, а Режиссёра. Своя
        // лесенка условий здесь и была вторым ответом на тот же вопрос: сцена
        // спрашивала Режиссёра, оболочка перебирала признаки сама, и алерта в
        // этой картине не было вовсе. Оболочка исполняет «назад» для СВОИХ
        // поверхностей: стопка модалей и лента вкладок.
        private void Update()
        {
            if (!UnityEngine.Input.GetKeyDown(KeyCode.Escape)) return;
            switch (Lvn.UI.LvnScreenDirector.Current.BackTarget)
            {
                // Алерт закрывается только своими кнопками — решение должно
                // быть осознанным.
                case Lvn.UI.LvnScreenDirector.Alert: return;
                case Lvn.UI.LvnScreenDirector.ShellModal: CloseTopModal(); return;
                // Сюжетную панель и квик-меню закрывает сцена.
                case Lvn.UI.LvnScreenDirector.StoryPanel:
                case Lvn.UI.LvnScreenDirector.QuickMenu: return;
            }
            if (InChapter) return;    // экран чист, глава идёт — «назад» не наш
            if (_tab != LvnTabs.Home && !_tabBusy) LvnAsync.Fire(TabGoTo(LvnTabs.Home), "BackHome");
        }

        public async Task TabGoTo(int target)
        {
            if (_tabBusy || target == _tab) return;
            var to = TabPage(target);
            if (to.el == null) return;
            _tabBusy = true;
            OnTabTravel?.Invoke(_tab, target);
            try
            {
                var from = TabPage(_tab);
                int dir = target > _tab ? 1 : -1;
                int leaving = _tab;
                // КУДА ЕДЕМ — ИЗВЕСТНО СРАЗУ. Номер вкладки менялся в КОНЦЕ
                // переезда, и всё, что спрашивало «где игрок» по дороге,
                // получало старый ответ: закрытие гардероба идёт ВНУТРИ этого
                // же перехода и возвращало сцене БОКОВУЮ композицию — героиня,
                // уже уехавшая на главную, отскакивала вправо («героиня ходуном
                // ходит… по центру, а становится справа» — Илья 08.09).
                _tab = target;
                float w = _root.resolvedStyle.width;
                if (w <= 0f || float.IsNaN(w)) w = 1080f;
                // ЭКРАНЫ СТОЯТ В ПРОСТРАНСТВЕ, ЛЕТИТ КАМЕРА. Раньше каждая
                // страница приезжала по своей траектории, и мир на переезде
                // разваливался. Комнаты расставлены ромбом — Главная сверху,
                // гардероб слева, магазин справа, профиль снизу, — и переход
                // это ОДИН перелёт между двумя точками: обе страницы едут по
                // общему вектору, сохраняя расстояние между собой, а полотно и
                // героиня летят с ними («будто интерфейс в пространстве был, а
                // к нему камера с героиней прилетали» — Илья 08.09).
                Vector2 hop = TabHop(leaving, target, w);
                Vector2 toStart = hop, fromEnd = -hop;
                Lvn.LvnLog.Trace($"[lvn-hop] переезд {leaving} → {target}: вектор "
                               + $"({hop.x:0}, {hop.y:0}) при экране {w:0}x{_root.resolvedStyle.height:0}; "
                               + $"приходящая стартует с ({toStart.x:0}, {toStart.y:0}), "
                               + $"уходящая уедет в ({fromEnd.x:0}, {fromEnd.y:0})");

                to.scr?.ShowAsTab();
                to.el.style.display = DisplayStyle.Flex;
                to.el.style.translate = new Translate(toStart.x, toStart.y);
                Hub?.SetActiveTab(target);
                // СНАЧАЛА СОБРАТЬСЯ, ПОТОМ ЕХАТЬ. Экран вкладки пересобирает
                // своё тело в ShowAsTab и проявляется сам, как только посчитана
                // геометрия. Пока это происходило ВО ВРЕМЯ переезда, движение
                // читалось не переездом, а пересборкой: панель проступала
                // кусками там, куда её как раз везли («не переезжает, а
                // перестраивается будто» — Илья 08.09). Ждём готовый кадр — с
                // потолком, чтобы медленный экран не подвесил переход.
                await WaitLaidOutAsync(to.el);

                var fromEl = from.el;
                float canvasFrom = _tabCanvasX, canvasTo = target * w * 0.067f; // втрое медленнее — глубина
                // 338 = 260 + 30% — «чуть медленнее» (26.08). Ожидание конца
                // движения — у дома движения: оборванная анимация не должна
                // оставить флаг «занято» поднятым навсегда.
                await Lvn.UI.LvnMotion.PlayAsync(to.el, Lvn.UI.LvnMenuStage.TravelMs, (e, p) =>
                {
                    // Кривая ПОЛЁТА, не «прихода»: камера с героиней летят к
                    // комнате, а сцена везёт фигуру между слотами той же
                    // кривой — иначе интерфейс уже стоит, а героиня ещё едет.
                    float k = Lvn.UI.LvnMotion.Glide(p);
                    var here = Vector2.Lerp(toStart, Vector2.zero, k);
                    e.style.translate = new Translate(here.x, here.y);
                    // ИЗДАЛЕКА — И ВДАЛЬ. Приходящая комната растёт из дальнего
                    // плана до полного размера, уходящая уменьшается: без
                    // этого страницы скользят в одной плоскости, а Илья хочет
                    // глубину («как будто издалека приезжают и уезжают» —
                    // 08.09).
                    float near = Mathf.Lerp(FarScale, 1f, k);
                    e.style.scale = new Scale(new Vector2(near, near));
                    // ГАСНЕТ ТОЛЬКО УХОДЯЩАЯ. Приходящая едет как есть — это и
                    // читается переездом. А вот уходящая обязана гаснуть: лист
                    // гардероба и магазин полупрозрачны, под ними видна главная,
                    // и без угасания две комнаты наезжают друг на друга
                    // («при переходе накладывает друг на друга интерфейс» —
                    // Илья 08.09).
                    if (fromEl != null)
                    {
                        var gone = Vector2.Lerp(Vector2.zero, fromEnd, k);
                        fromEl.style.translate = new Translate(gone.x, gone.y);
                        fromEl.style.opacity = Mathf.Clamp01(1f - k / 0.5f);
                        float far = Mathf.Lerp(1f, FarScale, k);
                        fromEl.style.scale = new Scale(new Vector2(far, far));
                    }
                    _tabCanvasX = Mathf.Lerp(canvasFrom, canvasTo, k); // полотно едет с нами
                    OnTabTravelTick?.Invoke(k); // сцена меню — той же кривой
                    if (_canvasTint != null)
                        _canvasTint.style.backgroundColor = Color.Lerp(
                            TabTints[Mathf.Clamp(_tab, 0, LvnTabs.PageCount - 1)],
                            TabTints[Mathf.Clamp(target, 0, LvnTabs.PageCount - 1)], k);
                });
                _tabCanvasX = canvasTo;
                Lvn.LvnLog.Trace($"[lvn-hop] переезд {leaving} → {target} доехал: "
                               + $"приходящая на ({to.el.resolvedStyle.translate.x:0}, "
                               + $"{to.el.resolvedStyle.translate.y:0}), должна быть в (0, 0)");

                if (from.scr != null) from.scr.HideAsTab();
                else if (fromEl != null) fromEl.style.display = DisplayStyle.None;
                if (fromEl != null)
                {
                    fromEl.style.translate = new Translate(0f, 0f);
                    fromEl.style.opacity = 1f;   // спрятана — но не полупрозрачна
                    fromEl.style.scale = new Scale(Vector2.one);
                }
                to.el.style.translate = new Translate(0f, 0f);
                to.el.style.scale = new Scale(Vector2.one);
                to.scr?.Settled();   // экран на месте — можно считать раскладку
                _ = dir;   // направление больше не решает: решает место кнопки
            }
            finally { _tabBusy = false; }
        }

        /// <summary>
        /// ПЕРЕЛЁТ КАМЕРЫ между двумя комнатами витрины — вектор в единицах
        /// экрана. Комнаты стоят по карте (<see cref="LvnTabs.Room"/>):
        /// приходящая страница стартует со стороны своей комнаты, уходящая
        /// уезжает в противоположную.
        ///
        /// <para>Путь НАРОЧНО короткий: экран во всю ширину за 340 мс читается
        /// рывком, а не движением — UITK везёт живую страницу целиком.
        /// Направление важнее размаха, остальное доскажет прозрачность.</para>
        /// </summary>
        /// <summary>Сколько экранов между комнатами по главной оси перелёта —
        /// не меньше: больше единицы, чтобы страницы не перекрывались.</summary>
        private const float MinScreensApart = 1.1f;

        /// <summary>Масштаб «дальней» комнаты: с него приходящая растёт до
        /// полного размера, до него уходящая сжимается.</summary>
        private const float FarScale = 0.86f;

        private Vector2 TabHop(int from, int to, float w)
        {
            var a = LvnTabs.Room(from);
            var b = LvnTabs.Room(to);
            float h = _root.resolvedStyle.height;
            if (h <= 0f || float.IsNaN(h)) h = 1920f;
            // Карта в экранных осях (y вниз), как и translate страницы:
            // комната ниже — страница приезжает снизу, знак общий.
            //
            // ХОД БОЛЬШОЙ — НАРАВНЕ С КАМЕРОЙ. Короткий путь читался как
            // «интерфейс искусственный»: полотно уезжало на полкартины, а
            // панели едва трогались с места, и мир распадался на два. Между
            // крайними комнатами экран проходит больше своей ширины и три
            // четверти высоты — столько же, сколько взгляд («надо, чтобы
            // прям ездил… вместе с камерой наравне» — Илья 08.09).
            var hop = new Vector2((b.x - a.x) * 2.0f * w, (b.y - a.y) * 1.5f * h);
            // ЭКРАНЫ НЕ ПЕРЕКРЫВАЮТСЯ. Приходящая и уходящая страницы стоят
            // друг от друга ровно на вектор перелёта; если по главной оси он
            // короче экрана, по дороге они наезжают друг на друга («надо чуть
            // раздвинуть, а то наезжают» — Илья 08.09). Вектор тянется до
            // 1.1 экрана по своей главной оси — между комнатами остаётся
            // просвет.
            float span = Mathf.Max(Mathf.Abs(hop.x) / w, Mathf.Abs(hop.y) / h);
            if (span > 0.001f && span < MinScreensApart) hop *= MinScreensApart / span;
            Lvn.LvnLog.Trace($"[lvn-hop] комнаты: {from} = ({a.x:0.00}, {a.y:0.00}) → "
                           + $"{to} = ({b.x:0.00}, {b.y:0.00}); шаг ({hop.x:0}, {hop.y:0}), "
                           + $"экранов по главной оси {Mathf.Max(Mathf.Abs(hop.x) / w, Mathf.Abs(hop.y) / h):0.00}");
            return hop;
        }

        /// <summary>ДОЖДАТЬСЯ ГОТОВОГО КАДРА страницы: пересобранное тело
        /// показывается по первой раскладке (LvnMontage.RevealWhenLaidOut), и
        /// до неё везти нечего. Потолок обязателен: страница может собираться
        /// долго (гардероб идёт на диск), а переход стоять не имеет права.</summary>
        private static async Task WaitLaidOutAsync(VisualElement el, float capSeconds = 0.16f)
        {
            if (el == null) return;
            float until = Lvn.LvnClock.Now() + capSeconds;
            while (Lvn.LvnClock.Now() < until && el.resolvedStyle.opacity < 0.99f)
                await Task.Yield();
        }

        /// <summary>Мгновенно домой (гардероб/старт главы): без анимации.</summary>
        public void TabReset()
        {
            var from = TabPage(_tab);
            if (from.scr != null) from.scr.HideAsTab();
            var home = TabPage(LvnTabs.Home);
            if (home.el != null)
            {
                home.el.style.display = DisplayStyle.Flex;
                home.el.style.translate = new Translate(0f, 0f);
            }
            _tab = LvnTabs.Home;
            _tabCanvasX = 0f;
            Hub?.SetActiveTab(LvnTabs.Home, instant: true);
        }

        // ── НАБОР ЭКРАНОВ ВЕДЁТ СЕБЯ САМ ──
        // Перечень был написан от руки в ShowOnly, и дописать туда новый экран
        // забывали: таблица лидеров, экран конца главы и гардеробная вкладка в
        // него так и не попали. Держался он на втором ручном перечне — на том,
        // что каждый экран ещё и прячут поимённо сразу после создания. Два
        // списка одного набора, и оба надо было не забыть. Механизм уехал в
        // LvnScreenSet, здесь остался вопрос «что чем является».
        private readonly LvnScreenSet _screens = new LvnScreenSet();

        /// <summary>Внести ЭКРАН: в дерево и в набор. Он поднимается скрытым —
        /// показывает его тот, кто его открывает.</summary>
        private void Add(VisualElement el)
        {
            if (el == null) return;
            LvnChrome.Stretch(el);
            _root.Add(el);
            _screens.Add(el);
        }

        /// <summary>Внести ОСНАСТКУ — верхний бар и кружок загрузок. Она живёт
        /// ПОВЕРХ любого экрана и переживает «убрать всё»: бар — единый верх
        /// приложения, кружок показывает качанное из любого места, даже из-под
        /// алерта (живой репорт «закрыл — и остановилось»).</summary>
        private void AddChrome(VisualElement el)
        {
            if (el == null) return;
            LvnChrome.Stretch(el);
            _root.Add(el);
        }

        /// <summary>Убрать все экраны — приложение поднимается на чистом.</summary>
        private void ShowOnly() => _screens.HideAll();

        private static void Show(VisualElement el) { if (el != null) el.style.display = DisplayStyle.Flex; }

        /// <summary>Убрать один экран — тем же правилом, что и весь набор:
        /// у кого уход свой, тот уходит сам. Раньше это решалось на глаз в
        /// месте вызова, и заставка гасла, не вернув себе непрозрачность.</summary>
        private static void Hide(VisualElement el) => LvnScreenSet.Shut(el);
    }
}
