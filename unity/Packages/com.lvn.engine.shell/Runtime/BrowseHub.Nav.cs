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
                case LvnTabs.Gacha: return () => { if (OnGacha != null) LvnAsync.Fire(OnGacha(), "OpenGacha"); };
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

        /// <summary>ВЫСОТА НИЖНЕЙ ЛЕНТЫ — её спрашивают экраны, встающие над
        /// ней (лист гардероба). Числом её не задать: у облика «сцена» лента
        /// рисованная и выше обычной, и жёсткий отступ у гардероба перестал
        /// сходиться — кнопки «Отменить/Выбрать» уехали под меню (скрин Ильи
        /// 08.09). Спрашиваем у самой ленты, сколько она заняла.</summary>
        public float NavHeight
            => _bottomNav == null || _bottomNav.style.display == DisplayStyle.None
                ? 0f : _bottomNav.resolvedStyle.height;

        // Место вкладки в пространстве витрины — КАРТА КОМНАТ (LvnTabs.Room),
        // а не измерение ленты: здесь стоял TabSeat, меривший worldBound
        // кнопок и строивший ромб «кнопка выше — комната выше». Он ходил
        // поперёк глаза и дрожал на пикселях раскладки; композиция — решение,
        // и она записана словами у вкладок.

        /// <summary>Запасное место — по порядку показа: пока лента не
        /// разложена, спрашивать у неё геометрию нечего.</summary>
        private static float SeatByOrder(int index)
        {
            int n = 0, mine = -1;
            for (int i = 0; i < LvnTabs.Shown.Count; i++)
            {
                if (LvnTabs.Shown[i].Index == index) mine = n;
                n++;
            }
            if (mine < 0 || n <= 1) return 0.5f;
            return (mine + 0.5f) / n;

        }

        /// <summary>СПРЯТАТЬ ИЛИ ВЕРНУТЬ НИЖНЕЕ МЕНЮ. Зовёт оболочка, когда экран
        /// просит кадр целиком («Во весь рост» в гардеробе) и в комнате круток.
        /// Плавно: уезжает вниз и гаснет («чтобы меню плавно вниз уезжало,
        /// исчезая» — Илья 15.09), возвращается тем же путём.
        /// <paramref name="instant"/> — без движения: старт главы, сброс ленты.</summary>
        public void SetNavHidden(bool hidden, bool instant = false, int ms = 0)
        {
            if (_bottomNav == null) return;
            // УЖЕ ТАМ — НЕ ТРОГАТЬ. Гардероб на каждом открытии и закрытии
            // снимает «Во весь рост» и просит вернуть меню; после комнаты
            // круток «вернуть» стало въездом снизу, и он проигрывался поверх
            // стоящего меню — «меню скачет при переходе в гардероб и назад»
            // (Илья 16.09, TR-128). Мгновенный вызов дожимает начатое движение.
            if (hidden == _navHidden && !instant) return;
            _navHidden = hidden;
            int v = ++_navMotion;
            if (instant || LvnPrefs.ReduceMotion)
            {
                _bottomNav.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;
                _bottomNav.style.opacity = 1f;
                _bottomNav.style.translate = new Translate(0f, 0f);
                return;
            }
            if (!hidden)
            {
                // Первый кадр — уже внизу и невидимо: иначе меню мигнёт на месте.
                _bottomNav.style.opacity = 0f;
                _bottomNav.style.translate = new Translate(0f, Length.Percent(120f));
                _bottomNav.style.display = DisplayStyle.Flex;
            }
            LvnAsync.Fire(SlideNavAsync(hidden, v, ms > 0 ? ms : NavSlideMs), "NavSlide");
        }

        private int _navMotion;
        private bool _navHidden;
        /// <summary>Своё время ухода — когда меню едет не с перелётом (гардероб
        /// «Во весь рост»); с перелётом ему отдают время перелёта.</summary>
        private const int NavSlideMs = 420;

        private async System.Threading.Tasks.Task SlideNavAsync(bool hidden, int v, int ms)
        {
            float from = hidden ? 0f : 1f, to = hidden ? 1f : 0f;   // доля ухода вниз
            // Кривая ПОЛЁТА, как у перелёта между комнатами: одно движение с
            // приходящей комнатой, а не своё «дёрнулось и село» («плавнее» —
            // Илья 16.09).
            await LvnMotion.PlayAsync(_bottomNav, ms, (el, p) =>
            {
                if (v != _navMotion) return;
                float k = Mathf.Lerp(from, to, LvnMotion.Glide(p));
                el.style.opacity = 1f - k;
                el.style.translate = new Translate(0f, Length.Percent(120f * k));
            });
            if (v != _navMotion) return;
            if (hidden) ScreenFx.PutAway(_bottomNav);
            else { _bottomNav.style.opacity = 1f; _bottomNav.style.translate = new Translate(0f, 0f); }
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
                // У рисованного меню (облик «сцена») ни черты, ни значка нет —
                // значки нарисованы в картинке, а центральная кнопка держит
                // своё золото и не красится вовсе.
                if (t.Mark != null) t.Mark.style.backgroundColor = on ? _accent : Color.clear;
                if (t.Label != null) t.Label.style.unityFontStyleAndWeight = on ? FontStyle.Bold : FontStyle.Normal;
                if (t.Label == null && t.IconEl == null) continue;
                float glow = on ? _theme.IconGlow : 0f;

                // ВКЛАДКА БОЛЬШЕ НЕ МИГАЕТ. Раньше здесь стояло «полфейда вниз
                // → перекраска → полфейда вверх»: значок нельзя было
                // перекрасить, его пересоздавали, и подмену прикрывали
                // гашением всей вкладки. Игрок видел не переход, а моргание.
                // Теперь значок перекрашивается НА МЕСТЕ (LvnIcons.Tint), и
                // переход — это переход цвета, а не исчезновение кнопки.
                if (instant || from == to)
                {
                    if (t.Label != null) t.Label.style.color = to;
                    if (t.IconEl != null) LvnIcons.Tint(t.IconEl, to, glow);
                    continue;
                }
                t.Root.experimental.animation.Start(0f, 1f, LvnMotion.Ms(LvnMotion.Normal), (e, p) =>
                {
                    var c = Color.Lerp(from, to, p);
                    if (t.Label != null) t.Label.style.color = c;
                    if (t.IconEl != null) LvnIcons.Tint(t.IconEl, c, glow);
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
