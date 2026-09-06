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
        private float _safeTop;

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
            _safeTop = units;
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
            _capsule.style.marginLeft = inGame ? Mathf.Lerp(104f, 16f, _morph) : 0f;
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

        private readonly VisualElement _capsule;
        private readonly ProgressRing _miniRing;
        private readonly VisualElement _full;
        private Label _file, _kind;
        private readonly Label _eta;
        private readonly VisualElement _info;
        private VisualElement _bar, _barFill;
        private Label _vSpeed, _vQueue, _vGot, _vLeft;
        private ScrollView _sections;
        private VisualElement _sectionCards;
        /// <summary>Текущий качаемый url — для человеческой подписи
        /// («Персонажи и наряды», а не имя файла).</summary>
        public Func<string> ActiveUrl;

        private bool _expanded;
        private float _morph;          // 0 = мини, 1 = полная (текущее положение)

        private readonly VisualElement _scrim;

        public DownloadHud()
        {
            pickingMode = PickingMode.Ignore;
            LvnChrome.Stretch(this);
            style.alignItems = Align.Center; // капсула — центр строки навбара
            style.display = DisplayStyle.None; // до первой работы кружка нет
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (_watched != null) _watched.Changed -= MarkCenterDirty;
                _watched = null;
            });
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

            var close = new Button(() => SetExpanded(false)) { text = "×", name = "download-close" };
            close.RegisterCallback<ClickEvent>(e => e.StopPropagation());
            LvnStyler.Plate(close, Color.clear, LvnTokens.TextDim, 12f);
            close.style.width = LvnTokens.Touch;
            close.style.height = LvnTokens.Touch;
            close.style.flexShrink = 0;
            head.Add(close);

            // В коротком окне прокручивается всё содержимое, кроме шапки:
            // статистика не выталкивает кнопки за нижнюю границу карточки.
            _sections = Lvn.UI.LvnScroll.Vertical();
            _sections.style.flexGrow = 1;
            _sections.style.minHeight = 0;
            _full.Add(_sections);

            var active = ScreenUi.Row();
            active.pickingMode = PickingMode.Ignore;
            ScreenUi.Row(active);
            active.style.marginTop = LvnTokens.Space2;
            _sections.Add(active);

            var col = new VisualElement();
            col.pickingMode = PickingMode.Ignore;
            col.style.flexGrow = 1; col.style.flexShrink = 1;
            active.Add(col);

            _file = new Label("") { name = "download-title" };
            _file.pickingMode = PickingMode.Ignore;
            _file.style.color = LvnTokens.Text;
            _file.style.fontSize = LvnTokens.TextSm;
            _file.style.unityFontStyleAndWeight = FontStyle.Bold;
            _file.style.whiteSpace = WhiteSpace.Normal;
            col.Add(_file);

            _kind = new Label("") { name = "download-detail" };
            _kind.pickingMode = PickingMode.Ignore;
            _kind.style.color = LvnTokens.TextDim;
            _kind.style.fontSize = LvnTokens.TextXs;
            _kind.style.marginTop = LvnTokens.Hair;
            _kind.style.whiteSpace = WhiteSpace.Normal;
            col.Add(_kind);

            // ПОЛОСА — ДЛЯ ГЛАЗА, КОЛЬЦО — ДЛЯ УГЛА ЭКРАНА. Кольцо в кружке
            // отвечает «идёт ли», но «сколько осталось» глаз читает с полосы
            // быстрее: у неё есть край, до которого видно расстояние. Живой
            // репорт был именно про это — «прогресса загрузки не видно».
            _bar = new VisualElement();
            _bar.pickingMode = PickingMode.Ignore;
            _bar.style.height = 6;
            _bar.style.marginTop = LvnTokens.Space1;
            _bar.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Edged(_bar, 3);
            _barFill = new VisualElement();
            _barFill.pickingMode = PickingMode.Ignore;
            _barFill.style.height = 6;
            _barFill.style.width = Length.Percent(0);
            _barFill.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.Edged(_barFill, 3);
            _bar.Add(_barFill);
            col.Add(_bar);
            _eta = new Label { name = "download-eta" };
            _eta.style.color = LvnTokens.TextDim;
            _eta.style.fontSize = LvnTokens.TextXs;
            _eta.style.marginTop = LvnTokens.Space1;
            col.Add(_eta);

            // Поля — ТАБЛИЦЕЙ, по строке на факт (не лапшой через «·»):
            // скорость, очередь, скачано, осталось — всё, что просилось.
            // Поля — матрицей 2×2 (уточнение Ильи 26.08): компактнее, меньше
            // высоты, читается блоком.
            var info = _info = new VisualElement { name = "download-metrics" };
            info.pickingMode = PickingMode.Ignore;
            info.style.marginTop = LvnTokens.Space2;
            info.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Edged(info, LvnTokens.Radius);
            LvnAir.PadX(info, LvnTokens.Space2);
            info.style.paddingBottom = LvnTokens.Space1;
            info.style.paddingTop = LvnTokens.Space2;
            LvnFlow.Wrap(info);
            _sections.Add(info);
            _vSpeed = InfoCell(info, () => LvnWords.Of("dl.speed", "Speed"));
            _vQueue = InfoCell(info, () => LvnWords.Of("dl.next", "Next in queue"));
            _vGot   = InfoCell(info, () => LvnWords.Of("dl.done", "Downloaded"));
            _vLeft  = InfoCell(info, () => LvnWords.Of("dl.bytes_left", "Left to download"));
            _vSpeed.name = "download-speed";
            _vQueue.name = "download-queue";
            _vGot.name = "download-received";
            _vLeft.name = "download-left";

            // Секции (офлайн-правила, синк, очередь глав, «скачать всё») —
            // перестраиваются при развороте и по изменению очереди.
            _sectionCards = new VisualElement();
            _sectionCards.style.marginTop = LvnTokens.Space2;
            _sections.Add(_sectionCards);

            ApplyMorph(0f);
        }


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
                set
                {
                    // Монотонность модели — внутри пакета, не между ними.
                    if (value < _progress) { _model.Reset(); _shown = -1f; }
                    _progress = value;
                    MarkDirtyRepaint();
                }
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
                    if (_glyph != RingGlyph.Alert) _spin = (now * SpinDegreesPerSecond) % 360f;
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
                    p.strokeColor = LvnTokens.Warn;
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
