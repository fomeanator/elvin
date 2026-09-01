using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// НИЖНЯЯ НАВИГАЦИЯ ХАБА — четыре вкладки и переезд между ними.
    ///
    /// <para>Вкладка это не кнопка со страницей: переезд ведёт СРАЗУ ТРОИХ —
    /// страницы, подчёркивание и полотно сцены за интерфейсом, — и все трое
    /// обязаны ехать одной кривой и одним таймером. Своя анимация у каждого
    /// давала рассинхрон, который «бросается в глаза» (живой репорт).</para>
    /// </summary>
    public sealed partial class BrowseHub
    {
        /// <summary>
        /// Слова или шрифт сменились. Подписи вкладок обновляются НА МЕСТЕ, без
        /// пересборки: навбар живёт под всеми экранами и держит подсветку
        /// активной вкладки — снеси его, и подсветка начнёт переезжать заново,
        /// а игрок увидит, как нижнее меню моргает при каждой смене настройки.
        /// </summary>
        public void Redress()
        {
            for (int i = 0; i < _navTabs.Count; i++)
            {
                var t = _navTabs[i];
                if (t?.Label == null) continue;
                t.Label.text = _theme.Heading(NavLabel(t.Index));
                t.Label.style.unityFontStyleAndWeight = LvnFonts.UiWeightStyle;
            }
            if (_hubTitle != null)
                _hubTitle.text = _theme.Heading(LvnWords.Pick("browse.title", _cfg?.title, ""));
            if (_hubEyebrow != null) _hubEyebrow.text = HubEyebrow();
            // Карточки пересобирает сам хаб при следующем показе: трогать их
            // отсюда значило бы знать, из каких данных они собраны.
            // Подсветку возвращаем на место: переодевание меняет ПОДПИСИ, а не
            // то, где игрок находится. Пересборка навбара сбрасывала её на
            // «Главную» — со стороны это выглядело как переход, которого он не
            // делал.
            SetActiveTab(_activeTab, instant: true);
        }

        // Подпись вкладки — у набора: правило «перевод сильнее авторского
        // поля, оно сильнее умолчания» одно на все пять, и жило оно тут
        // пятикратно переписанным.
        private string NavLabel(int index) => LvnTabs.Label(index, _cfg);

        // ЧТО ВКЛАДКА ДЕЛАЕТ — единственное, что тут и правда дело хаба.
        // Обработчики читаются ЛЕНИВО, в момент нажатия: хозяин привязывает их
        // ПОСЛЕ сборки, и захваченное здесь значение было бы null.
        private System.Action TabAction(int index)
        {
            switch (index)
            {
                case LvnTabs.Home: return () => OnHomeNav?.Invoke();
                case LvnTabs.Store: return () => { if (OnStore != null) LvnAsync.Fire(OnStore(), "OpenStore"); };
                case LvnTabs.Wardrobe: return () => { if (OnWardrobe != null) LvnAsync.Fire(OnWardrobe(), "OpenWardrobe"); };
                case LvnTabs.Gallery: return () => { if (OnGallery != null) LvnAsync.Fire(OnGallery(), "OpenGallery"); };
                case LvnTabs.Profile: return () => { if (OnProfile != null) LvnAsync.Fire(OnProfile(), "OpenProfile"); };
                default: return null;
            }
        }

        private VisualElement BottomNav()
        {
            var nav = new VisualElement();
            _bottomNav = nav;
            nav.style.flexDirection = FlexDirection.Row;
            nav.style.alignItems = Align.Stretch;
            nav.style.flexShrink = 0;
            LvnAir.PadY(nav, LvnTokens.Space1);
            LvnChrome.EdgeOn(nav, LvnSide.Top,
                _theme.EdgeWidth > 0f ? _theme.EdgeColor : _border,
                _theme.EdgeWidth > 0f ? _theme.EdgeWidth : 1f);
            // Панель непрозрачна: под ней проезжает лента, и полупрозрачный низ
            // превращается в кашу из букв.
            nav.style.backgroundColor = UiColor.WithAlpha(_bg, 0.96f);
            // Callbacks are read LAZILY at click time — the host wires them AFTER
            // this is built, so capturing the field value here would capture null.
            // Ряд идёт по НАБОРУ (LvnTabs.Shown), а не по руке: место, значок
            // и подпись у вкладки одни на всё приложение. Здесь остаётся
            // только то, что и правда дело хаба, — что вкладка ДЕЛАЕТ.
            foreach (var tab in LvnTabs.Shown)
            {
                if (tab.Index == LvnTabs.Gallery && !(_cfg.show_gallery ?? true)) continue;
                nav.Add(NavTab(tab.Index, tab.Icon, NavLabel(tab.Index), TabAction(tab.Index)));
            }
            SetActiveTab(0, instant: true);
            return nav;
        }

        // Табы с живой подсветкой: прошлый гаснет фейдом, новый загорается
        // (решение Ильи 26.08 — раньше «активная» была захардкожена).
        private sealed class TabRef
        {
            public int Index;
            public LvnIcon Icon;
            public VisualElement Root, Mark, IconSlot, IconEl;
            public Label Label;
            /// <summary>Каким цветом вкладка покрашена сейчас — переход идёт
            /// ОТ него, иначе каждый переезд начинался бы с чужого цвета.</summary>
            public Color Painted;
        }
        private readonly List<TabRef> _navTabs = new List<TabRef>();
        private int _activeTab;

        /// <summary>Подсветить вкладку: прошлая гаснет фейдом, новая
        /// загорается. Зовёт навигатор ленты оболочки.</summary>
        public void SetActiveTab(int index, bool instant = false)
        {
            _activeTab = index;
            foreach (var t in _navTabs)
            {
                bool on = t.Index == index;
                var to = on ? _accent : _dim;
                var from = t.Painted;
                t.Painted = to;
                t.Mark.style.backgroundColor = on ? _accent : Color.clear;
                t.Label.style.unityFontStyleAndWeight = on ? FontStyle.Bold : FontStyle.Normal;
                float glow = on ? _theme.IconGlow : 0f;

                // ВКЛАДКА БОЛЬШЕ НЕ МИГАЕТ. Раньше здесь стояло «полфейда вниз
                // → перекраска → полфейда вверх»: значок нельзя было
                // перекрасить, его пересоздавали, и подмену прикрывали
                // гашением всей вкладки. Игрок видел не переход, а моргание.
                // Теперь значок перекрашивается НА МЕСТЕ (LvnIcons.Tint), и
                // переход — это переход цвета, а не исчезновение кнопки.
                if (instant || from == to)
                {
                    t.Label.style.color = to;
                    LvnIcons.Tint(t.IconEl, to, glow);
                    continue;
                }
                t.Root.experimental.animation.Start(0f, 1f, LvnMotion.Ms(LvnMotion.Normal), (e, p) =>
                {
                    var c = Color.Lerp(from, to, p);
                    t.Label.style.color = c;
                    LvnIcons.Tint(t.IconEl, c, glow);
                });
            }
        }

        private VisualElement NavTab(int index, LvnIcon icon, string label, System.Action onTap)
        {
            var tab = new VisualElement();
            // РАВНЫЕ ДОЛИ, а не распределение по содержимому. Раньше здесь стояло
            // justify-content: space-around при вкладках разной ширины — и
            // «Главная» с «Гардеробом» разъезжались тем сильнее, чем длиннее
            // слово. Одинаковый flex-basis выравнивает центры, а центры и есть
            // то, по чему глаз читает ряд как ряд.
            tab.style.flexGrow = 1; tab.style.flexBasis = 0;
            tab.style.alignItems = Align.Center;
            tab.style.justifyContent = Justify.FlexStart;
            LvnAir.PadY(tab, LvnTokens.Space1);
            const bool active = false; // подсветку ведёт SetActiveTab

            // Активную вкладку помечает ЧЕРТА СВЕРХУ, а не только цвет: черта
            // читается боковым зрением и не теряется у тех, кто не различает
            // акцент и приглушённый на глаз.
            var mark = new VisualElement { pickingMode = PickingMode.Ignore };
            mark.style.height = 3; mark.style.width = 26;
            mark.style.backgroundColor = Color.clear;
            mark.style.marginBottom = LvnTokens.Space1;
            tab.Add(mark);

            var iconSlot = new VisualElement { pickingMode = PickingMode.Ignore };
            var iconEl = LvnIcons.Make(icon, 30f, _dim, 0f, 0f);
            iconSlot.Add(iconEl);
            tab.Add(iconSlot);
            var lb = new Label(_theme.Heading(label)) { pickingMode = PickingMode.Ignore };
            lb.style.fontSize = LvnTokens.TextSm; lb.style.color = _dim; lb.style.marginTop = LvnTokens.Tight;
            lb.style.letterSpacing = _theme.Tracking;
            tab.Add(lb);
            if (onTap != null) { tab.AddManipulator(new Clickable(onTap)); LvnMotion.Tappable(tab); }
            _navTabs.Add(new TabRef
            {
                Index = index, Icon = icon, Root = tab, Mark = mark,
                IconSlot = iconSlot, IconEl = iconEl, Label = lb, Painted = _dim,
            });
            return tab;
        }

        // ЧИСТЫЙ ФЕЙД строк, без сдвига (решение Ильи 26.08): rise-хореография
        // переигрывалась при асинхронных перестройках ленты по УЖЕ видимому
        // контенту — «элементы задираются и съезжают». Фейд повторяться может
        // безболезненно, а появление читается как у актёров и диалога.
    }
}
