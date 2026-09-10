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

        private bool HasDeviceRows()
        {
            var titles = TitlesInfo?.Invoke();
            if (titles != null) return titles.Count > 0;
            var chapters = ChaptersInfo?.Invoke();
            return chapters != null && chapters.Count > 0;
        }

        /// <summary>Что на устройстве — по новеллам, если хост умеет их
        /// считать; иначе по главам (старый шов, тесты и чужие хосты).</summary>
        private void AddDeviceRows(VisualElement card)
        {
            var titles = TitlesInfo?.Invoke();
            if (titles != null)
            {
                foreach (var (title, cached, total) in titles) card.Add(TitleRow(title, cached, total));
                return;
            }
            var chapters = ChaptersInfo?.Invoke();
            if (chapters == null) return;
            foreach (var (label, cached) in chapters) card.Add(ChapterRow(label, cached));
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
