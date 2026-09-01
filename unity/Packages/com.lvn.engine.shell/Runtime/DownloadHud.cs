using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЕДИНЫЙ индикатор загрузки контента, двух видов (решение Ильи 25.08):
    /// МИНИ — хромовский кружок: стрелка с полочкой и кольцо прогресса вокруг
    /// (painter2D, без единого ассета); тап ПЕРЕТЕКАЕТ капсулу в ПОЛНУЮ
    /// карточку — имя файла, текущая скорость, очередь и скачанный объём.
    /// Морф — одна анимация ширины/высоты/радиуса с кроссфейдом содержимого:
    /// UITK анимирует style-свойства по тикам, поэтому «перетекание» здесь
    /// такое же родное, как в вебе FLIP.
    ///
    /// Сюда стекается ВСЯ сеть («Скачать всё», прелоад главы, стриминг) через
    /// ContentLoader.Transfers() — раньше прогресс жил только в настройках, и
    /// «закрыл настройки — загрузка пропала» (живой репорт).
    /// </summary>
    public sealed partial class DownloadHud : VisualElement
    {
        // Геометрия двух состояний капсулы.
        private const float MiniSize = 54f; // чуть шире (просьба Ильи 26.08)
        private const float FullWMax = 720f;
        private float _fullW = 520f; // 60% ширины экрана, считается при развороте
        private const float FullHMax = 560f;
        // Фактическая высота полной формы — АДАПТИВНАЯ (живой скрин: 560
        // не влезали, кнопка уходила за край): считается при развороте от
        // реальной высоты экрана, контент внутри скроллится.
        private float _fullH = FullHMax;

        // ── швы к хосту (NovelApp навешивает после Build) ────────────────────
        /// <summary>Очередь глав «Скачать всё» — для списка и крестиков.</summary>
        public DownloadCenter Center;
        /// <summary>Сеть пропала? (LvnNetworkStatus)</summary>
        public Func<bool> Offline;
        /// <summary>Событий кошелька/прогресса, ждущих отправки на сервер.</summary>
        public Func<int> PendingOps;
        /// <summary>Главы и их офлайн-доступность (полностью в кэше?). Зовётся
        /// при развороте попапа — проверка по диску не для каждого тика.</summary>
        public Func<List<(string label, bool cached)>> ChaptersInfo;
        /// <summary>«Скачать всю игру» — тот же хук, что в настройках.</summary>
        public Func<Task> DownloadAll;
        /// <summary>Текущая открытая глава, если её ещё можно докачать:
        /// (подпись, старт) — кнопка «Скачать главу» в попапе.</summary>
        public Func<(string label, Action start)?> CurrentChapterOffer;
        /// <summary>Сколько осталось скачать (байт, файлов) — подпись кнопки.</summary>
        public Func<(long bytes, int files)> MissingInfo;
        /// <summary>Что-то уже на диске → кнопка говорит «Докачать».</summary>
        public Func<bool> HasSomeDownloaded;
        /// <summary>Есть ли работа прямо сейчас (кружок показан) — единый
        /// навбар держится на экране этим сигналом в игровом режиме.</summary>
        public bool HasWork => _shown;

        /// <summary>Отступ safe area — кружок сидит в строке бара, ниже выреза.</summary>
        public void SetSafeTop(float units)
        {
            if (_capsule == null) return;   // ещё строимся: отступ придёт с панелью
            _capsule.style.marginTop = units + 5f;
        }

        /// <summary>Модаль сцены открыта: мини-кружок прячется (декор уступает),
        /// развёрнутый попап — модаль оболочки и остаётся поверх.</summary>
        public void SetSceneModal(bool modal)
            => _capsule.style.visibility = modal && !_expanded
                ? Visibility.Hidden : Visibility.Visible;

        /// <summary>
        /// Игровой режим: кружок — отдельный баблик в ЛЕВОМ верхнем углу сцены
        /// (бар пропал, валюты справа такими же бабликами); в меню — центр
        /// строки бара.
        ///
        /// <para>СЛУШАЕТ РЕЖИССЁРА, а не ждёт команды. Прежде состояние «мы в
        /// главе» рассылали вручную: оболочка звала и бар, и кружок на входе и
        /// выходе. Но есть третий путь — показ хаба — и там звали ТОЛЬКО бар:
        /// кружок оставался с игровым отступом поверх меню. Бар при этом сам
        /// сообщает режим Режиссёру, так что источник правды был, просто кружок
        /// его не спрашивал.</para>
        /// </summary>
        private void ApplyChapterMode()
        {
            bool inGame = Lvn.UI.LvnScreenDirector.Current.InChapter;
            style.alignItems = inGame ? Align.FlexStart : Align.Center;
            _capsule.style.marginLeft = inGame ? 104 : 0; // правее баблика прогресса
        }


        private void FollowChapterMode()
        {
            Lvn.LvnLeash.WhileOnScreen(this,
                () => Lvn.UI.LvnScreenDirector.Current.Changed += ApplyChapterMode,
                () => Lvn.UI.LvnScreenDirector.Current.Changed -= ApplyChapterMode,
                ApplyChapterMode);
        }

        /// <summary>Подтолкнуть отправку накопленных событий: кошелёк флашится
        /// только на операциях, и без пинка «↑ Синхронизация» висела бы до
        /// следующего действия игрока.</summary>
        public Func<Task> FlushPending;
        private float _lastFlushKick;
        private float _lastMissingAt = -999f;

        private readonly VisualElement _capsule;
        private readonly ProgressRing _miniRing;
        private readonly VisualElement _full;
        private ProgressRing _fullRing;
        private Label _file, _kind;
        private Label _vSpeed, _vQueue, _vGot, _vLeft;
        private ScrollView _sections;
        /// <summary>Текущий качаемый url — для человеческой подписи
        /// («Персонажи и наряды», а не имя файла).</summary>
        public Func<string> ActiveUrl;

        private bool _expanded;
        private float _morph;          // 0 = мини, 1 = полная (текущее положение)
        private long _lastBytes;
        private float _lastAt = -1f;
        private float _speed;          // байт/с, EMA
        private float _speedLast;      // последнее ОСМЫСЛЕННОЕ значение — его и показываем

        private readonly VisualElement _scrim;

        public DownloadHud()
        {
            pickingMode = PickingMode.Ignore;
            LvnChrome.Stretch(this);
            style.alignItems = Align.Center; // капсула — центр строки навбара
            style.display = DisplayStyle.None; // до первой работы кружка нет
            FollowChapterMode();               // вид следует за Режиссёром сам
            // И за кромкой — тоже сам: кружок сидит в строке бара, ниже выреза.
            Lvn.UI.LvnEdges.Follow(this, insets => SetSafeTop(insets.x));

            // Ловец тапов «мимо попапа»: невидим и не мешает, пока попап
            // свёрнут; при развороте ловит клик В ЛЮБОЙ точке экрана и утекает
            // попап обратно в кружок (решение Ильи: крестик или тап вне).
            _scrim = new VisualElement();
            LvnChrome.Stretch(_scrim);
            _scrim.style.display = DisplayStyle.None;
            _scrim.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                SetExpanded(false);
            });
            Add(_scrim);

            _capsule = new VisualElement();
            // ЦЕНТР строки единого навбара (решение Ильи 26.08): кружок живёт
            // в баре, морф попапа растёт симметрично из его же точки. Отступ
            // сверху хост синхронизирует с safe area бара (SetSafeTop).
            _capsule.style.marginTop = LvnTokens.Tight;
            var bg = LvnTokens.PanelBg;
            // Просто полупрозрачный тон — блюр-стекло снято (Илья, 26.08).
            _capsule.style.backgroundColor = UiColor.WithAlpha(bg, 0.94f);
            LvnChrome.Edge(_capsule);
            _capsule.style.overflow = Overflow.Hidden;
            _capsule.style.alignItems = Align.Center;
            _capsule.style.justifyContent = Justify.Center;
            // Разворачивает клик по МИНИ; свёртывание — только крестик или
            // тап мимо (клик по самой карточке ничего не делает — иначе любое
            // случайное касание закрывало бы её).
            _capsule.RegisterCallback<ClickEvent>(_ => { if (!_expanded) SetExpanded(true); });
            _capsule.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            Add(_capsule);

            _miniRing = new ProgressRing(MiniSize * 0.5f - 5f, 3.5f, drawArrow: true);
            _miniRing.style.width = MiniSize; _miniRing.style.height = MiniSize;
            _miniRing.pickingMode = PickingMode.Ignore;
            _capsule.Add(_miniRing);

            // Полное содержимое живёт всегда и кроссфейдится морфом.
            _full = new VisualElement();
            _full.pickingMode = PickingMode.Ignore;
            _full.style.position = Position.Absolute;
            _full.style.left = 18; _full.style.right = 18;
            _full.style.top = 14; _full.style.bottom = 14;
            _full.style.opacity = 0f;
            _capsule.Add(_full);

            var head = ScreenUi.Row();
            head.pickingMode = PickingMode.Ignore;
            ScreenUi.Row(head, spread: true);
            _full.Add(head);

            var title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("downloads.title", "Downloads"));
            title.pickingMode = PickingMode.Ignore;
            title.style.color = LvnTokens.Text;
            title.style.fontSize = LvnTokens.TextBase;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.Add(title);

            var close = new Label("×");
            close.style.color = LvnTokens.TextDim;
            close.style.fontSize = LvnTokens.TextSm;
            LvnAir.PadY(close, LvnTokens.Space1);
            close.style.paddingLeft = LvnTokens.Space2;
            close.style.paddingRight = LvnTokens.Space1;
            close.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); SetExpanded(false); });
            head.Add(close);

            var active = ScreenUi.Row();
            active.pickingMode = PickingMode.Ignore;
            ScreenUi.Row(active);
            active.style.marginTop = LvnTokens.Space2;
            _full.Add(active);

            _fullRing = new ProgressRing(24f, 4.5f, drawArrow: true);
            _fullRing.style.width = 58; _fullRing.style.height = 58;
            _fullRing.style.marginRight = LvnTokens.Space2;
            _fullRing.style.flexShrink = 0;
            _fullRing.pickingMode = PickingMode.Ignore;
            active.Add(_fullRing);

            var col = new VisualElement();
            col.pickingMode = PickingMode.Ignore;
            col.style.flexGrow = 1; col.style.flexShrink = 1;
            active.Add(col);

            _file = new Label("");
            _file.pickingMode = PickingMode.Ignore;
            _file.style.color = LvnTokens.Text;
            _file.style.fontSize = LvnTokens.TextSm;
            _file.style.unityFontStyleAndWeight = FontStyle.Bold;
            _file.style.overflow = Overflow.Hidden;
            _file.style.textOverflow = TextOverflow.Ellipsis;
            _file.style.whiteSpace = WhiteSpace.NoWrap;
            col.Add(_file);

            _kind = new Label("");
            _kind.pickingMode = PickingMode.Ignore;
            _kind.style.color = LvnTokens.TextDim;
            _kind.style.fontSize = LvnTokens.TextXs;
            _kind.style.marginTop = LvnTokens.Hair;
            col.Add(_kind);

            // Поля — ТАБЛИЦЕЙ, по строке на факт (не лапшой через «·»):
            // скорость, очередь, скачано, осталось — всё, что просилось.
            // Поля — матрицей 2×2 (уточнение Ильи 26.08): компактнее, меньше
            // высоты, читается блоком.
            var info = new VisualElement();
            info.pickingMode = PickingMode.Ignore;
            info.style.marginTop = LvnTokens.Space2;
            info.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Edge(info);
            LvnChrome.Round(info, LvnTokens.Radius);
            LvnAir.PadX(info, LvnTokens.Space2);
            info.style.paddingBottom = LvnTokens.Space1;
            info.style.paddingTop = LvnTokens.Space2;
            LvnFlow.Wrap(info);
            _full.Add(info);
            _vSpeed = InfoCell(info, () => LvnWords.Of("dl.speed", "Speed"));
            _vQueue = InfoCell(info, () => LvnWords.Of("dl.queued", "Queued"));
            _vGot   = InfoCell(info, () => LvnWords.Of("dl.done", "Downloaded"));
            _vLeft  = InfoCell(info, () => LvnWords.Of("dl.left", "Left"));

            // Секции (офлайн-правила, синк, очередь глав, «скачать всё») —
            // перестраиваются при развороте и по изменению очереди.
            _sections = Lvn.UI.LvnScroll.Vertical();
            _sections.style.flexGrow = 1;
            _sections.style.marginTop = LvnTokens.Space2;
            _full.Add(_sections);

            ApplyMorph(0f);
        }

        // ── данные ────────────────────────────────────────────────────────────

        /// <summary>Скормить свежий снимок сети (таймер оболочки, ~300 мс).</summary>
        public void Tick((int inflight, int batchTotal, int batchDone, long received, long expected, string label) t)
        {
            float now = Lvn.LvnClock.Wall();
            bool act = t.inflight > 0 || (t.batchTotal > 0 && t.batchDone < t.batchTotal);
            bool off = Offline?.Invoke() ?? false;
            bool queued = Center != null && (Center.Running || Center.Queue.Count > 0);
            int pend = PendingOps?.Invoke() ?? 0;
            // Кружок видим, пока ЕСТЬ РАБОТА: активная загрузка, непустая
            // очередь глав (паузы между файлами и главами НЕ прячут его —
            // «мигает», живой репорт), офлайн с очередью или несинхроненные
            // события. Скрывается только в настоящем простое.
            bool visible = act || queued || pend > 0;

            if (_lastAt > 0f && t.received >= _lastBytes)
            {
                float dt = now - _lastAt;
                if (dt > 0.05f)
                {
                    float inst = (t.received - _lastBytes) / dt;
                    _speed = _speed <= 0f ? inst : Mathf.Lerp(_speed, inst, 0.35f);
                }
            }
            else if (t.received < _lastBytes) _speed = 0f;
            _lastBytes = t.received;
            _lastAt = now;

            if (visible)
            {
                _quietSince = -1f;
                if (!_shown)
                {
                    _shown = true;
                    style.display = DisplayStyle.Flex;
                    _capsule.experimental.animation.Start(0f, 1f, LvnMotion.Ms(LvnMotion.Normal),
                        (_, p) => _capsule.style.opacity = p);
                }

                var glyph = off && (act || queued) ? RingGlyph.Alert
                    : act || queued ? RingGlyph.Down
                    : RingGlyph.Up;
                if (glyph == RingGlyph.Up && !off && FlushPending != null
                    && now - _lastFlushKick > 5f)
                {
                    _lastFlushKick = now;
                    Lvn.LvnAsync.Fire(FlushPending(), "FlushPending");
                }
                _miniRing.Glyph = glyph;
                _fullRing.Glyph = glyph;

                // Кольцо и «Скачано» — ОБЩИЙ прогресс очереди (сумма глав):
                // байты завершённых глав + текущий батч / все поставленные.
                // Без очереди: одиночный батч как раньше; стриминг — спиннер.
                var (qDone, qTotal) = Center?.Progress ?? (0L, 0L);
                long batchRec = t.batchTotal > 0 ? t.received : 0; // вне батча — мусор стриминга
                float frac;
                if (qTotal > 0)
                    frac = Mathf.Clamp01((qDone + batchRec) / (float)qTotal);
                else if (act && t.batchTotal > 0)
                    frac = t.expected > 0 ? Mathf.Clamp01((float)t.received / t.expected)
                        : Mathf.Clamp01((float)t.batchDone / Mathf.Max(1, t.batchTotal));
                else frac = -1f;
                _miniRing.Progress = frac;
                _fullRing.Progress = frac;

                if (glyph == RingGlyph.Alert)
                {
                    _file.text = Lvn.Content.LvnOfflineText.Title;
                    _kind.text = LvnWords.Of("downloads.resumes", "The download will resume by itself");
                }
                else if (glyph == RingGlyph.Up)
                {
                    _file.text = LvnWords.Of("downloads.syncing", "Syncing");
                    _kind.text = LvnWords.Of("downloads.pending_ops", "Events to send: {0}", pend);
                }
                else
                {
                    _file.text = string.IsNullOrEmpty(t.label) ? LvnWords.Of("downloads.content", "Downloading content") : t.label;
                    var activeEntry = ActiveEntry();
                    _kind.text = Humanize(ActiveUrl?.Invoke(), null)
                        + (activeEntry != null ? " · " + activeEntry.Label : "");
                }
                if (_expanded)
                {
                    // ПОСЛЕДНЕЕ ИЗВЕСТНОЕ, А НЕ ПРОЧЕРК. Между файлами мгновенная
                    // скорость падает в ноль на доли секунды, и показатель мигал
                    // «—» — читалось как «встало», хотя загрузка идёт. Прочерк
                    // остаётся только до первого замера.
                    if (_speed > 1024f) _speedLast = _speed;
                    ScreenUi.SetValue(_vSpeed, _speedLast > 0f ? Speed(_speedLast) : "—");
                    int filesLeft = t.batchTotal > 0 ? Mathf.Max(0, t.batchTotal - t.batchDone) : t.inflight;
                    int chLeft = 0;
                    if (Center != null) foreach (var e in Center.Queue) if (!e.Active) chLeft++;
                    ScreenUi.SetValue(_vQueue, chLeft > 0
                        ? LvnWords.Of("downloads.queue_chapters", "chapters {0}", chLeft) + " · "
                          + LvnWords.Of("downloads.queue_files", "files {0}", filesLeft)
                        : LvnWords.Of("downloads.queue_files", "files {0}", filesLeft));
                    ScreenUi.SetValue(_vGot, qTotal > 0
                        ? Mb(qDone + batchRec) + " " + LvnWords.Of("common.of", "of") + " " + Mb(qTotal)
                        : Mb(t.received) + (t.expected > 0 ? " " + LvnWords.Of("common.of", "of") + " " + Mb(t.expected) : ""));
                    // ОДИН ВОПРОС — ОДИН ОТВЕТ. Рядом стояли два числа из разных
                    // источников: «скачано X из Y» считал ПЛАН очереди, а
                    // «осталось» — правду с диска, и они честно расходились
                    // («94,8 из 139» при «осталось 60,1» — живой скрин). Пока
                    // очередь идёт, остаток — это ЕЁ остаток; сколько всего не
                    // на устройстве, отвечает «игра целиком» в настройках.
                    if (qTotal > 0)
                        ScreenUi.SetValue(_vLeft, Mb(System.Math.Max(0L, qTotal - (qDone + batchRec))));
                    else if (now - _lastMissingAt > 3f)
                    {
                        _lastMissingAt = now;
                        var miss = MissingInfo?.Invoke() ?? (0, 0);
                        ScreenUi.SetValue(_vLeft, miss.Item2 > 0 ? Lvn.Content.LvnBytes.Approx(miss.Item1)
                            : LvnWords.Of("downloads.all_done", "everything downloaded"));
                    }
                }
                if (_expanded && Center != null && _centerDirty) { _centerDirty = false; RebuildSections(animate: false); }
            }
            else if (_shown && !_expanded)
            {
                // Настоящий простой (работы нет) — мягко уходим, с запасом
                // против мигания на коротких паузах.
                if (_quietSince < 0f) _quietSince = now;
                if (now - _quietSince > 2f)
                {
                    _shown = false;
                    _quietSince = -1f;
                    _speed = 0f;
                    _speedLast = 0f;   // загрузка кончилась — прочерк снова честен
                    _capsule.experimental.animation.Start(1f, 0f, 200, (_, p) =>
                    {
                        _capsule.style.opacity = p;
                        if (p <= 0.01f) style.display = DisplayStyle.None;
                    });
                }
            }
        }

        private bool _shown;
        private float _quietSince = -1f;

        private bool _centerDirty;
        private DownloadCenter _watched;

        private DownloadCenter.Entry ActiveEntry()
        {
            if (Center == null) return null;
            // Подписка на очередь — лениво, когда центр появился у хоста.
            if (_watched != Center)
            {
                if (_watched != null) _watched.Changed -= MarkCenterDirty;
                _watched = Center;
                _watched.Changed += MarkCenterDirty;
            }
            foreach (var e in Center.Queue) if (e.Active) return e;
            return null;
        }

        private void MarkCenterDirty() => _centerDirty = true;

        /// <summary>Человеческая подпись того, что качается: класс файла
        /// словами игрока, не именем файла (решение Ильи: «скачиваем героиню
        /// и фаворитов», а не cr_transcoded_layer_0000).</summary>
        private static string Humanize(string url, string fallback)
        {
            if (string.IsNullOrEmpty(url))
                return string.IsNullOrEmpty(fallback) ? LvnWords.Of("dl.class_other", "Game files") : fallback;
            switch (Lvn.Content.DownloadPolicy.Classify(url))
            {
                case Lvn.Content.AssetClass.Actor: return LvnWords.Of("dl.class_actor", "Characters and outfits");
                case Lvn.Content.AssetClass.SceneBg: return LvnWords.Of("dl.class_scene_bg", "Scene backdrops");
                case Lvn.Content.AssetClass.ChapterBg: return LvnWords.Of("dl.class_chapter_bg", "Chapter screens");
                case Lvn.Content.AssetClass.Cover: return LvnWords.Of("dl.class_cover", "Story covers");
                case Lvn.Content.AssetClass.Audio: return LvnWords.Of("dl.class_audio", "Music and sound");
                case Lvn.Content.AssetClass.Script: return LvnWords.Of("dl.class_script", "Chapter text");
                case Lvn.Content.AssetClass.Ui: return LvnWords.Of("dl.class_ui", "Interface");
            }
            if (url.Contains("/sprites/")) return LvnWords.Of("dl.class_actor", "Characters and outfits");
            return LvnWords.Of("dl.class_other", "Game files");
        }

        // Правило показа размера переехало в дом (LvnBytes): здесь оно было
        // самым разумным из трёх — его и записали как общее.
        private static string Mb(long bytes) => Lvn.Content.LvnBytes.Short(bytes);

        // Правило скорости — там же, где правило размера: величина одна.
        private static string Speed(float bytesPerSec) => Lvn.Content.LvnBytes.Speed(bytesPerSec);

        /// <summary>Что рисуется внутри кольца: стрелка вниз (загрузка),
        /// «!» (офлайн при живой очереди), стрелка вверх (синхронизация —
        /// события уезжают на сервер).</summary>
        public enum RingGlyph { Down, Alert, Up }

        /// <summary>Хромовский значок загрузки чистым painter2D: глиф в центре
        /// и кольцо прогресса вокруг. Progress &lt; 0 — прогресс неизвестен:
        /// короткая дуга крутится сама (спиннер).</summary>
        private sealed class ProgressRing : VisualElement
        {
            private readonly float _radius, _stroke;
            private readonly bool _arrow;
            private float _progress = -1f;  // цель
            private float _shown = -1f;     // что нарисовано: плывёт к цели
            private float _spin;
            private float _lastTick;

            // Скорость вращения спиннера. Быстрее прежнего (было ~150°/с на
            // ровных тиках): короткая загрузка должна успеть показать ход, а не
            // мигнуть неподвижной дугой.
            private const float SpinDegreesPerSecond = 260f;

            // Как догоняет показанное — общая модель прогресса (та же, что у
            // бут-вуали и экрана загрузки): монотонно и по времени.
            private readonly Lvn.Content.LoadingProgressModel _model =
                new Lvn.Content.LoadingProgressModel(smoothRate: 5.5f);
            private RingGlyph _glyph = RingGlyph.Down;

            public RingGlyph Glyph
            {
                get => _glyph;
                set
                {
                    if (_glyph == value) return;
                    _glyph = value;
                    // Смена состояния — короткий пульс: глаз ловит перемену.
                    this.experimental.animation.Start(0f, 1f, LvnMotion.Ms(LvnMotion.Calm), (e, t) =>
                    {
                        float k = 1f + 0.14f * Mathf.Sin(t * Mathf.PI);
                        e.style.scale = new Scale(new Vector2(k, k));
                    });
                    MarkDirtyRepaint();
                }
            }

            public float Progress
            {
                get => _progress;
                set { _progress = value; MarkDirtyRepaint(); }
            }

            public ProgressRing(float radius, float stroke, bool drawArrow)
            {
                _radius = radius; _stroke = stroke; _arrow = drawArrow;
                generateVisualContent += Draw;
                // Спиннеру нужен ход времени; при известном прогрессе тик
                // просто перерисовывает свежую дугу.
                schedule.Execute(() =>
                {
                    // ХОД ПО ЧАСАМ, А НЕ ПО ТИКАМ. Угол считался прибавкой на
                    // каждый тик планировщика, а тик приходит с дрожанием: на
                    // коротких загрузках колесо дёргалось, будто подвисает.
                    // Время идёт ровно — и угол вместе с ним.
                    float now = Lvn.LvnClock.Now();
                    _spin = (now * SpinDegreesPerSecond) % 360f;
                    // Дуга не скачет между тиками данных (300 мс), а плывёт —
                    // ТОЙ ЖЕ моделью, что вуаль и экран загрузки. Здесь стояло
                    // своё сглаживание: одна работа («показанное догоняет
                    // настоящее, монотонно и по времени») жила двумя правилами,
                    // и «плавно» у кружка означало не то же, что у полосы.
                    if (_progress >= 0f)
                    {
                        float dt = _lastTick > 0f ? Mathf.Clamp(now - _lastTick, 0f, 0.25f) : 0.033f;
                        if (_shown < 0f) { _model.Reset(); _model.RaiseTo(_progress); }
                        _shown = _model.TickToward(_progress, dt);
                    }
                    else { _shown = -1f; _model.Reset(); }
                    _lastTick = now;
                    MarkDirtyRepaint();
                }).Every(16);
            }

            private void Draw(MeshGenerationContext mgc)
            {
                var p = mgc.painter2D;
                var c = new Vector2(resolvedStyle.width * 0.5f, resolvedStyle.height * 0.5f);

                // Фоновое кольцо.
                p.lineWidth = _stroke;
                p.strokeColor = LvnTokens.Faint;
                p.BeginPath();
                p.Arc(c, _radius, 0f, 360f);
                p.Stroke();

                // Дуга прогресса — от «12 часов» по часовой; спиннер — бегущая
                // четверть круга.
                p.strokeColor = LvnTokens.Accent;
                p.lineCap = LineCap.Round;
                p.BeginPath();
                if (_shown >= 0f)
                    p.Arc(c, _radius, -90f, -90f + 360f * Mathf.Clamp01(_shown));
                else
                    p.Arc(c, _radius, _spin, _spin + 90f);
                p.Stroke();

                if (!_arrow) return;
                float a = _radius * 0.52f;
                p.lineWidth = Mathf.Max(2f, _stroke * 0.8f);
                p.lineJoin = LineJoin.Round;
                if (_glyph == RingGlyph.Alert)
                {
                    // «!»: штрих + точка — сеть пропала, загрузка ждёт.
                    p.strokeColor = new Color(1f, 0.76f, 0.3f);
                    p.BeginPath();
                    p.MoveTo(new Vector2(c.x, c.y - a));
                    p.LineTo(new Vector2(c.x, c.y + a * 0.35f));
                    p.Stroke();
                    p.BeginPath();
                    p.Arc(new Vector2(c.x, c.y + a * 0.85f), p.lineWidth * 0.55f, 0f, 360f);
                    p.Stroke();
                    return;
                }
                // Стрелка (вниз — загрузка; вверх — синк) + полочка, как у Chrome.
                float dirY = _glyph == RingGlyph.Up ? -1f : 1f;
                p.strokeColor = LvnTokens.Text;
                p.BeginPath();
                p.MoveTo(new Vector2(c.x, c.y - a * dirY));
                p.LineTo(new Vector2(c.x, c.y + a * 0.55f * dirY));
                p.Stroke();
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - a * 0.6f, c.y - a * 0.05f * dirY));
                p.LineTo(new Vector2(c.x, c.y + a * 0.62f * dirY));
                p.LineTo(new Vector2(c.x + a * 0.6f, c.y - a * 0.05f * dirY));
                p.Stroke();
                p.BeginPath();
                p.MoveTo(new Vector2(c.x - a * 0.7f, c.y + a));
                p.LineTo(new Vector2(c.x + a * 0.7f, c.y + a));
                p.Stroke();
            }
        }
    }
}
