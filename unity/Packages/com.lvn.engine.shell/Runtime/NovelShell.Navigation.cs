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
        private (VisualElement el, LvnOverlayScreen scr) TabPage(int i) => i switch
        {
            0 => (Hub?.ContentRoot, null),
            1 => (PackShop, PackShop),
            2 => (WardrobeTab, WardrobeTab),
            3 => (Profile, Profile),
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

        // Системная «назад» ВНЕ сцены: сначала верхняя модаль, затем — домой по
        // ленте. Алерт (Popup) закрывается только своими кнопками — решение
        // должно быть осознанным.
        private void Update()
        {
            if (!UnityEngine.Input.GetKeyDown(KeyCode.Escape)) return;
            if (Popup != null && Popup.style.display == DisplayStyle.Flex) return;
            if (CloseTopModal()) return;
            if (_inChapter) return; // сюжетные панели и квик-меню закрывает VnStage
            if (_tab != 0 && !_tabBusy) LvnAsync.Fire(TabGoTo(0), "BackHome");
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
                float w = _root.resolvedStyle.width;
                if (w <= 0f || float.IsNaN(w)) w = 1080f;

                to.scr?.ShowAsTab();
                to.el.style.display = DisplayStyle.Flex;
                to.el.style.translate = new Translate(dir * w, 0f);
                Hub?.SetActiveTab(target);

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var fromEl = from.el;
                float canvasFrom = _tabCanvasX, canvasTo = target * w * 0.067f; // втрое медленнее — глубина
                to.el.experimental.animation.Start(0f, 1f, LvnMotion.Ms(338), (e, p) => // 260 + 30% — «чуть медленнее» (26.08)
                {
                    float k = 1f - Mathf.Pow(1f - p, 3f);
                    e.style.translate = new Translate(Mathf.Lerp(dir * w, 0f, k), 0f);
                    if (fromEl != null)
                        fromEl.style.translate = new Translate(Mathf.Lerp(0f, -dir * w, k), 0f);
                    _tabCanvasX = Mathf.Lerp(canvasFrom, canvasTo, k); // полотно едет с нами
                    OnTabTravelTick?.Invoke(k); // сцена меню — той же кривой
                    if (_canvasTint != null)
                        _canvasTint.style.backgroundColor = Color.Lerp(
                            TabTints[Mathf.Clamp(_tab, 0, 3)], TabTints[Mathf.Clamp(target, 0, 3)], k);
                    if (p >= 1f) tcs.TrySetResult(true);
                });
                await tcs.Task;
                _tabCanvasX = canvasTo;

                if (from.scr != null) from.scr.HideAsTab();
                else if (fromEl != null) fromEl.style.display = DisplayStyle.None;
                if (fromEl != null) fromEl.style.translate = new Translate(0f, 0f);
                to.el.style.translate = new Translate(0f, 0f);
                _tab = target;
            }
            finally { _tabBusy = false; }
        }

        /// <summary>Мгновенно домой (гардероб/старт главы): без анимации.</summary>
        public void TabReset()
        {
            var from = TabPage(_tab);
            if (from.scr != null) from.scr.HideAsTab();
            var home = TabPage(0);
            if (home.el != null)
            {
                home.el.style.display = DisplayStyle.Flex;
                home.el.style.translate = new Translate(0f, 0f);
            }
            _tab = 0;
            _tabCanvasX = 0f;
            Hub?.SetActiveTab(0, instant: true);
        }

        private void Add(VisualElement el)
        {
            LvnChrome.Stretch(el);
            _root.Add(el);
        }

        private void ShowOnly()
        {
            Hide(Boot); Hide(Carousel); Hide(Hub); Hide(Loading); Hide(Title); Hide(Hud);
            Auth?.Hide();
            Settings?.Hide();
            Detail?.Hide(); Gallery?.Hide(); Profile?.Hide(); Daily?.Hide();
            SkinShop?.Hide(); PackShop?.Hide(); PackShopModal?.Hide();
        }

        private static void Show(VisualElement el) { if (el != null) el.style.display = DisplayStyle.Flex; }

        private static void Hide(VisualElement el) { if (el != null) el.style.display = DisplayStyle.None; }
    }
}
