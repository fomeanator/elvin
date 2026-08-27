using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЕДИНЫЙ НАВБАР приложения (решение Ильи 26.08): один верхний бар и в
    /// меню, и в игре — вместо «мусорки из разных навбаров у всех» (пилюли
    /// хаба, фабы сцены, отдельный кружок). Полупрозрачная подложка с нижней
    /// кромкой; слева лого «Т» в кружке (вектор кодом), центр отдан кружку
    /// загрузок (DownloadHud — отдельный оверлей, позиционируется в эту же
    /// строку), справа валюты БЕЗ «+» (тап по пилюле открывает магазин) и
    /// бургер.
    ///
    /// <para>ИГРОВОЙ РЕЖИМ: в сцене бар скрыт полностью (чистый кадр) и
    /// появляется тремя путями — тапом по верхней кромке (невидимая
    /// полоска-ловушка), САМИМ СОБЫТИЕМ (пошла загрузка/пропала сеть — «а как
    /// же по событиям»), и прячется через паузу тишины.</para>
    ///
    /// <para>ШТОРКИ/ЧЁЛКИ: бар отступает на высоту safe area (хост кормит
    /// <see cref="SetSafeTop"/>), поэтому вырез камеры всегда ВЫШЕ бара и
    /// центр строки безопасен — как делают все крупные мобильные игры.</para>
    /// </summary>
    public sealed class LvnTopBar : VisualElement
    {
        /// <summary>Высота ряда навбара — публична: экраны, встающие «под
        /// навбаром» (колонка эмоций гардероба), считают от неё.</summary>
        public const float RowH = 76f;

        /// <summary>Валюты пилюль (id кошелька), порядок = порядок на баре.</summary>
        public List<string> Currencies = new List<string>();
        /// <summary>Тап по пилюле валюты — хост открывает магазин.</summary>
        public Action<string> OnCurrency;
        /// <summary>Бургер: в сцене — квик-меню, в меню — настройки.</summary>
        public Action OnBurger;
        /// <summary>Игровые кнопки выезжающего бара (решение Ильи 26.08):
        /// выход в меню, история, гардероб, магазин.</summary>
        public Action OnGameExit, OnGameHistory, OnGameWardrobe, OnGameStore;

        /// <summary>Свободна ли верхняя тап-зона: шелл живёт НАД документом
        /// сцены, и с открытой панелью (история, квик-меню) ловушка глотала
        /// её шапку (живой скрин «историю не закрыть»). Хост отдаёт сюда
        /// «в сцене нет открытого UI»; синк — тиком оболочки.</summary>
        public Func<bool> TapZoneAvailable;

        public void SyncTapZone()
        {
            bool free = TapZoneAvailable?.Invoke() ?? true;
            bool on = _inGame && !_silent && !_gameBarShown && free;
            _tapCatcher.pickingMode = on ? PickingMode.Position : PickingMode.Ignore;
            // ДОКТРИНА СЛОЁВ (решение Ильи 26.08): модаль сцены (квик-меню,
            // история, статы) на время жизни подавляет немодальный декор
            // оболочки — баблики прячутся, развёрнутый игровой бар сворачивается.
            // Модали оболочки (магазин, попапы) — осознанно поверх всего.
            bool modal = _inGame && !free;
            if (modal && _gameBarShown) ToggleGameBar(false);
            var vis = modal ? Visibility.Hidden : Visibility.Visible;
            _miniPills.style.visibility = vis;
            _miniProgress.style.visibility = vis;
        }

        private readonly VisualElement _row;
        private readonly VisualElement _pills;
        private readonly VisualElement _miniPills; // игровые баблики валют
        private readonly VisualElement _miniProgress; // баблик прогресса главы
        private readonly Label _miniProgressLabel;
        private VisualElement _gameRow;   // выезжающий игровой бар (4 кнопки)
        private bool _gameBarShown;
        private readonly VisualElement _tapCatcher;
        // Режим не хранится: одна правда у Режиссёра, бар лишь одевается по ней.
        private bool _inGame => Lvn.UI.LvnScreenDirector.Current.InChapter;
        private float _safeTop;

        public LvnTopBar()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0;

            // Ловушка тапа по верхней кромке — активна только в игре при
            // скрытом баре. Ниже её 48 юнитов сцена живёт как обычно.
            _tapCatcher = new VisualElement();
            _tapCatcher.style.position = Position.Absolute;
            _tapCatcher.style.left = 0; _tapCatcher.style.right = 0;
            _tapCatcher.style.top = 0; _tapCatcher.style.height = 48;
            _tapCatcher.style.display = DisplayStyle.None;
            _tapCatcher.style.height = Length.Percent(15f); // верхние 15% экрана
            _tapCatcher.RegisterCallback<PointerDownEvent>(e =>
            {
                // Тап по верхней зоне сцены НЕ листает реплику — только зовёт
                // игровой бар (решение Ильи 26.08).
                e.StopPropagation();
                ToggleGameBar(true);
            });
            Add(_tapCatcher);

            _row = new VisualElement();
            var bg = LvnTokens.PanelBg;
            _row.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.62f);
            _row.style.borderBottomWidth = 1f;
            _row.style.borderBottomColor = LvnTokens.Border;
            _row.style.height = RowH;
            _row.style.flexDirection = FlexDirection.Row;
            _row.style.alignItems = Align.Center;
            _row.style.paddingLeft = 12; _row.style.paddingRight = 12;
            Add(_row);

            _row.Add(Logo());

            var spacer = new VisualElement();
            spacer.pickingMode = PickingMode.Ignore;
            spacer.style.flexGrow = 1;
            _row.Add(spacer);

            _pills = new VisualElement();
            _pills.style.flexDirection = FlexDirection.Row;
            _pills.style.alignItems = Align.Center;
            _row.Add(_pills);

            _row.Add(Burger());

            // ИГРОВОЙ РЕЖИМ (уточнение Ильи 26.08): бар в сцене пропадает
            // целиком, а валюты живут МИНИ-БАБЛИКАМИ у правого края — свой
            // пузырёк на каждую, без общей подложки. Кружок загрузок — такой
            // же баблик слева (DownloadHud сам).
            _miniPills = new VisualElement();
            _miniPills.style.position = Position.Absolute;
            _miniPills.style.top = 8;
            _miniPills.style.right = 12;
            _miniPills.style.flexDirection = FlexDirection.Row;
            _miniPills.style.display = DisplayStyle.None;
            Add(_miniPills);

            // Прогресс главы — такой же пузырёк слева (замена полосе GameHud).
            _miniProgress = new VisualElement();
            _miniProgress.style.position = Position.Absolute;
            _miniProgress.style.top = 8;
            _miniProgress.style.left = 12;
            _miniProgress.style.height = 42;
            _miniProgress.style.paddingLeft = 13; _miniProgress.style.paddingRight = 13;
            _miniProgress.style.justifyContent = Justify.Center;
            var pbg = LvnTokens.PanelBg;
            _miniProgress.style.backgroundColor = new Color(pbg.r, pbg.g, pbg.b, 0.72f);
            LvnChrome.Edge(_miniProgress);
            LvnChrome.Round(_miniProgress, 21f);
            _miniProgress.style.display = DisplayStyle.None;
            _miniProgress.pickingMode = PickingMode.Ignore;
            _miniProgressLabel = new Label("0%");
            _miniProgressLabel.pickingMode = PickingMode.Ignore;
            _miniProgressLabel.style.color = LvnTokens.Text;
            _miniProgressLabel.style.fontSize = 21;
            _miniProgressLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _miniProgress.Add(_miniProgressLabel);
            Add(_miniProgress);

            // Выезжающий игровой бар: 4 кнопки — выход в меню, история,
            // гардероб, магазин. Открывается тапом по верхней зоне сцены,
            // закрывается повторным тапом по зоне/кнопкой.
            _gameRow = new VisualElement();
            var gbg = LvnTokens.PanelBg;
            _gameRow.style.position = Position.Absolute;
            _gameRow.style.left = 0; _gameRow.style.right = 0; _gameRow.style.top = 0;
            _gameRow.style.backgroundColor = new Color(gbg.r, gbg.g, gbg.b, 0.9f);
            _gameRow.style.borderBottomWidth = 1f;
            _gameRow.style.borderBottomColor = LvnTokens.Border;
            _gameRow.style.flexDirection = FlexDirection.Row;
            _gameRow.style.alignItems = Align.Center;
            _gameRow.style.justifyContent = Justify.SpaceAround;
            _gameRow.style.paddingTop = 10; _gameRow.style.paddingBottom = 12;
            _gameRow.style.paddingLeft = 8; _gameRow.style.paddingRight = 8;
            _gameRow.style.display = DisplayStyle.None;
            _gameRow.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _gameRow.Add(GameButton(LvnIcon.Home, "Выйти в меню", () => { ToggleGameBar(false); OnGameExit?.Invoke(); }));
            _gameRow.Add(GameButton(LvnIcon.Book, "История", () => { ToggleGameBar(false); OnGameHistory?.Invoke(); }));
            _gameRow.Add(GameButton(LvnIcon.Wardrobe, "Гардероб", () => { ToggleGameBar(false); OnGameWardrobe?.Invoke(); }));
            _gameRow.Add(GameButton(LvnIcon.Store, "Магазин", () => { ToggleGameBar(false); OnGameStore?.Invoke(); }));
            Add(_gameRow);

            RefreshBalances();
        }

        private VisualElement GameButton(LvnIcon icon, string label, Action onTap)
        {
            var b = new VisualElement();
            b.style.alignItems = Align.Center;
            b.style.paddingTop = 6; b.style.paddingBottom = 6;
            b.style.paddingLeft = 14; b.style.paddingRight = 14;
            LvnChrome.Round(b, 12f);
            var ic = LvnIcons.Make(icon, 28f, LvnTokens.Accent, 0f, LvnTheme.Current.IconGlow);
            ic.pickingMode = PickingMode.Ignore;
            b.Add(ic);
            var l = new Label(label);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.Text;
            l.style.fontSize = 19;
            l.style.marginTop = 5;
            b.Add(l);
            b.RegisterCallback<ClickEvent>(_ => onTap());
            return b;
        }

        private void ToggleGameBar(bool? force = null)
        {
            bool show = force ?? !_gameBarShown;
            if (show == _gameBarShown && force == null) return;
            _gameBarShown = show;
            float slide = RowH + _safeTop + 150f;
            if (show)
            {
                // ПОЛНЫЙ навбар (лого/валюты/бургер) + строка кнопок ПОД ним —
                // ансамблем сверху; баблики на это время прячутся (дубль).
                _miniPills.style.display = DisplayStyle.None;
                _miniProgress.style.display = DisplayStyle.None;
                _row.style.display = DisplayStyle.Flex;
                _gameRow.style.top = _safeTop + RowH;
                _gameRow.style.paddingTop = 10;
                _gameRow.style.display = DisplayStyle.Flex;
                _row.style.translate = new Translate(0f, -slide);
                _gameRow.style.translate = new Translate(0f, -slide);
                _row.experimental.animation.Start(0f, 1f, 240, (r, v) =>
                {
                    float k = 1f - Mathf.Pow(1f - v, 3f);
                    var y = Mathf.Lerp(-slide, 0f, k);
                    r.style.translate = new Translate(0f, y);
                    _gameRow.style.translate = new Translate(0f, y);
                });
                // Автоуход через 5 с тишины — сцена остаётся чистой.
                schedule.Execute(() => { if (_gameBarShown) ToggleGameBar(false); }).ExecuteLater(5000);
            }
            else
            {
                _row.experimental.animation.Start(0f, 1f, 200, (r, v) =>
                {
                    float k = 1f - Mathf.Pow(1f - v, 3f);
                    var y = Mathf.Lerp(0f, -slide, k);
                    r.style.translate = new Translate(0f, y);
                    _gameRow.style.translate = new Translate(0f, y);
                    if (v >= 1f)
                    {
                        r.style.display = DisplayStyle.None;
                        _gameRow.style.display = DisplayStyle.None;
                        if (_inGame)
                        {
                            _miniPills.style.display = DisplayStyle.Flex;
                            _miniProgress.style.display = DisplayStyle.Flex;
                            _row.style.translate = new Translate(0f, 0f);
                        }
                    }
                });
            }
        }

        // ── содержимое ────────────────────────────────────────────────────────

        // Лого: «Т» в акцентном кружке — вектор кодом, без ассетов.
        private VisualElement Logo()
        {
            var circle = new VisualElement();
            circle.pickingMode = PickingMode.Ignore;
            circle.style.width = 50; circle.style.height = 50;
            LvnChrome.Round(circle, 25f);
            circle.style.backgroundColor = LvnTokens.Accent;
            circle.style.alignItems = Align.Center;
            circle.style.justifyContent = Justify.Center;
            var t = new Label("Т");
            t.pickingMode = PickingMode.Ignore;
            t.style.color = LvnTokens.OnAccent;
            t.style.fontSize = 30;
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            circle.Add(t);
            return circle;
        }

        // Бургер — три полоски (глиф «☰» на Android — tofu, грабля уже ловлена).
        private VisualElement Burger()
        {
            var b = new VisualElement();
            b.style.width = 52; b.style.height = 52;
            b.style.marginLeft = 10;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            LvnChrome.Round(b, 12f);
            b.style.backgroundColor = LvnTokens.Faint;
            for (int i = 0; i < 3; i++)
            {
                var bar = new VisualElement();
                bar.pickingMode = PickingMode.Ignore;
                bar.style.width = 20; bar.style.height = 2.5f;
                bar.style.marginTop = i == 0 ? 0 : 4;
                bar.style.backgroundColor = LvnTokens.Text;
                b.Add(bar);
            }
            b.RegisterCallback<ClickEvent>(_ => OnBurger?.Invoke());
            b.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            return b;
        }

        /// <summary>Перерисовать пилюли валют из живого кошелька. Без «+» —
        /// тап по самой пилюле открывает магазин.</summary>
        public void RefreshBalances()
        {
            FillPills(_pills, compact: false);
            FillPills(_miniPills, compact: true);
        }

        private void FillPills(VisualElement host, bool compact)
        {
            host.Clear();
            var bg = LvnTokens.PanelBg;
            foreach (var cur in Currencies)
            {
                var captured = cur;
                host.Add(new LvnWalletPill(cur, new LvnWalletPill.Look
                {
                    MarginLeft = compact ? 6 : 8,
                    Height = compact ? 42 : 46,
                    Radius = compact ? 21f : 23f,
                    IconSize = compact ? 19f : 20f,
                    FontSize = 21f,
                    Bold = true,
                    Edge = true,
                    // Над сценой у каждой валюты свой пузырёк, в меню — общий
                    // ряд на приглушённой подложке бара.
                    Background = compact ? new Color(bg.r, bg.g, bg.b, 0.72f) : LvnTokens.Faint,
                }, onTap: () => OnCurrency?.Invoke(captured)));
            }
        }

        // ── режимы ────────────────────────────────────────────────────────────

        /// <summary>Высота безопасной зоны сверху в юнитах панели — бар и его
        /// содержимое опускаются ПОД вырез камеры.</summary>
        public void SetSafeTop(float units)
        {
            if (Mathf.Approximately(_safeTop, units)) return;
            _safeTop = units;
            _row.style.marginTop = units;
            _miniPills.style.top = units + 8f;
            _miniProgress.style.top = units + 8f;
            _tapCatcher.style.height = 48 + units;
        }

        /// <summary>Вход бара: строка выезжает СВЕРХУ (вызов оболочки при
        /// показе меню) — в паре с нижней навигацией хаба.</summary>
        /// <summary>Состояние бара для лога: видно ли его вообще и не застрял
        /// ли он за верхней кромкой (вход анимирует translate).</summary>
        public string DebugState =>
            $"display={_row.resolvedStyle.display} translate={_row.resolvedStyle.translate} "
            + $"opacity={_row.resolvedStyle.opacity:0.00} inGame={_inGame} silent={_silent} "
            + $"rect=({_row.worldBound.y:0} {_row.worldBound.width:0}x{_row.worldBound.height:0})";

        public void PlayEntrance()
        {
            // ПРОЯВЛЕНИЕ НА МЕСТЕ вместо въезда сверху (Илья 26.08: «прыжки
            // убери везде»). Съезжающая строка дёргала верх экрана на каждый
            // показ хаба, а при обрыве анимации бар оставался за кромкой.
            _row.style.translate = new Translate(0f, 0f);
            Lvn.UI.LvnMotion.FadeIn(_row);
        }

        /// <summary>Прогресс главы для левого баблика (та же формула Percent,
        /// что была у полосы GameHud).</summary>
        public void SetProgress(int currentIndex, int totalCommands)
            => _miniProgressLabel.text = Lvn.Content.Percent.Text(currentIndex, totalCommands);

        /// <summary>ВОРОНКА-ИНТРО: полная тишина — ни бабликов, ни тап-зоны,
        /// ни бара. Новичок в кинематографичном прологе не должен случайно
        /// получить «Выйти в меню», которого для него ещё не существует.</summary>
        public void SetSilent(bool silent)
        {
            _silent = silent;
            if (silent)
            {
                _row.style.display = DisplayStyle.None;
                _miniPills.style.display = DisplayStyle.None;
                _miniProgress.style.display = DisplayStyle.None;
                _tapCatcher.style.display = DisplayStyle.None;
                _gameRow.style.display = DisplayStyle.None;
                _gameBarShown = false;
            }
            else SetInGameApply();
        }
        private bool _silent;

        private void SetInGameApply()
        {
            _row.style.display = _inGame ? DisplayStyle.None : DisplayStyle.Flex;
            _miniPills.style.display = _inGame ? DisplayStyle.Flex : DisplayStyle.None;
            _miniProgress.style.display = _inGame ? DisplayStyle.Flex : DisplayStyle.None;
            _tapCatcher.style.display = _inGame ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Игровой режим (уточнение Ильи 26.08): бар в сцене
        /// ПРОПАДАЕТ целиком — вместо него мини-баблики валют (справа) и
        /// кружок загрузок (слева, DownloadHud сам). Ловушка тапа не нужна:
        /// квик-меню открывают фабы сцены.</summary>
        public void SetInGame(bool inGame)
        {
            // БЕЗ РАННЕГО ВЫХОДА ПО ФЛАГУ. «Уже не в игре» не значит «бар на
            // экране»: его прячет ещё и тишина воронки, и любой путь, где
            // Apply не дошёл. Флаг совпал — а бар остался скрытым, и меню
            // открывалось без верхней строки (Илья 26.08). Теперь вызов
            // «мы в меню» всегда доводит вид до состояния.
            Lvn.UI.LvnScreenDirector.Current.SetChapter(inGame);
            if (_silent)
            {
                if (!inGame) _silent = false; // выход в меню снимает тишину
                else return;                   // воронка: остаёмся немыми
            }
            SetInGameApply();
            if (!inGame && _gameBarShown) ToggleGameBar(false);
        }
    }
}
