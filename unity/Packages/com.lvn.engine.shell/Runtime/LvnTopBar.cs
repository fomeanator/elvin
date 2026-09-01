using System;
using System.Collections.Generic;
using Lvn.Content;
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
    public sealed class LvnTopBar : VisualElement, ILvnEntrance
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

            // РЕЖИМ ЭКРАНА — у Режиссёра, как и у кружка загрузок.
            Lvn.LvnLeash.WhileOnScreen(this,
                () => Lvn.UI.LvnScreenDirector.Current.Changed += ApplyChapterMode,
                () => Lvn.UI.LvnScreenDirector.Current.Changed -= ApplyChapterMode,
                ApplyChapterMode);

            // ДЕНЬГИ БАР СЛУШАЕТ САМ — как это делает витрина хаба. Подписку
            // держал хост (LvnWallet.Changed → TopBar.RefreshBalances), и
            // правило выходило разное для двух соседних поверхностей: одна
            // узнаёт о движении денег сама, другая — только если её кормят.
            // Отписка на снятии с панели обязательна: делегат метода экземпляра
            // у пересозданной оболочки другой, и прежняя подписка дёргала бы
            // мёртвое дерево на каждое движение денег.
            Lvn.LvnLeash.WhileOnScreen(this,
                () => Lvn.Services.LvnWallet.Changed += RefreshBalances,
                () => Lvn.Services.LvnWallet.Changed -= RefreshBalances,
                RefreshBalances);

            // ВЫРЕЗ КАМЕРЫ БАР СПРАШИВАЕТ САМ. Раньше его кормил хост: раз в
            // 300 мс мерил кромку и раздавал двум жильцам вызовом SetSafeTop.
            // Пока хост это делал, всё работало; экран без хоста (демо-сцена,
            // песочница) получал бар, наехавший на чёлку, — а причина была не
            // в баре, и искать её приходилось в чужом файле.
            Lvn.UI.LvnEdges.Follow(this, insets => SetSafeTop(insets.x));

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
            _row.style.backgroundColor = UiColor.WithAlpha(bg, 0.62f);
            LvnChrome.Divider(_row);
            _row.style.height = RowH;
            ScreenUi.Row(_row);
            LvnAir.PadX(_row, LvnTokens.Space2);
            Add(_row);

            _row.Add(Logo());

            var spacer = new VisualElement();
            spacer.pickingMode = PickingMode.Ignore;
            spacer.style.flexGrow = 1;
            _row.Add(spacer);

            _pills = new VisualElement();
            ScreenUi.Row(_pills);
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
            LvnAir.PadX(_miniProgress, LvnTokens.Space2);
            _miniProgress.style.justifyContent = Justify.Center;
            var pbg = LvnTokens.PanelBg;
            _miniProgress.style.backgroundColor = UiColor.WithAlpha(pbg, 0.72f);
            LvnChrome.Edge(_miniProgress);
            LvnChrome.Round(_miniProgress, LvnTokens.Radius);
            _miniProgress.style.display = DisplayStyle.None;
            _miniProgress.pickingMode = PickingMode.Ignore;
            _miniProgressLabel = new Label("0%");
            _miniProgressLabel.pickingMode = PickingMode.Ignore;
            _miniProgressLabel.style.color = LvnTokens.Text;
            _miniProgressLabel.style.fontSize = LvnTokens.TextXs;
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
            _gameRow.style.backgroundColor = UiColor.WithAlpha(gbg, 0.9f);
            LvnChrome.Divider(_gameRow);
            ScreenUi.Row(_gameRow);
            _gameRow.style.justifyContent = Justify.SpaceAround;
            LvnAir.PadX(_gameRow, LvnTokens.Space1);
            LvnAir.PadY(_gameRow, LvnTokens.Space2);
            _gameRow.style.display = DisplayStyle.None;
            _gameRow.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            // Подписи игровой панели — через словарь: зашитые по-русски, они делали
            // английскую сборку невозможной, хотя дом для слов давно есть.
            _gameRow.Add(GameButton(LvnIcon.Home, () => LvnWords.Of("game.exit", "Menu"), () => { ToggleGameBar(false); OnGameExit?.Invoke(); }));
            _gameRow.Add(GameButton(LvnIcon.Book, () => LvnWords.Of("game.history", "History"), () => { ToggleGameBar(false); OnGameHistory?.Invoke(); }));
            _gameRow.Add(GameButton(LvnIcon.Wardrobe, () => LvnWords.Of("menu.wardrobe", "Wardrobe"), () => { ToggleGameBar(false); OnGameWardrobe?.Invoke(); }));
            _gameRow.Add(GameButton(LvnIcon.Store, () => LvnWords.Of("menu.store", "Store"), () => { ToggleGameBar(false); OnGameStore?.Invoke(); }));
            Add(_gameRow);

            RefreshBalances();
        }

        // Подпись кнопки берётся ИСТОЧНИКОМ: игровой ряд шапки собирается один
        // раз и живёт всю игру, поэтому строка в нём застыла бы на языке,
        // который стоял в момент сборки.
        private VisualElement GameButton(LvnIcon icon, Func<string> label, Action onTap)
        {
            var b = new VisualElement();
            b.style.alignItems = Align.Center;
            LvnAir.PadX(b, LvnTokens.Space2);
            LvnAir.PadY(b, LvnTokens.Space1);
            LvnChrome.Round(b, LvnTokens.RadiusSm);
            var ic = LvnIcons.Make(icon, 28f, LvnTokens.Accent);
            ic.pickingMode = PickingMode.Ignore;
            b.Add(ic);
            var l = Lvn.UI.LvnRedress.Bind(new Label(), label);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.Text;
            l.style.fontSize = LvnTokens.TextXs;
            l.style.marginTop = 5;
            b.Add(l);
            b.RegisterCallback<ClickEvent>(_ => onTap());
            return b;
        }

        /// <summary>Сколько бар ждёт тишины, прежде чем уйти сам.</summary>
        private const long GameBarQuietMs = 5000;

        // Один отсчёт автоухода на все открытия — заводится при первом.
        private IVisualElementScheduledItem _barAutoHide;

        private void ToggleGameBar(bool? force = null)
        {
            bool show = force ?? !_gameBarShown;
            if (show == _gameBarShown && force == null) return;
            _gameBarShown = show;
            // Закрылись — отсчёт больше не нужен: он разбудится при следующем
            // открытии. Иначе он доживёт до конца и закроет уже чужое открытие.
            if (!show) _barAutoHide?.Pause();
            float slide = RowH + _safeTop + 150f;
            if (show)
            {
                // ПОЛНЫЙ навбар (лого/валюты/бургер) + строка кнопок ПОД ним —
                // ансамблем сверху; баблики на это время прячутся (дубль).
                _miniPills.style.display = DisplayStyle.None;
                _miniProgress.style.display = DisplayStyle.None;
                _row.style.display = DisplayStyle.Flex;
                _gameRow.style.top = _safeTop + RowH;
                _gameRow.style.paddingTop = LvnTokens.Space2;
                _gameRow.style.display = DisplayStyle.Flex;
                _row.style.translate = new Translate(0f, -slide);
                _gameRow.style.translate = new Translate(0f, -slide);
                _row.experimental.animation.Start(0f, 1f, LvnMotion.Ms(LvnMotion.Calm), (r, v) =>
                {
                    float k = LvnMotion.Settle(v);
                    var y = Mathf.Lerp(-slide, 0f, k);
                    r.style.translate = new Translate(0f, y);
                    _gameRow.style.translate = new Translate(0f, y);
                });
                // Автоуход через 5 с ТИШИНЫ — сцена остаётся чистой.
                //
                // Отсчёт один, и он перезапускается. Здесь на каждое открытие
                // заводился новый, а прежние продолжали идти: открыл бар, ушёл в
                // магазин, вернулся и открыл снова — и старый отсчёт захлопывал
                // бар раньше срока, посреди объяснения. Оба смотрели только на
                // «бар открыт?», а не на «моё ли это открытие».
                _barAutoHide ??= schedule.Execute(
                    () => { if (_gameBarShown) ToggleGameBar(false); });
                _barAutoHide.ExecuteLater(GameBarQuietMs);
            }
            else
            {
                _row.experimental.animation.Start(0f, 1f, LvnMotion.Ms(200), (r, v) =>
                {
                    float k = LvnMotion.Settle(v);
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

        // Лого: буква в акцентном кружке — вектор кодом, без ассетов.
        //
        // Буква АВТОРСКАЯ. Здесь стояла «Т» — инициал одной конкретной новеллы,
        // зашитый в движок, который лежит в открытом репозитории и служит любым
        // играм. Умолчание нейтральное, своё автор ставит словом app.logo.
        private VisualElement Logo()
        {
            var circle = new VisualElement();
            circle.pickingMode = PickingMode.Ignore;
            circle.style.width = 50; circle.style.height = 50;
            LvnChrome.Round(circle, LvnTokens.RadiusLg);
            circle.style.backgroundColor = LvnTokens.Accent;
            circle.style.alignItems = Align.Center;
            circle.style.justifyContent = Justify.Center;
            var t = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("app.logo", "L"));
            t.pickingMode = PickingMode.Ignore;
            t.style.color = LvnTokens.OnAccent;
            t.style.fontSize = LvnTokens.TextBase;
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            circle.Add(t);
            return circle;
        }

        // Бургер — три полоски (глиф «☰» на Android — tofu, грабля уже ловлена).
        private VisualElement Burger()
        {
            var b = new VisualElement();
            b.style.width = 52; b.style.height = 52;
            b.style.marginLeft = LvnTokens.Space2;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            LvnChrome.Round(b, LvnTokens.RadiusSm);
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
            // СПИСОК СВЕРЯЕТСЯ, А НЕ ПЕРЕСОБИРАЕТСЯ (правило Монтажёра). Этот
            // же симптом чинили для шапки хаба: ответ кошелька приходит через
            // круговой путь до сервера, и пересборка на каждое изменение
            // выбрасывала живые пилюли вместе с их значками и заново их
            // грузила — при том что пилюля умеет обновлять своё число сама и
            // так тикает раз в секунду. Пересборка нужна ровно тогда, когда
            // сменился САМ СПИСОК валют.
            var bg = LvnTokens.PanelBg;
            Lvn.UI.LvnMontage.Sync(host, Currencies,
                key: cur => cur,
                create: cur => MakePill(cur, compact),
                update: (el, _) => (el as LvnWalletPill)?.Refresh());
        }

        private LvnWalletPill MakePill(string cur, bool compact)
        {
            var bg = LvnTokens.PanelBg;
            var captured = cur;
            return new LvnWalletPill(cur, new LvnWalletPill.Look
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
                Background = compact ? UiColor.WithAlpha(bg, 0.72f) : LvnTokens.Faint,
            }, onTap: () => OnCurrency?.Invoke(captured));
        }

        // ── режимы ────────────────────────────────────────────────────────────

        /// <summary>Высота безопасной зоны сверху в юнитах панели — бар и его
        /// содержимое опускаются ПОД вырез камеры.</summary>
        public void SetSafeTop(float units)
        {
            if (_row == null || _miniPills == null || _miniProgress == null || _tapCatcher == null)
                return;                     // ещё строимся: отступ придёт с панелью
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

        /// <summary>
        /// ВЕРХНИЙ БАР ВЪЕЗЖАЕТ СВЕРХУ — зеркало нижней навигации, той же
        /// длительности (<see cref="Lvn.UI.LvnMotion.Curtain"/>): меню
        /// раскрывается двумя кромками одновременно.
        ///
        /// <para>26.08 въезд отсюда убрали по двум причинам: он играл на каждый
        /// показ хаба, и при обрыве анимации бар оставался за кромкой. Первая
        /// ушла — точка вызова теперь только старт и возврат из главы; от
        /// второй стоит страховка ниже: чем бы анимация ни кончилась, через её
        /// срок бар возвращается на место принудительно.</para>
        /// </summary>
        /// <summary>Зарядить вход: бар уведён за верхнюю кромку ещё до показа
        /// меню — иначе он успевает мелькнуть на месте.</summary>
        public void ArmEntrance()
        {
            _row.style.translate = new Translate(0f, Length.Percent(-120f));
        }

        public void PlayEntrance()
        {
            _row.style.opacity = 1f;
            Lvn.UI.LvnMotion.Enter(_row, Lvn.UI.LvnMotion.Curtain,
                k => _row.style.translate = new Translate(0f, Length.Percent(-120f * (1f - k))));
        }

        /// <summary>Встать на место немедленно — зовёт Швейцар, если вход
        /// сорвался (пересборка документа, смена темы посреди движения).</summary>
        public void RestoreEntrance() => _row.style.translate = new Translate(0f, 0f);

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
            => Lvn.UI.LvnScreenDirector.Current.AnnounceChapter(inGame);

        // ВИД ПРИХОДИТ СИГНАЛОМ. Раньше правда о режиме текла ЧЕРЕЗ бар: хост
        // говорил бару, бар — Режиссёру, Режиссёр — всем остальным (кружку
        // загрузок, сцене). Виджет оказывался источником состояния приложения,
        // и без него — сцена без оболочки, другой хост — Режиссёр не узнавал о
        // главе вовсе, а подписчики оставались в режиме меню.
        private void ApplyChapterMode()
        {
            bool inGame = _inGame;
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
