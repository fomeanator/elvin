using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЧТО НАПИСАНО В ЛИСТЕ — очередь, отказы, что уже на устройстве, кнопки.
    ///
    /// <para>Список — как в Steam и торрент-клиентах, а не как журнал: каждая
    /// строка — плашка с именем, размером и состоянием; у той, что качается
    /// сейчас, — полоса хода и скорость; главы на устройстве свёрнуты в
    /// новеллы («12 из 12 глав»), а не перечислены по одной («список это
    /// пизда» — Илья 10.09).</para>
    ///
    /// <para>Вёрстка живёт отдельно от механики загрузки: лист пересобирается
    /// на каждое изменение состояния, и держать её рядом с подсчётом скорости
    /// значило смешивать «сколько байт пришло» с «какой отступ у заголовка».</para>
    /// </summary>
    public sealed partial class DownloadHud
    {
        // ── секции листа ──────────────────────────────────────────────────────

        private void RebuildSections()
        {
            if (_sectionCards == null) return;
            _sectionCards.Clear();
            _activeFill = null;
            _activeMeta = null;
            _mFps = _mRam = _mCpu = _mDisk = null;
            _fpsChart = null;

            // ДВА ВИДА, А НЕ ОДИН ДЛИННЫЙ ЛИСТ. Показатели устройства нужны не
            // всегда, а очередь — всегда; сложенные в одну прокрутку, они
            // мешали друг другу («переключатель, не аккордеон», «тогда и
            // список загрузок нормально показать можно» — Илья 10.09).
            _sectionCards.Add(ViewSwitch());
            if (_deviceView)
            {
                var card = Section(() => LvnWords.Of("dl.device_title", "This device"));
                card.Add(DeviceMetrics());
                return;
            }

            bool off = Offline?.Invoke() ?? false;
            int pend = PendingOps?.Invoke() ?? 0;

            if (off)
            {
                var card = Section(() => LvnWords.Of("dl.offline_title", "Play offline"));
                card.Add(Hint(() => LvnWords.Of("dl.offline_available", "Ticked chapters have all their files saved on this device.")));
                AddDeviceRows(card);
            }

            if (pend > 0)
            {
                var card = Section(() => LvnWords.Of("dl.pending_title", "Waiting to send"));
                card.Add(Hint(() => off
                    ? LvnWords.Of("dl.sync_offline", "Progress is saved on this device. Sync will resume when connected.")
                    : LvnWords.Of("dl.sync_saved", "Progress is saved on this device. Syncing with the server.")));
            }

            if (Center != null && Center.Failed.Count > 0)
            {
                var card = Section(() => LvnWords.Of("dl.failed", "Download incomplete"));
                foreach (var entry in Center.Failed) card.Add(QueueRow(entry, failed: true));
            }

            if (Center != null && Center.Queue.Count > 0)
            {
                var card = Section(() => LvnWords.Of("dl.queue_title", "Download queue"));
                foreach (var e in Center.Queue) card.Add(QueueRow(e, failed: false));
            }

            // СОСТОЯНИЕ УСТРОЙСТВА — рядом с загрузкой, как в клиенте Steam:
            // одной строкой видно, тянет ли телефон то, что мы качаем. Кадры и
            // память отвечают на вопрос «почему подтормаживает», место на
            // диске — «влезет ли остальное» («можно даже запись и сколько
            // оперативной памяти жрёт показывать… и кадры» — Илья 10.09).
            // КАЧЕСТВО — ВЫБОР С ЦЕНОЙ, А НЕ ТРИ БУКВЫ. Ступень стоит места на
            // телефоне, и назвать её цену в мегабайтах — единственный честный
            // способ дать выбрать («качество 1к — 360 МБ, 1.4к — 700 МБ, 2к —
            // 1.5 гига» — Илья 10.09). Пока сервер не назвал весов, раздела
            // нет вовсе: выдуманные мегабайты хуже их отсутствия.
            if (Lvn.Content.LvnCatalogSize.Known && CatalogUrls != null)
            {
                var card = Section(() => LvnWords.Of("dl.quality_title", "Art quality"));
                card.Add(Hint(() => LvnWords.Of("dl.quality_hint",
                    "Lower quality saves space on this device. Already downloaded files stay.")));
                foreach (var q in new[] { "1k", "1440", "2k" })
                {
                    string quality = q;
                    long bytes = Lvn.Content.LvnCatalogSize.Total(CatalogUrls(), quality);
                    card.Add(QualityRow(quality, bytes));
                }
            }

            // ЧТО УЖЕ НА УСТРОЙСТВЕ — и в сети тоже: при живой сети под графиком
            // пустовала половина листа, а вопрос «что у меня скачано» оставался
            // без ответа. Идёт последним: очередь и отказы важнее.
            if (!off && HasDeviceRows())
            {
                var card = Section(() => LvnWords.Of("dl.on_device", "On this device"));
                AddDeviceRows(card);
            }

            RebuildActions();
        }

        /// <summary>
        /// КНОПКИ ЛИСТА — над прокруткой, не в её хвосте: «Скачать всю игру» —
        /// то, ради чего лист открывают, и оно обязано быть видно без листания.
        /// Пока очередь идёт, вместо неё «Остановить»: снимает всё из очереди.
        /// </summary>
        private void RebuildActions()
        {
            if (_actions == null) return;
            _actions.Clear();
            bool off = Offline?.Invoke() ?? false;
            bool running = Center != null && Center.Queue.Count > 0;
            if (running)
            {
                var stop = Lvn.UI.LvnRedress.Bind(new Button { name = "download-stop" },
                    () => Up(LvnWords.Of("dl.stop", "Stop downloading")));
                stop.style.height = LvnTokens.Touch;
                stop.style.fontSize = LvnTokens.TextSm;
                stop.style.unityTextAlign = TextAnchor.MiddleCenter;
                DressButton(stop, primary: false);
                stop.clicked += () =>
                {
                    foreach (var e in new List<DownloadCenter.Entry>(Center.Queue)) Center.Remove(e);
                };
                _actions.Add(stop);
                return;
            }
            var missing = MissingInfo?.Invoke() ?? (0, 0);
            // ПРЯЧЕТ ТОЛЬКО ЖИВАЯ ОЧЕРЕДЬ. Отказавшиеся файлы её не прячут:
            // один 404 убирал предложение «вся игра с собой» целиком, а связи
            // между исчезнувшей кнопкой и красной строкой ниже игрок не видит.
            // Именно тогда оно и нужно — докачать то, что не доехало.
            if (missing.Item2 <= 0 || DownloadAll == null)
            {
                _actions.Add(Hint(() => LvnWords.Of("dl.all_done", "The whole game is on this device.")));
                return;
            }
            var hint = Hint(() => LvnWords.Of("dl.all_hint", "Download once and play with no network: chapters, art and music stay on the device."));
            hint.style.marginBottom = LvnTokens.Space1;
            _actions.Add(hint);
            var row = ScreenUi.Row();
            bool partial = HasSomeDownloaded?.Invoke() ?? false;
            var btn = new Button { name = "download-all", text = Up(
                (partial ? LvnWords.Of("dl.resume", "Finish downloading") : LvnWords.Of("dl.get_all", "Download the whole game"))
                + " · " + Lvn.Content.LvnBytes.Approx(missing.Item1)) };
            btn.style.height = LvnTokens.TouchLg;
            btn.style.fontSize = LvnTokens.TextSm;
            btn.style.unityFontStyleAndWeight = FontStyle.Bold;
            // Без темы-USS у кнопки нет умолчаний: текст лип к левому краю.
            btn.style.unityTextAlign = TextAnchor.MiddleCenter;
            btn.style.flexGrow = 1;
            DressButton(btn, primary: true);
            btn.SetEnabled(!off);
            btn.clicked += () => { btn.SetEnabled(false); Lvn.LvnAsync.Fire(DownloadAll(), "DownloadAll"); };
            row.Add(btn);
            var offer = CurrentChapterOffer?.Invoke();
            if (offer != null)
            {
                var chBtn = new Button { text = Up(offer.Value.label), name = "download-chapter" };
                chBtn.style.height = LvnTokens.TouchLg;
                chBtn.style.fontSize = LvnTokens.TextXs;
                chBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
                LvnAir.PadX(chBtn, LvnTokens.Space2);
                chBtn.style.marginLeft = LvnTokens.Space1;
                chBtn.SetEnabled(!off);
                DressButton(chBtn, primary: false);
                var startCh = offer.Value.start;
                chBtn.clicked += () => { chBtn.SetEnabled(false); startCh(); };
                row.Add(chBtn);
            }
            _actions.Add(row);
        }

        // ── ряды ──────────────────────────────────────────────────────────────

        // СНИМОК ДИСКА, А НЕ ОБХОД НА КАЖДУЮ ПЕРЕСБОРКУ. «На устройстве» — это
        // File.Exists на каждый файл каждой главы; лист пересобирается на
        // каждое событие очереди, и во время «скачать всю игру» обход шёл бы
        // сотнями stat-ов с каждой доехавшей главой, на телефоне — рывком в
        // кадре. Снимок берётся на развороте листа и на границах работы
        // (начало и конец очереди): между ними на диске меняется только то,
        // что лист и так показывает очередью.
        private List<(string title, int cached, int total)> _deviceRows;
        private List<(string label, bool cached)> _deviceChapters;

        private void TakeDeviceSnapshot()
        {
            if (_deviceRows != null || _deviceChapters != null) return;
            _deviceRows = TitlesInfo?.Invoke();
            if (_deviceRows == null) _deviceChapters = ChaptersInfo?.Invoke();
        }

        private bool HasDeviceRows()
        {
            TakeDeviceSnapshot();
            if (_deviceRows != null) return _deviceRows.Count > 0;
            return _deviceChapters != null && _deviceChapters.Count > 0;
        }

        /// <summary>Что на устройстве — по новеллам, если хост умеет их
        /// считать; иначе по главам (старый шов, тесты и чужие хосты).</summary>
        private void AddDeviceRows(VisualElement card)
        {
            TakeDeviceSnapshot();
            if (_deviceRows != null)
            {
                foreach (var (title, cached, total) in _deviceRows) card.Add(TitleRow(title, cached, total));
                return;
            }
            if (_deviceChapters == null) return;
            foreach (var (label, cached) in _deviceChapters) card.Add(ChapterRow(label, cached));
        }

        /// <summary>Ряд очереди или отказа — как строка торрента: имя, размер,
        /// состояние; у активной — полоса хода и скорость под ней, у отказа —
        /// «Повторить». Крестик снимает ряд.</summary>
        private VisualElement QueueRow(DownloadCenter.Entry e, bool failed)
        {
            bool active = e.Active && !failed;
            var row = RowPlate(active);
            var line = ScreenUi.Row(spread: true);
            line.pickingMode = PickingMode.Ignore;
            row.Add(line);

            var name = RowName(e.Label, failed ? LvnTokens.Warn : LvnTokens.Text);
            line.Add(name);

            var right = ScreenUi.Row();
            right.style.flexShrink = 0;
            line.Add(right);
            if (failed)
            {
                var size = RowMeta(LvnBytes.Approx(e.Bytes));
                size.style.marginRight = LvnTokens.Space1;
                right.Add(size);
            }
            if (failed)
            {
                var retry = new Button(() => Center?.Retry(e))
                {
                    text = Up(LvnWords.Of("dl.retry", "Retry")), name = "download-retry"
                };
                retry.style.minHeight = LvnTokens.Touch;
                retry.style.fontSize = LvnTokens.TextXs;
                retry.style.unityTextAlign = TextAnchor.MiddleCenter;
                LvnAir.PadX(retry, LvnTokens.Space2);
                retry.style.flexShrink = 0;
                DressButton(retry, primary: false);
                retry.SetEnabled(!(Offline?.Invoke() ?? false));
                right.Add(retry);
            }
            right.Add(RemoveCross(() => Center?.Remove(e)));

            // ОДНА ВЫСОТА У ВСЕХ РЯДОВ ОЧЕРЕДИ: полоса и подпись есть у каждого,
            // у ждущих полоса пуста. Иначе с переходом хода к следующей главе
            // ряды ниже прыгали на строку («всё как-то прыгает» — Ваня 10.09).
            if (!failed)
            {
                var bar = MakeBar(out var fill);
                bar.style.marginTop = LvnTokens.Space1;
                row.Add(bar);
                var meta = RowMeta(active ? "" : LvnBytes.Approx(e.Bytes) + " · " + LvnWords.Of("dl.in_queue", "queued"));
                meta.style.marginTop = LvnTokens.Hair;
                row.Add(meta);
                if (active) { _activeFill = fill; _activeMeta = meta; }
            }
            return row;
        }

        /// <summary>Ряд новеллы на устройстве: имя, «N из M глав», полоса.</summary>
        private VisualElement TitleRow(string title, int cached, int total)
        {
            var row = RowPlate(chosen: false);
            row.pickingMode = PickingMode.Ignore;
            var line = ScreenUi.Row(spread: true);
            line.pickingMode = PickingMode.Ignore;
            row.Add(line);
            line.Add(RowName(title, cached >= total && total > 0 ? ChosenInk : LvnTokens.Text));
            var counter = RowMeta(LvnWords.Of("dl.chapters_of", "{0} of {1} chapters", cached, total));
            counter.style.flexShrink = 0;
            counter.style.marginLeft = LvnTokens.Space1;
            line.Add(counter);
            var bar = MakeBar(out var fill);
            bar.style.marginTop = LvnTokens.Space1;
            fill.style.width = Length.Percent(total > 0 ? Mathf.Clamp01((float)cached / total) * 100f : 0f);
            row.Add(bar);
            return row;
        }

        /// <summary>Глава на устройстве — старый ряд с меткой; остаётся для
        /// хостов, что дают только список глав.</summary>
        private VisualElement ChapterRow(string label, bool cached)
        {
            var row = RowPlate(chosen: false);
            row.pickingMode = PickingMode.Ignore;
            var line = ScreenUi.Row(spread: true);
            line.pickingMode = PickingMode.Ignore;
            row.Add(line);
            line.Add(RowName(label, cached ? LvnTokens.Text : LvnTokens.TextDim));
            var mark = RowMeta(cached ? "√" : "○");
            mark.style.color = cached ? ChosenInk : LvnTokens.TextDim;
            line.Add(mark);
            return row;
        }

        // ── детали рядов ──────────────────────────────────────────────────────

        /// <summary>Плашка ряда: тон панели, скругление; выбранной — грань
        /// цветом облика.</summary>
        /// <summary>Строка выбора качества: название ступени, её цена в
        /// мегабайтах и отметка выбранного. Выбор пишется в настройки — тот же
        /// ключ, что в настройках устройства, второго хозяина у него нет.</summary>
        /// <summary>Кадры, память и место — тремя ячейками, как показатели
        /// загрузки выше. Обновляются тиком окна, а не пересборкой листа:
        /// цифра, которая меняется каждую секунду, не должна двигать строки.</summary>
        /// <summary>Переключатель вида: загрузки или устройство. Одна строка
        /// из двух слов — выбранное золотом, второе приглушено.</summary>
        private VisualElement ViewSwitch()
        {
            var row = ScreenUi.Row();
            row.style.marginBottom = LvnTokens.Space2;
            VisualElement Tab(System.Func<string> text, bool on, System.Action tap)
            {
                var b = Lvn.UI.LvnRedress.Bind(new Button(() => tap()), text);
                b.style.fontSize = LvnTokens.TextSm;
                LvnAir.PadY(b, LvnTokens.Space1);
                b.style.marginRight = LvnTokens.Space2;
                LvnStyler.Tab(b, on, LvnTokens.RadiusSm);
                return b;
            }
            row.Add(Tab(() => LvnWords.Of("dl.view_downloads", "Downloads"), !_deviceView,
                        () => { _deviceView = false; RebuildSections(); }));
            row.Add(Tab(() => LvnWords.Of("dl.device_title", "This device"), _deviceView,
                        () => { _deviceView = true; RebuildSections(); }));
            return row;
        }

        private bool _deviceView;

        private VisualElement DeviceMetrics()
        {
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnFlow.Wrap(row);
            _mFps = InfoCell(row, () => LvnWords.Of("dl.fps", "Frames"));
            _mRam = InfoCell(row, () => LvnWords.Of("dl.ram", "Memory"));
            _mCpu = InfoCell(row, () => LvnWords.Of("dl.cpu", "Frame time"));
            _mDisk = InfoCell(row, () => LvnWords.Of("dl.disk", "Free space"));
            box.Add(row);

            // ГРАФИК КАДРОВ — тем же домом, что и график скорости: одна
            // картинка минуты жизни, только по другой оси. Просадка видна
            // глазом, а не вычитается из двух чисел.
            _fpsChart = new TrafficChart { pickingMode = PickingMode.Ignore };
            _fpsChart.style.height = 56f;
            _fpsChart.style.marginTop = LvnTokens.Space2;
            box.Add(_fpsChart);
            return box;
        }

        private Label _mFps, _mRam, _mCpu, _mDisk;
        private TrafficChart _fpsChart;

        /// <summary>Обновить показатели устройства. Кадры — сглаженные, иначе
        /// число прыгает и читать его нечем; память — то, что заняла игра;
        /// место — сколько свободно под остальную игру.</summary>
        private void TickDeviceMetrics()
        {
            if (_mFps == null) return;
            float dt = Time.smoothDeltaTime;
            int fps = dt > 0f ? Mathf.RoundToInt(1f / dt) : 0;
            ScreenUi.SetText(_mFps, fps > 0 ? fps.ToString() : "—");
            // «Проц» на телефоне честнее считать ВРЕМЕНЕМ КАДРА: доля ядер
            // недоступна приложению, а миллисекунды на кадр — та же нагрузка,
            // только измеримая.
            ScreenUi.SetText(_mCpu, dt > 0f ? Mathf.RoundToInt(dt * 1000f) + " ms" : "—");
            long ram = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            ScreenUi.SetText(_mRam, ram > 0 ? Mb(ram) : "—");
            ScreenUi.SetText(_mDisk, FreeDiskBytes() is long free && free > 0 ? Mb(free) : "—");
            // График кадров живёт своей шкалой: дом рисует «байты в секунду»,
            // а мы кормим его кадрами — картинка та же, подпись своя.
            _fpsChart?.Add(Lvn.LvnClock.Wall(), fps, 0f);
        }

        /// <summary>Сколько места осталось на устройстве. Спрашиваем РЕДКО:
        /// обращение к файловой системе стоит миллисекунды, а число меняется
        /// медленнее, чем кадры.</summary>
        private long? FreeDiskBytes()
        {
            float now = Lvn.LvnClock.Wall();
            if (_diskAskedAt > 0f && now - _diskAskedAt < 5f) return _diskFree;
            _diskAskedAt = now;
            try
            {
                var root = System.IO.Path.GetPathRoot(Application.persistentDataPath);
                if (!string.IsNullOrEmpty(root))
                    _diskFree = new System.IO.DriveInfo(root).AvailableFreeSpace;
            }
            catch { _diskFree = null; }   // платформа не отвечает — покажем прочерк
            return _diskFree;
        }

        private float _diskAskedAt;
        private long? _diskFree;

        private VisualElement QualityRow(string quality, long bytes)
        {
            bool chosen = (string.IsNullOrEmpty(LvnPrefs.ArtQuality)
                           ? Lvn.UI.Screens.NovelApp.EffectiveArtQuality()
                           : LvnPrefs.ArtQuality) == quality;
            var row = RowPlate(chosen);
            var name = new Label(quality == "1k" ? "1K" : quality == "1440" ? "1440p" : "2K");
            name.style.color = chosen ? LvnTokens.Gold : LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextSm;
            name.style.flexGrow = 1;
            row.Add(name);
            var size = new Label(bytes > 0 ? "≈ " + Mb(bytes) : "");
            size.style.color = LvnTokens.TextDim;
            size.style.fontSize = LvnTokens.TextXs;
            row.Add(size);
            row.AddManipulator(new Clickable(() =>
            {
                LvnPrefs.ArtQuality = quality;
                RebuildSections();
            }));
            LvnMotion.Tappable(row);
            return row;
        }

        private VisualElement RowPlate(bool chosen)
        {
            var row = new VisualElement();
            row.style.flexShrink = 0;
            row.style.marginTop = LvnTokens.Space1;
            LvnAir.Pad(row, LvnTokens.Space2, LvnTokens.Space1);
            LvnStyler.Plate(row, UiColor.WithAlpha(LvnTokens.PanelBg, 0.82f), LvnTokens.Text, LvnTokens.RadiusSm);
            if (chosen) LvnStyler.Chosen(row, true, ChosenInk);
            return row;
        }

        private Label RowName(string text, Color ink)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.style.color = ink;
            l.style.fontSize = LvnTokens.TextSm;
            l.style.flexGrow = 1;
            l.style.flexShrink = 1;
            l.style.minWidth = 0;
            l.style.overflow = Overflow.Hidden;
            l.style.textOverflow = TextOverflow.Ellipsis;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            if (StageDressed) LvnFonts.Apply(l, LvnFonts.Display);
            return l;
        }

        private Label RowMeta(string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.style.color = LvnTokens.TextDim;
            l.style.fontSize = LvnTokens.TextXs;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            if (StageDressed) LvnFonts.Apply(l, LvnFonts.Display);
            return l;
        }

        private Button RemoveCross(Action onTap)
        {
            var remove = new Button(onTap) { text = "×" };
            LvnStyler.Plate(remove, Color.clear, LvnTokens.TextDim, LvnTokens.RadiusSm);
            remove.style.width = LvnTokens.Touch;
            remove.style.height = LvnTokens.Touch;
            remove.style.fontSize = LvnTokens.TextSm;
            remove.style.unityTextAlign = TextAnchor.MiddleCenter;
            remove.style.flexShrink = 0;
            return remove;
        }

        /// <summary>Полоса ряда: облика — полоса макета, иначе дорожка темы.</summary>
        private VisualElement MakeBar(out VisualElement fill)
        {
            if (StageDressed) return LvnStageKit.Progress(out fill);
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.height = LvnTokens.Hair * 2f;
            bar.style.backgroundColor = LvnTokens.Track;
            LvnChrome.Edged(bar, LvnTokens.Hair);
            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.style.height = LvnTokens.Hair * 2f;
            fill.style.width = Length.Percent(0f);
            fill.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.Edged(fill, LvnTokens.Hair);
            bar.Add(fill);
            return bar;
        }

        /// <summary>Секция листа: заголовок и ряды под ним. Без облика — карточка
        /// тона темы; с обликом ряды сами плашки, и карточке нечего добавить.</summary>
        private VisualElement Section(Func<string> heading)
        {
            var card = new VisualElement();
            card.style.flexShrink = 0;
            card.style.marginBottom = LvnTokens.Space2;
            if (!StageDressed)
            {
                card.style.backgroundColor = LvnTokens.Faint;
                LvnChrome.Edged(card, LvnTokens.Radius);
                LvnAir.Pad(card, LvnTokens.Space2);
            }
            card.Add(CardHeading(heading));
            _sectionCards.Add(card);
            return card;
        }

        // Ячейка сводки: подпись тускло сверху, значение жирно снизу. Подпись
        // берётся источником: сведения обновляются каждый тик, а их НАЗВАНИЯ
        // ставились один раз при сборке и смену языка не переживали.
        private Label InfoCell(VisualElement host, Func<string> caption)
        {
            var cell = new VisualElement();
            cell.pickingMode = PickingMode.Ignore;
            cell.style.width = Length.Percent(33.3f);
            cell.style.minWidth = 0;
            cell.style.paddingRight = LvnTokens.Space1;
            cell.style.marginBottom = LvnTokens.Space1;
            var c = Lvn.UI.LvnRedress.Bind(new Label(), caption);
            c.pickingMode = PickingMode.Ignore;
            c.style.color = LvnTokens.TextDim;
            c.style.fontSize = LvnTokens.TextMicro;
            c.style.whiteSpace = WhiteSpace.Normal;
            cell.Add(c);
            _captions.Add(c);
            var v = new Label("—");
            v.pickingMode = PickingMode.Ignore;
            v.style.color = LvnTokens.Text;
            v.style.fontSize = LvnTokens.TextXs;
            v.style.unityFontStyleAndWeight = FontStyle.Bold;
            v.style.marginTop = LvnTokens.Hair;
            v.style.whiteSpace = WhiteSpace.Normal;
            cell.Add(v);
            host.Add(cell);
            return v;
        }

        /// <summary>
        /// ЗАГОЛОВОК СЕКЦИИ — не заголовок экрана (тот живёт у оболочки
        /// экранов и вдвое крупнее). С обликом — золотом, шрифтом витрины.
        ///
        /// <para>ИСТОЧНИК, А НЕ ГОТОВАЯ СТРОКА: готовая обрывает связь со
        /// словарём, и при смене языка на лету заголовки оставались на прежнем
        /// языке до ближайшей смены данных.</para>
        /// </summary>
        private Label CardHeading(Func<string> text)
        {
            var l = Lvn.UI.LvnRedress.Bind(new Label(), text);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = StageDressed ? LvnTokens.Gold : LvnTokens.Text;
            l.style.fontSize = LvnTokens.TextSm;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginBottom = LvnTokens.Tight;
            if (StageDressed) LvnFonts.Apply(l, LvnFonts.Display);
            return l;
        }

        /// <summary>Пояснение под заголовком — тоже от источника, по той же
        /// причине.</summary>
        private Label Hint(Func<string> text)
        {
            var l = Lvn.UI.LvnRedress.Bind(new Label(), text);
            l.pickingMode = PickingMode.Ignore;
            ScreenUi.Quiet(l, LvnTokens.TextXs);
            return l;
        }
    }
}
