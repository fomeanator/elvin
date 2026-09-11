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
        /// навбаром» (колонка эмоций гардероба), считают от неё. Облик «сцена»
        /// ставит свою (32 dp макета), поэтому не константа.</summary>
        public static float RowH { get; private set; } = 76f;

        /// <summary>ГДЕ КОНЧАЕТСЯ ШАПКА — один ответ всем, кто строится под ней.
        ///
        /// <para>Сумму «безопасный верх плюс высота ряда» складывали втроём:
        /// дважды сама панель (въезд и второй ряд) и лист гардероба, который для
        /// этого тянулся к чужому экрану — единственная такая связь во всей
        /// оболочке. Нижний край панели знает панель, и спрашивать его надо у
        /// неё.</para>
        ///
        /// <para>Безопасный верх зависит от устройства и меняется на повороте,
        /// поэтому ответ считается на месте, а не запоминается.</para></summary>
        public static float BottomEdge(float safeTop) => safeTop + RowH;

        /// <summary>То же, когда безопасный верх ещё не спрошен.</summary>
        public static float BottomEdge(VisualElement ctx) => BottomEdge(ScreenUi.SafeTop(ctx));

        /// <summary>Валюты пилюль (id кошелька), порядок = порядок на баре.</summary>
        public List<string> Currencies = new List<string>();
        /// <summary>Тап по пилюле валюты — хост открывает магазин.</summary>
        public Action<string> OnCurrency;
        /// <summary>Бургер: в сцене — квик-меню, в меню — настройки.</summary>
        public Action OnBurger;

        /// <summary>Игрок нажал логотип — домой, на главный экран витрины.
        /// Пусто — логотип остаётся картинкой.</summary>
        public Action OnHome;
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
            bool on = InChapter && !_silent && !_gameBarShown && free;
            _tapCatcher.pickingMode = on ? PickingMode.Position : PickingMode.Ignore;
            // ДОКТРИНА СЛОЁВ (решение Ильи 26.08): модаль сцены (квик-меню,
            // история, статы) на время жизни подавляет немодальный декор
            // оболочки — баблики прячутся, развёрнутый игровой бар сворачивается.
            // Модали оболочки (магазин, попапы) — осознанно поверх всего.
            bool modal = InChapter && !free;
            if (modal && _gameBarShown) ToggleGameBar(false);
            var vis = modal ? Visibility.Hidden : Visibility.Visible;
            _miniPills.style.visibility = vis;
            _miniProgress.style.visibility = vis;
        }

        private readonly VisualElement _row;
        private readonly VisualElement _pills;
        // Бургер — ТОЛЬКО В ГЛАВЕ, см. ApplyBarVisibility.
        private readonly VisualElement _logo, _burger;
        // Облик «сцена» (см. SetStage): аватар с именем, логотип-картинка,
        // значки валют картинками.
        private StageLook _stage;
        private ILvnAssets _assets;
        private Action _onAvatar;
        private readonly VisualElement _miniPills; // игровые баблики валют
        private readonly VisualElement _miniProgress; // баблик прогресса главы
        private readonly Label _miniProgressLabel;
        private VisualElement _gameRow;   // выезжающий игровой бар (4 кнопки)
        private bool _gameBarShown;
        private readonly VisualElement _tapCatcher;
        // Окно в Режиссёра, а не память: имя у вопроса ОДНО на всю оболочку —
        // искать «идёт ли глава» по трём синонимам было негде.
        private bool InChapter => Lvn.UI.LvnScreenDirector.Current.InChapter;
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

            // ЛОВУШКА ТАПА ПО ВЕРХНЕЙ КРОМКЕ — активна только в игре при
            // скрытом баре; ниже её сцена живёт как обычно.
            //
            // ВЫСОТА ЗАДАЁТСЯ ОДИН РАЗ И ЗДЕСЬ. Ответов на «какая она» было
            // три: сорок восемь юнитов, потом пятнадцать процентов экрана
            // (строкой ниже), а потом ещё раз сорок восемь плюс кромка — в
            // SetSafeTop, который зовётся при каждом замере выреза. Побеждал
            // последний: на телефоне с чёлкой зона схлопывалась в полоску
            // высотой с саму чёлку, и игровой бар переставал вызываться
            // тапом сверху вообще.
            //
            // Вырез сдвигает зону ВНИЗ, а не режет её: за него отвечает `top`,
            // высота остаётся долей экрана (см. SetSafeTop).
            _tapCatcher = new VisualElement();
            _tapCatcher.style.position = Position.Absolute;
            _tapCatcher.style.left = 0; _tapCatcher.style.right = 0;
            _tapCatcher.style.top = 0;
            _tapCatcher.style.height = Length.Percent(TapZonePercent);
            _tapCatcher.style.display = DisplayStyle.None;
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

            _logo = Logo();
            _row.Add(_logo);

            var spacer = new VisualElement();
            spacer.pickingMode = PickingMode.Ignore;
            spacer.style.flexGrow = 1;
            _row.Add(spacer);

            _pills = new VisualElement();
            ScreenUi.Row(_pills);
            _row.Add(_pills);

            _burger = Burger();
            _row.Add(_burger);

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
            LvnChrome.Edged(_miniProgress, LvnTokens.Radius);
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
            LvnAir.Pad(_gameRow, LvnTokens.Space1, LvnTokens.Space2);
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
            // ВИД НАДО ПРИМЕНИТЬ СРАЗУ, а не ждать первой смены режима.
            // Правда о режиме приходит сигналом Режиссёра, но сигнал звучит
            // только КОГДА РЕЖИМ МЕНЯЕТСЯ. Бар рождается в витрине, где режим
            // уже стоит и меняться не собирается, — и до первого входа в главу
            // жил с видом по умолчанию (страж TopBarBurgerTests: бургер
            // оставался на экране витрины).
            ApplyBarVisibility();
        }

        // Подпись кнопки берётся ИСТОЧНИКОМ: игровой ряд шапки собирается один
        // раз и живёт всю игру, поэтому строка в нём застыла бы на языке,
        // который стоял в момент сборки.
        private VisualElement GameButton(LvnIcon icon, Func<string> label, Action onTap)
        {
            var b = new VisualElement();
            b.style.alignItems = Align.Center;
            LvnAir.Pad(b, LvnTokens.Space2, LvnTokens.Space1);
            LvnChrome.Round(b, LvnTokens.RadiusSm);
            var ic = LvnIcons.Make(icon, 28f, LvnTokens.Accent);
            ic.pickingMode = PickingMode.Ignore;
            b.Add(ic);
            var l = Lvn.UI.LvnRedress.Bind(new Label(), label);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.Text;
            l.style.fontSize = LvnTokens.TextXs;
            l.style.marginTop = LvnTokens.Tight;
            b.Add(l);
            b.RegisterCallback<ClickEvent>(_ => onTap());
            return b;
        }

        /// <summary>Сколько бар ждёт тишины, прежде чем уйти сам.</summary>
        private const long GameBarQuietMs = 5000;

        // Один отсчёт автоухода на все открытия — заводится при первом.
        private IVisualElementScheduledItem _barAutoHide;

        /// <summary>Открыто ли контекстное меню главы — у Режиссёра, не у виджета.</summary>
        private static bool QuickMenuOpen
            => Lvn.UI.LvnScreenDirector.Current.IsOpen(Lvn.UI.LvnScreenDirector.QuickMenu);

        /// <summary>ПОРА ЛИ БАРУ УЙТИ ПО ТИШИНЕ: он показан, и над ним нет
        /// открытого меню. Правило одно на оба места, где живёт отсчёт: бар,
        /// открытый самим меню (тап по бургеру в бабликах при спрятанном
        /// баре), взводил отсчёт при показе, а паузу получал только «уже
        /// показанный» — и через пять секунд уезжал из-под открытого меню
        /// («навбар скрывается хотя контекстное меню открыто» — Илья 09.09).</summary>
        public static bool BarRests(bool barShown, bool menuOpen) => barShown && !menuOpen;

        private void ToggleGameBar(bool? force = null)
        {
            bool show = force ?? !_gameBarShown;
            if (show == _gameBarShown && force == null) return;
            _gameBarShown = show;
            // Закрылись — отсчёт больше не нужен: он разбудится при следующем
            // открытии. Иначе он доживёт до конца и закроет уже чужое открытие.
            if (!show) _barAutoHide?.Pause();
            float slide = BottomEdge(_safeTop) + 150f;
            if (show)
            {
                // ПОЛНЫЙ навбар (лого/валюты/бургер) + строка кнопок ПОД ним —
                // ансамблем сверху; баблики на это время прячутся (дубль).
                _gameRow.style.top = GameRowTop();
                _gameRow.style.paddingTop = LvnTokens.Space2;
                ApplyBarVisibility();   // баблики — дубль бара: на это время уходят
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
                    () => { if (BarRests(_gameBarShown, QuickMenuOpen)) ToggleGameBar(false); });
                if (QuickMenuOpen) _barAutoHide.Pause();   // меню открыто — отсчёт не идёт
                else _barAutoHide.ExecuteLater(GameBarQuietMs);
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
                        // Сдвиг снимается ВСЕГДА, а не только в игре: в меню ряд
                        // остаётся видимым, и уехавший за верх экрана он выглядел
                        // бы пропавшим — тем же, чем и был до этой правки.
                        _row.style.translate = new Translate(0f, 0f);
                        _gameRow.style.translate = new Translate(0f, 0f);
                        ApplyBarVisibility();
                    }
                });
            }
        }

        // ── облик «сцена» ─────────────────────────────────────────────────────

        /// <summary>
        /// ОБЛИК «СЦЕНА» ШАПКИ (<c>ui.browse.skin</c>): аватар с именем слева,
        /// логотип-картинка по центру на всю ширину, валюты значками-картинками
        /// с нарисованным «плюсом», без подложки — шапка стоит на тёмной вуали
        /// полотна. Всё это данные манифеста; без них шапка прежняя.
        /// </summary>
        public sealed class StageLook
        {
            /// <summary>Картинка логотипа — на всю ширину шапки (с линиями по бокам).</summary>
            public string Logo;
            /// <summary>Аватар игрока, пока у аккаунта нет своего.</summary>
            public string Avatar;
            /// <summary>Значок «+» у валюты.</summary>
            public string Plus;
            /// <summary>Валюта → значок-картинка.</summary>
            public Dictionary<string, string> CurrencyIcons;
        }

        /// <summary>Холст макета 390 dp против панели 1080 — тот же множитель,
        /// что у главной (BrowseHub.Stage).</summary>
        private static float StageD(float dp) => Mathf.Round(dp * (1080f / 390f));

        /// <summary>Картинка логотипа облика «сцена» — по ней считается место
        /// циферблата (см. <see cref="LogoDialRect"/>).</summary>
        private VisualElement _stageLogo;
        private VisualElement _stageAvatar;

        /// <summary>Показать другое лицо игрока (TR-79). Пересобирать всю
        /// шапку ради кружка незачем: у аватара своя картинка и своё место.</summary>
        public void SetAvatar(string url, ILvnAssets assets)
        {
            if (_stageAvatar == null || string.IsNullOrEmpty(url)) return;
            LvnPicture.Photo(_stageAvatar, url, assets ?? _assets, cover: true);
        }

        // ЦИФЕРБЛАТ В ЛОГОТИПЕ — доли от габаритов картинки. В логотипе Time
        // Romance буква «O» слова ROMANCE нарисована карманными часами, и
        // кружок загрузок садится ровно в неё: синее кольцо крутится вокруг
        // циферблата, «по размеру её» (просьба Ильи 08.09). Числа сняты с самой
        // картинки (1152×246: центр 501.5×160, оправа 53 px), поэтому держатся
        // за пропорции, а не за пиксели экрана.
        private const float DialCenterX = 0.4353f;
        private const float DialCenterY = 0.6484f;
        private const float DialSize = 0.046f;

        /// <summary>Докуда в картинке логотипа доходят БУКВЫ (доля высоты):
        /// ниже — только прозрачный запас под свечение. Картинка нарисована
        /// выше своего места, и без этой доли «под логотипом» означало бы «под
        /// его пустотой».</summary>
        private const float LogoInkBottom = 0.781f;

        /// <summary>ГДЕ НАЧИНАЕТСЯ ИГРОВОЙ РЯД. Обычно сразу под строкой
        /// шапки, но логотип облика «сцена» свисает НИЖЕ неё — и кнопки
        /// «Выйти в меню / История / Гардероб / Магазин» ложились прямо на
        /// слово ROMANCE (скрин Ильи 08.09). Считаем по самой картинке, а не
        /// подбираем отступ: её высота и вылет заданы рядом, тут же.</summary>
        private float GameRowTop()
        {
            // ШАПКИ В ГЛАВЕ НЕТ (TR-76) — и вставать под неё не нужно: ряд
            // садится сразу под вырез, а не под пустое место, где раньше был
            // логотип с валютами.
            if (InChapter) return _safeTop + LvnTokens.Space1;
            float row = BottomEdge(_safeTop);
            if (_stageLogo == null) return row;
            float logoInk = _safeTop - StageD(12f) + StageD(82f) * LogoInkBottom + StageD(6f);
            return Mathf.Max(row, logoInk);
        }

        /// <summary>
        /// МЕСТО ЦИФЕРБЛАТА НА ЭКРАНЕ — прямоугольник в координатах панели,
        /// или null, если логотипа облика на экране сейчас нет (обычная шапка,
        /// глава со свёрнутым баром, ещё не разложенная панель).
        ///
        /// <para>Спрашивает кружок загрузок: держать его собственные координаты
        /// он не может — логотип живёт по своей вёрстке и меняет место вместе с
        /// шириной экрана и safe area.</para>
        /// </summary>
        public Rect? LogoDialRect()
        {
            if (_stageLogo == null || _row == null) return null;
            if (_row.style.display == DisplayStyle.None
                || _stageLogo.style.display == DisplayStyle.None) return null;
            var r = _stageLogo.worldBound;
            if (float.IsNaN(r.width) || r.width <= 1f || float.IsNaN(r.height)) return null;
            float d = r.width * DialSize;
            return new Rect(r.xMin + r.width * DialCenterX - d * 0.5f,
                            r.yMin + r.height * DialCenterY - d * 0.5f, d, d);
        }

        /// <summary>Одеть шапку по макету. Зовёт хост, когда манифест назвал
        /// облик «сцена»; повторный вызов пересобирает только живое.</summary>
        public void SetStage(StageLook look, ILvnAssets assets, Action onAvatar)
        {
            if (look == null) return;
            _stage = look; _assets = assets; _onAvatar = onAvatar;
            RowH = StageD(32f);
            _row.style.height = RowH;
            // Подложки и черты нет: шапка стоит на вуали полотна.
            _row.style.backgroundColor = Color.clear;
            LvnChrome.ClearBorder(_row);
            LvnAir.PadX(_row, StageD(15f));
            _logo.style.display = DisplayStyle.None;
            _burger.style.display = DisplayStyle.None;

            // Аватар с именем — дверь в профиль.
            var profile = ScreenUi.Row();
            var avatar = new VisualElement { name = "stage-img", pickingMode = PickingMode.Ignore };
            avatar.style.width = StageD(32f); avatar.style.height = StageD(32f);
            avatar.style.backgroundColor = LvnTokens.SurfaceHi;
            avatar.style.overflow = Overflow.Hidden;
            LvnChrome.Frame(avatar, StageD(4f), UiColor.Darker(LvnTokens.Gold, 0.55f), StageD(1f));
            if (!string.IsNullOrEmpty(look.Avatar)) LvnPicture.Photo(avatar, look.Avatar, assets);
            _stageAvatar = avatar;   // TR-79: лицо меняется на ходу, без пересборки шапки
            profile.Add(avatar);
            var name = Lvn.UI.LvnRedress.Bind(new Label(), () => Lvn.UI.LvnPlayerName.Display);
            name.pickingMode = PickingMode.Ignore;
            name.style.color = LvnTokens.Bronze;
            name.style.fontSize = LvnTokens.TextSm;
            name.style.marginLeft = StageD(8f);
            profile.Add(name);
            profile.AddManipulator(new Clickable(() => _onAvatar?.Invoke()));
            profile.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            LvnMotion.Tappable(profile);
            _row.Insert(0, profile);

            // Логотип: полоса на всю ширину шапки, нарисованная с запасом в
            // 12 dp под свечение — потому шире и выше своего места.
            if (!string.IsNullOrEmpty(look.Logo))
            {
                var art = new VisualElement { name = "stage-img", pickingMode = PickingMode.Ignore };
                art.style.position = Position.Absolute;
                art.style.left = StageD(3f); art.style.right = StageD(3f);
                art.style.top = -StageD(12f); art.style.height = StageD(82f);
                LvnPicture.Skin(art, look.Logo, assets, "StageLogo");
                // ЛОГОТИП ВЕДЁТ ДОМОЙ (TR-77). Раньше на главную возвращала
                // вкладка «Свидания», но у неё будет свой экран, и тогда
                // возвращаться станет нечем. Логотип — привычная дверь домой в
                // любом приложении, и она никуда не переедет.
                art.pickingMode = PickingMode.Position;
                art.AddManipulator(new Clickable(() => OnHome?.Invoke()));
                LvnMotion.Tappable(art);
                _row.Add(art);
                _stageLogo = art;
            }
            else _stageLogo = null;

            // Пилюли пересобираются под облик: значки картинками, без подложки.
            _pills.Clear();
            RefreshBalances();
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
            LvnChrome.Circle(circle, 50f);
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
            b.name = "burger";   // имя нужно стражу и разбору дерева
            const float size = 52f;   // палец: минимальная зона нажатия с запасом
            b.style.marginLeft = LvnTokens.Space2;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            // КРУГ, а не скруглённый квадрат: рядом с ним стоят круглые пилюли
            // валют и круглый кружок загрузок, и один квадрат в этом ряду
            // читался чужим («кнопку меню надо сделать круговой» — Илья 09.09).
            // Радиус — половина стороны, а не своё число: иначе он разъезжается
            // с размером при первой же правке.
            LvnChrome.Circle(b, size);
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
            // Облик «сцена»: значок-картинка, число латунью, «плюс» картинкой,
            // без подложки. Игровые баблики над сценой остаются прежними.
            if (_stage != null && !compact)
                return new LvnWalletPill(cur, new LvnWalletPill.Look
                {
                    // Пары валют стоят плотно внутри и просторно между собой:
                    // так читается «значок с числом», а не четыре отдельных
                    // предмета в ряд (TR-78, эталон Арама).
                    MarginLeft = StageD(14f),
                    Height = StageD(24f),
                    PadLeft = 0, PadRight = 0, PadY = 0,
                    Radius = 0f,
                    IconSize = StageD(24f),
                    FontSize = LvnTokens.TextSm,
                    Bold = false,
                    Edge = false,
                    Background = Color.clear,
                    TextColor = LvnTokens.Bronze,
                    IconUrl = _stage.CurrencyIcons != null
                              && _stage.CurrencyIcons.TryGetValue(cur, out var iconUrl) ? iconUrl : null,
                    PlusIconUrl = _stage.Plus,
                    PlusSize = StageD(16f),
                    AmountMinWidth = StageD(10f),
                }, _assets,
                onTap: () => OnCurrency?.Invoke(captured),
                onPlus: () => OnCurrency?.Invoke(captured));
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
            // Зона тапа ОПУСКАЕТСЯ под вырез, а не сжимается им. Здесь стояла
            // высота (`48 + units`) — третий ответ на вопрос, у которого один
            // хозяин: конструктор. На телефоне с чёлкой она перебивала долю
            // экрана пикселями и оставляла от зоны полоску.
            _tapCatcher.style.top = units;
        }

        /// <summary>ВЫСОТА ЗОНЫ ТАПА — доля экрана, один ответ на весь класс.
        /// Проценты, а не юниты: на планшете и на телефоне «верхняя кромка» —
        /// разное число пикселей, а доля одна.</summary>
        internal const float TapZonePercent = 15f;

        /// <summary>Состояние бара для лога: видно ли его вообще и не застрял
        /// ли он за верхней кромкой (вход анимирует translate).</summary>
        public string DebugState =>
            $"display={_row.resolvedStyle.display} translate={_row.resolvedStyle.translate} "
            + $"opacity={_row.resolvedStyle.opacity:0.00} inGame={InChapter} silent={_silent} "
            + $"rect=({_row.worldBound.y:0} {_row.worldBound.width:0}x{_row.worldBound.height:0})";

        /// <summary>Зарядить вход: бар уведён за верхнюю кромку ещё до показа
        /// меню — иначе он успевает мелькнуть на месте.</summary>
        public void ArmEntrance()
        {
            _row.style.translate = new Translate(0f, Length.Percent(-120f));
        }

        /// <summary>
        /// ВЕРХНИЙ БАР ВЪЕЗЖАЕТ СВЕРХУ — зеркало нижней навигации, той же
        /// длительности (<see cref="Lvn.UI.LvnMotion.Curtain"/>): меню
        /// раскрывается двумя кромками одновременно.
        ///
        /// <para>26.08 въезд отсюда убрали по двум причинам: он играл на каждый
        /// показ хаба, и при обрыве анимации бар оставался за кромкой. Первая
        /// ушла — точка вызова теперь только старт и возврат из главы; от
        /// второй стоит страховка: чем бы анимация ни кончилась, через её срок
        /// бар возвращается на место принудительно.</para>
        /// </summary>
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
                _gameBarShown = false;
                ApplyBarVisibility();
            }
            else SetInGameApply();
        }
        private bool _silent;

        /// <summary>
        /// КТО ВИДЕН ПРЯМО СЕЙЧАС — из трёх признаков разом: тишина воронки,
        /// игровой режим и открыт ли игровой бар.
        ///
        /// <para>Состав бара — пять поверхностей, а решали про них ТРИ места, и
        /// каждое перечисляло свой набор: тишина — все пять, смена режима —
        /// четыре, открытие/закрытие бара — по-своему в каждой ветке. Шестая
        /// поверхность попала бы в одно место из трёх.</para>
        ///
        /// <para>Живой случай: выход из главы при ОТКРЫТОМ игровом баре. Смена
        /// режима показывала верхний ряд, а через 200 мс конец анимации
        /// скрытия прятал его обратно — и возвращал только в игре. В меню
        /// верхняя панель просто исчезала до следующего повода её поставить.
        /// Там же терялся сдвиг: ряд оставался уехавшим за верх экрана.</para>
        /// </summary>
        private void ApplyBarVisibility()
        {
            // ТИШИНА — НЕ ТОЛЬКО ВОРОНКИ. Кадр без интерфейса (катсцена,
            // разглядывание арта) прячет реплики и меню сцены, а игровой бар
            // оставался поверх кино: баблики, полоса главы, ловец тапа
            // («надо навбар скрыть полностью в катсцене, игровой» — Илья
            // 09.09). Причину держит Режиссёр — у бара она общая с хромом
            // сцены, и своя отмена не снимает чужую.
            bool hush = _silent || Lvn.UI.LvnScreenDirector.Current.ChromeHidden;
            bool bar = _gameBarShown && !hush;      // игровой бар развёрнут
            // В ГЛАВЕ ПО ТАПУ ПОКАЗЫВАЕТСЯ РОВНО ОДНО: ряд из четырёх кнопок,
            // а с ним процент главы и кошелёк (TR-76). Шапка витрины — аватар,
            // логотип, пилюли, бургер — в главе не нужна вовсе: это экран
            // истории, а не витрины, и всё перечисленное либо дублирует ряд,
            // либо уводит из сцены случайным касанием.
            //
            // Процент и валюта РАНЬШЕ ВИСЕЛИ ВСЕГДА: их показывали, пока бар
            // свёрнут, — то есть постоянно поверх текста. Теперь они приходят
            // и уходят вместе с рядом, одним тапом.
            bool mini = bar && InChapter;
            Vis(_row, !hush && !InChapter);
            Vis(_gameRow, bar);
            Vis(_miniPills, mini);
            Vis(_miniProgress, mini);
            Vis(_tapCatcher, InChapter && !hush);
            // БУРГЕР — ДВЕРЬ ИЗ ГЛАВЫ, а не украшение шапки. Он открывает
            // игровое меню (сохранения, история, настройки сцены), и в
            // витрине ему открывать нечего: там те же вещи лежат по вкладкам
            // навбара. Игрок видел его на главной и на гардеробе, жал — и
            // получал меню про главу, которой нет (просьба Ильи 08.09).
            Vis(_burger, InChapter);
        }

        private static void Vis(VisualElement el, bool on)
        {
            if (el != null) el.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SetInGameApply() => ApplyBarVisibility();

        /// <summary>Игровой режим (уточнение Ильи 26.08): бар в сцене
        /// ПРОПАДАЕТ целиком — вместо него мини-баблики валют (справа) и
        /// кружок загрузок (слева, DownloadHud сам). Ловушка тапа не нужна:
        /// квик-меню открывают фабы сцены.</summary>
        /// <para>ШОВ ДЛЯ ХОСТА, и внутри движка его никто не зовёт: правда о
        /// режиме течёт сигналом Режиссёра (см. ниже), а не через бар. Дверь
        /// оставлена тому, кто держит бар руками и не имеет Режиссёра.</para>
        public void SetInGame(bool inGame)
            => Lvn.UI.LvnScreenDirector.Current.AnnounceChapter(inGame);

        // ВИД ПРИХОДИТ СИГНАЛОМ. Раньше правда о режиме текла ЧЕРЕЗ бар: хост
        // говорил бару, бар — Режиссёру, Режиссёр — всем остальным (кружку
        // загрузок, сцене). Виджет оказывался источником состояния приложения,
        // и без него — сцена без оболочки, другой хост — Режиссёр не узнавал о
        // главе вовсе, а подписчики оставались в режиме меню.
        private void ApplyChapterMode()
        {
            bool inGame = InChapter;
            // НАВБАР ДЕРЖИТСЯ, ПОКА ОТКРЫТО КОНТЕКСТНОЕ МЕНЮ. У бара свой отсчёт
            // тишины, у меню — своя жизнь, и совпадали они случайно: игрок
            // открывал меню, а шапка под ним уже уезжала, и меню висело само по
            // себе на голой сцене («они раздельно живут» — Илья 09.09,
            // ELVIN-97). Меню открыто — отсчёт стоит; закрылось — пошёл заново,
            // и бар уйдёт своим чередом.
            bool menuOpen = inGame && Lvn.UI.LvnScreenDirector.Current.IsOpen(
                Lvn.UI.LvnScreenDirector.QuickMenu);
            if (menuOpen)
            {
                if (!_gameBarShown) ToggleGameBar(true);   // показ при открытом меню отсчёт не заводит
                _barAutoHide?.Pause();
            }
            else if (_gameBarShown && _barAutoHide != null)
            {
                _barAutoHide.ExecuteLater(GameBarQuietMs);
            }
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
