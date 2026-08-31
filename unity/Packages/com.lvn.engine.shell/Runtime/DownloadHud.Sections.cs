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

        private void RebuildSections(bool animate = false)
        {
            if (_sections == null) return;
            _sections.Clear();

            bool off = Offline?.Invoke() ?? false;
            int pend = PendingOps?.Invoke() ?? 0;

            if (off)
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.offline_title", "Play offline")));
                card.Add(Hint(() => LvnWords.Of("dl.offline_hint", "Everything already downloaded is available: the ticked chapters below open on a plane with no network. Download the whole game and read anywhere; purchases work offline too and sync later.")));
                var chapters = ChaptersInfo?.Invoke();
                if (chapters != null)
                    foreach (var (label, cached) in chapters)
                        card.Add(ChapterRow(label, cached));
                _sections.Add(card);
            }

            if (pend > 0)
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.pending_title", "Waiting to send")));
                card.Add(Hint(() => off
                    ? LvnWords.Of("dl.pending_offline", "{n} events — purchases and progress are saved on the device and leave for the server as soon as there is a network.", pend)
                    : LvnWords.Of("dl.pending_sending", "Sending to the server: {n} events (purchases, progress).", pend)));
                _sections.Add(card);
            }

            if (Center != null && Center.Queue.Count > 0)
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.queue_title", "Download queue")));
                foreach (var e in Center.Queue)
                    card.Add(QueueRow(e));
                _sections.Add(card);
            }

            var missingPlaceholder = 0; // (маркер позиции — каскад ниже)
            var missing = MissingInfo?.Invoke() ?? (0, 0);
            if (missing.Item2 > 0 && DownloadAll != null && !(Center != null && Center.Queue.Count > 0))
            {
                var card = SectionCard();
                card.Add(CardHeading(() => LvnWords.Of("dl.all_title", "The whole game with you")));
                card.Add(Hint(() => LvnWords.Of("dl.all_hint", "Download once and play with no network: chapters, art and music stay on the device.")));
                var offer = CurrentChapterOffer?.Invoke();
                if (offer != null)
                {
                    var chBtn = new Button { text = offer.Value.label };
                    chBtn.style.height = 48;
                    chBtn.style.fontSize = LvnTokens.TextXs;
                    chBtn.style.marginTop = 8;
                    LvnStyler.Plate(chBtn, LvnTokens.Faint, LvnTokens.Accent, 14f);
                    var startCh = offer.Value.start;
                    chBtn.clicked += () => { chBtn.SetEnabled(false); startCh(); };
                    card.Add(chBtn);
                }
                bool partial = HasSomeDownloaded?.Invoke() ?? false;
                var btn = new Button { text =
                    (partial ? LvnWords.Of("dl.resume", "Finish downloading") : LvnWords.Of("dl.get_all", "Download all"))
                    + " " + Lvn.Content.LvnBytes.Approx(missing.Item1) };
                btn.style.height = 52;
                btn.style.fontSize = LvnTokens.TextSm;
                btn.style.marginTop = 8;
                LvnStyler.Primary(btn, 14f);
                btn.clicked += () => { btn.SetEnabled(false); Lvn.LvnAsync.Fire(DownloadAll(), "DownloadAll"); };
                card.Add(btn);
                _sections.Add(card);
            }
            if (animate) CascadeIn();
        }

        private VisualElement SectionCard()
        {
            var card = new VisualElement();
            card.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Edge(card); // тонкий бордер токеном — карточка, не пятно
            LvnChrome.Round(card, LvnTokens.Radius);
            card.style.paddingTop = 12; card.style.paddingBottom = 12;
            card.style.paddingLeft = 14; card.style.paddingRight = 14;
            card.style.marginBottom = 10;
            return card;
        }

        // Каскад: карточки прибывают одна за другой (fade + подъём) — попап
        // «наполняется», а не вспыхивает готовым.
        private void CascadeIn()
        {
            int i = 0;
            foreach (var child in _sections.Children())
            {
                var el = child;
                el.style.opacity = 0f;
                el.style.translate = new Translate(0f, 10f);
                int delay = 60 + i * 55;
                el.schedule.Execute(() =>
                    el.experimental.animation.Start(0f, 1f, LvnMotion.Ms(220), (e2, t) =>
                    {
                        float e = 1f - Mathf.Pow(1f - t, 3f);
                        e2.style.opacity = e;
                        e2.style.translate = new Translate(0f, Mathf.Lerp(10f, 0f, e));
                    })).ExecuteLater(delay);
                i++;
            }
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
            cell.style.marginBottom = 8;
            var c = Lvn.UI.LvnRedress.Bind(new Label(), caption);
            c.pickingMode = PickingMode.Ignore;
            c.style.color = LvnTokens.TextDim;
            c.style.fontSize = LvnTokens.TextMicro;
            cell.Add(c);
            var v = new Label("—");
            v.pickingMode = PickingMode.Ignore;
            v.style.color = LvnTokens.Text;
            v.style.fontSize = LvnTokens.TextXs;
            v.style.unityFontStyleAndWeight = FontStyle.Bold;
            v.style.marginTop = 1;
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
            l.style.marginBottom = 4;
            return l;
        }

        /// <summary>Пояснение под заголовком карточки — тоже от источника, по
        /// той же причине.</summary>
        private Label Hint(System.Func<string> text)
        {
            var l = Lvn.UI.LvnRedress.Bind(new Label(), text);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.TextDim;
            l.style.fontSize = LvnTokens.TextXs;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        private VisualElement ChapterRow(string label, bool cached)
        {
            var row = ScreenUi.Row();
            row.pickingMode = PickingMode.Ignore;
            ScreenUi.Row(row);
            row.style.marginTop = 6;
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

        private VisualElement QueueRow(DownloadCenter.Entry e)
        {
            var row = ScreenUi.Row(spread: true);
            row.style.marginTop = 6;
            if (e.Active)
            {
                // Активная глава помечена акцентной кромкой слева — бордер
                // работает как маркер состояния, в языке Полуночи.
                row.style.borderLeftWidth = 3f;
                row.style.borderLeftColor = LvnTokens.Accent;
                row.style.paddingLeft = 8;
            }
            var l = new Label((e.Active ? "▶ " : "") + e.Label
                + (e.Bytes > 0 ? " · " + Lvn.Content.LvnBytes.Short(e.Bytes) : ""));
            l.pickingMode = PickingMode.Ignore;
            l.style.color = e.Active ? LvnTokens.Text : LvnTokens.TextDim;
            l.style.fontSize = LvnTokens.TextXs;
            l.style.overflow = Overflow.Hidden;
            l.style.textOverflow = TextOverflow.Ellipsis;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            l.style.flexShrink = 1;
            row.Add(l);
            var x = new Label("×");
            x.style.color = LvnTokens.TextDim;
            x.style.fontSize = LvnTokens.TextXs;
            x.style.paddingLeft = 10; x.style.paddingRight = 4;
            x.style.flexShrink = 0;
            var entry = e;
            x.RegisterCallback<ClickEvent>(ev =>
            {
                ev.StopPropagation();
                // Строка уезжает и схлопывается — и только потом выбывает из
                // очереди: снятие видно, а не «мигнуло и нет».
                float h0 = row.resolvedStyle.height;
                row.experimental.animation.Start(0f, 1f, LvnMotion.Ms(LvnMotion.Normal), (r, t) =>
                {
                    r.style.opacity = 1f - t;
                    r.style.translate = new Translate(Mathf.Lerp(0f, 40f, t * t), 0f);
                    if (h0 > 1f) r.style.height = Mathf.Lerp(h0, 0f, t);
                    if (t >= 1f) { Center?.Remove(entry); RebuildSections(); }
                });
            });
            row.Add(x);
            return row;
        }
    }
}
