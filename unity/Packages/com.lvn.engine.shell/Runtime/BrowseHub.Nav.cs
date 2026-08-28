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
            // Карточки несут названия новелл и подзаголовки — они тоже данные,
            // и на главной их видно первыми.
            Refresh();
            // Подсветку возвращаем на место: переодевание меняет ПОДПИСИ, а не
            // то, где игрок находится. Пересборка навбара сбрасывала её на
            // «Главную» — со стороны это выглядело как переход, которого он не
            // делал.
            SetActiveTab(_activeTab, instant: true);
        }

        // Подпись вкладки по её месту: перевод сильнее авторского поля, оно
        // сильнее умолчания. Раньше подписи стояли строками прямо в сборке, и
        // обновить их было негде.
        private string NavLabel(int index)
        {
            switch (index)
            {
                case 0: return LvnWords.Pick("nav.home", _cfg.nav_home, "Home");
                case 1: return LvnWords.Pick("nav.store", _cfg.nav_store, "Store");
                case 2: return LvnWords.Pick("nav.wardrobe", _cfg.nav_wardrobe, "Wardrobe");
                case 3: return LvnWords.Pick("nav.profile", _cfg.nav_profile, "Profile");
                case 4: return LvnWords.Pick("nav.gallery", _cfg.nav_gallery, "Gallery");
                default: return "";
            }
        }

        private VisualElement BottomNav()
        {
            var nav = new VisualElement();
            _bottomNav = nav;
            nav.style.flexDirection = FlexDirection.Row;
            nav.style.alignItems = Align.Stretch;
            nav.style.flexShrink = 0;
            nav.style.paddingBottom = 8; nav.style.paddingTop = 8;
            nav.style.borderTopWidth = _theme.EdgeWidth > 0f ? _theme.EdgeWidth : 1f;
            nav.style.borderTopColor = _theme.EdgeWidth > 0f ? _theme.EdgeColor : _border;
            // Панель непрозрачна: под ней проезжает лента, и полупрозрачный низ
            // превращается в кашу из букв.
            nav.style.backgroundColor = new Color(_bg.r, _bg.g, _bg.b, 0.96f);
            // Callbacks are read LAZILY at click time — the host wires them AFTER
            // this is built, so capturing the field value here would capture null.
            nav.Add(NavTab(0, LvnIcon.Home, NavLabel(0),
                () => OnHomeNav?.Invoke()));
            nav.Add(NavTab(1, LvnIcon.Store, NavLabel(1), () => { if (OnStore != null) LvnAsync.Fire(OnStore(), "OpenStore"); }));
            nav.Add(NavTab(2, LvnIcon.Wardrobe, NavLabel(2), () => { if (OnWardrobe != null) LvnAsync.Fire(OnWardrobe(), "OpenWardrobe"); }));
            if (_cfg.show_gallery ?? true)
                nav.Add(NavTab(4, LvnIcon.Gallery, NavLabel(4), () => { if (OnGallery != null) LvnAsync.Fire(OnGallery(), "OpenGallery"); }));
            nav.Add(NavTab(3, LvnIcon.Profile, NavLabel(3), () => { if (OnProfile != null) LvnAsync.Fire(OnProfile(), "OpenProfile"); }));
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
            tab.style.paddingTop = 6; tab.style.paddingBottom = 6;
            const bool active = false; // подсветку ведёт SetActiveTab

            // Активную вкладку помечает ЧЕРТА СВЕРХУ, а не только цвет: черта
            // читается боковым зрением и не теряется у тех, кто не различает
            // акцент и приглушённый на глаз.
            var mark = new VisualElement { pickingMode = PickingMode.Ignore };
            mark.style.height = 3; mark.style.width = 26;
            mark.style.backgroundColor = Color.clear;
            mark.style.marginBottom = 6;
            tab.Add(mark);

            var iconSlot = new VisualElement { pickingMode = PickingMode.Ignore };
            var iconEl = LvnIcons.Make(icon, 30f, _dim, 0f, 0f);
            iconSlot.Add(iconEl);
            tab.Add(iconSlot);
            var lb = new Label(_theme.Heading(label)) { pickingMode = PickingMode.Ignore };
            lb.style.fontSize = 26; lb.style.color = _dim; lb.style.marginTop = 5;
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
