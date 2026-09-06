using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЧТО НАПИСАНО В ПАНЕЛИ — карточки, строки глав и очереди.
    ///
    /// <para>Вёрстка живёт отдельно от механики загрузки: панель пересобирается
    /// на каждое изменение состояния, и держать её рядом с подсчётом скорости
    /// значило смешивать «сколько байт пришло» с «какой отступ у заголовка».
    /// Здесь только то, как это выглядит и в каком порядке появляется.</para>
    /// </summary>
    public sealed partial class DownloadHud
    {
        // ── секции попапа ─────────────────────────────────────────────────────

        private void RebuildSections()
        {
            if (_sectionCards == null) return;
            _sectionCards.Clear();

            bool off = Offline?.Invoke() ?? false;
            int pend = PendingOps?.Invoke() ?? 0;

            if (off)
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.offline_title", "Play offline")));
                card.Add(Hint(() => LvnWords.Of("dl.offline_available", "Ticked chapters have all their files saved on this device.")));
                var chapters = ChaptersInfo?.Invoke();
                if (chapters != null)
                    foreach (var (label, cached) in chapters)
                        card.Add(ChapterRow(label, cached));
                _sectionCards.Add(card);
            }

            if (pend > 0)
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.pending_title", "Waiting to send")));
                card.Add(Hint(() => off
                    ? LvnWords.Of("dl.sync_offline", "Progress is saved on this device. Sync will resume when connected.")
                    : LvnWords.Of("dl.sync_saved", "Progress is saved on this device. Syncing with the server.")));
                _sectionCards.Add(card);
            }

            if (Center != null && Center.Failed.Count > 0)
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.failed", "Download incomplete")));
                foreach (var entry in Center.Failed) card.Add(QueueRow(entry, failed: true));
                _sectionCards.Add(card);
            }

            if (Center != null && Center.Queue.Count > 0)
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.queue_title", "Download queue")));
                foreach (var e in Center.Queue)
                    card.Add(QueueRow(e));
                _sectionCards.Add(card);
            }

            var missing = MissingInfo?.Invoke() ?? (0, 0);
            // ПРЯЧЕТ ТОЛЬКО ЖИВАЯ ОЧЕРЕДЬ. Отказавшиеся файлы её не прячут:
            // один 404 убирал предложение «вся игра с собой» целиком, а связи
            // между исчезнувшей кнопкой и красной строкой ниже игрок не видит.
            // Именно тогда оно и нужно — докачать то, что не доехало.
            if (missing.Item2 > 0 && DownloadAll != null
                && !(Center != null && Center.Queue.Count > 0))
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.all_title", "The whole game with you")));
                card.Add(Hint(() => LvnWords.Of("dl.all_hint", "Download once and play with no network: chapters, art and music stay on the device.")));
                var offer = CurrentChapterOffer?.Invoke();
                if (offer != null)
                {
                    var chBtn = new Button { text = offer.Value.label };
                    chBtn.style.height = LvnTokens.Touch;
                    chBtn.style.fontSize = LvnTokens.TextXs;
                    chBtn.style.marginTop = LvnTokens.Space1;
                    chBtn.SetEnabled(!off);
                    LvnStyler.Plate(chBtn, LvnTokens.Faint, LvnTokens.Accent, 14f);
                    var startCh = offer.Value.start;
                    chBtn.clicked += () => { chBtn.SetEnabled(false); startCh(); };
                    card.Add(chBtn);
                }
                bool partial = HasSomeDownloaded?.Invoke() ?? false;
                var btn = new Button { text =
                    (partial ? LvnWords.Of("dl.resume", "Finish downloading") : LvnWords.Of("dl.get_all", "Download all"))
                    + " " + Lvn.Content.LvnBytes.Approx(missing.Item1) };
                btn.style.height = LvnTokens.Touch;
                btn.style.fontSize = LvnTokens.TextSm;
                btn.style.marginTop = LvnTokens.Space1;
                LvnStyler.Primary(btn, 14f);
                btn.SetEnabled(!off);
                btn.clicked += () => { btn.SetEnabled(false); Lvn.LvnAsync.Fire(DownloadAll(), "DownloadAll"); };
                card.Add(btn);
                _sectionCards.Add(card);
            }
        }

        private VisualElement SectionCard()
        {
            var card = new VisualElement();
            card.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Edged(card, LvnTokens.Radius); // кромка + скругление: карточка, не пятно
            LvnAir.Pad(card, LvnTokens.Space2);
            card.style.marginBottom = LvnTokens.Space2;
            return card;
        }

        // Ячейка 2×2: подпись тускло сверху, значение жирно снизу.
        // Подпись ячейки берётся источником: сведения о загрузке обновляются
        // каждый тик, а вот их НАЗВАНИЯ ставились один раз при сборке панели и
        // смену языка не переживали.
        private Label InfoCell(VisualElement host, System.Func<string> caption)
        {
            var cell = new VisualElement();
            cell.pickingMode = PickingMode.Ignore;
            cell.style.width = Length.Percent(50f);
            cell.style.minWidth = 0;
            cell.style.paddingRight = LvnTokens.Space1;
            cell.style.marginBottom = LvnTokens.Space1;
            var c = Lvn.UI.LvnRedress.Bind(new Label(), caption);
            c.pickingMode = PickingMode.Ignore;
            c.style.color = LvnTokens.TextDim;
            c.style.fontSize = LvnTokens.TextMicro;
            c.style.whiteSpace = WhiteSpace.Normal;
            cell.Add(c);
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
        /// ЗАГОЛОВОК КАРТОЧКИ — не заголовок экрана (тот живёт у оболочки
        /// экранов и вдвое крупнее). Раньше звался так же, `SectionTitle`, и
        /// это была ловушка: два разных размера под одним именем в одном
        /// пространстве имён.
        ///
        /// <para>ИСТОЧНИК, А НЕ ГОТОВАЯ СТРОКА. Готовая обрывает связь со
        /// словарём: смена языка на лету перестраивала всё вокруг, а «Играть
        /// офлайн», «Ждут отправки» и «Очередь загрузки» оставались на прежнем
        /// языке до ближайшей смены данных.</para>
        /// </summary>
        private Label CardHeading(System.Func<string> text)
        {
            var l = Lvn.UI.LvnRedress.Bind(new Label(), text);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.Text;
            l.style.fontSize = LvnTokens.TextSm;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginBottom = LvnTokens.Tight;
            return l;
        }

        /// <summary>Пояснение под заголовком карточки — тоже от источника, по
        /// той же причине.</summary>
        private Label Hint(System.Func<string> text)
        {
            var l = Lvn.UI.LvnRedress.Bind(new Label(), text);
            l.pickingMode = PickingMode.Ignore;
            ScreenUi.Quiet(l, LvnTokens.TextXs);
            return l;
        }

        private VisualElement ChapterRow(string label, bool cached)
        {
            var row = ScreenUi.Row();
            row.pickingMode = PickingMode.Ignore;
            ScreenUi.Row(row);
            row.style.marginTop = LvnTokens.Space1;
            var mark = new Label(cached ? "√" : "○");
            mark.pickingMode = PickingMode.Ignore;
            mark.style.color = cached ? LvnTokens.Accent : LvnTokens.TextDim;
            mark.style.fontSize = LvnTokens.TextXs;
            mark.style.width = 26;
            row.Add(mark);
            var l = new Label(label);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = cached ? LvnTokens.Text : LvnTokens.TextDim;
            l.style.fontSize = LvnTokens.TextXs;
            row.Add(l);
            return row;
        }

        private VisualElement QueueRow(DownloadCenter.Entry e, bool failed = false)
        {
            var row = ScreenUi.Row(spread: true);
            row.style.marginTop = LvnTokens.Space1;
            if (e.Active) LvnChrome.Stripe(row);
            var label = new Label(e.Label) { pickingMode = PickingMode.Ignore };
            label.style.color = failed ? LvnTokens.Warn : LvnTokens.Text;
            label.style.fontSize = LvnTokens.TextXs;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            row.Add(label);
            if (failed)
            {
                var retry = new Button(() => Center?.Retry(e))
                {
                    text = LvnWords.Of("dl.retry", "Retry"), name = "download-retry"
                };
                LvnStyler.Plate(retry, LvnTokens.Faint, LvnTokens.Accent, 12f);
                retry.style.minHeight = LvnTokens.Touch;
                retry.style.flexShrink = 0;
                retry.SetEnabled(!(Offline?.Invoke() ?? false));
                row.Add(retry);
            }
            var remove = new Button(() => Center?.Remove(e)) { text = "×" };
            LvnStyler.Plate(remove, Color.clear, LvnTokens.TextDim, 12f);
            remove.style.width = LvnTokens.Touch;
            remove.style.height = LvnTokens.Touch;
            remove.style.flexShrink = 0;
            row.Add(remove);
            return row;
        }
    }
}
