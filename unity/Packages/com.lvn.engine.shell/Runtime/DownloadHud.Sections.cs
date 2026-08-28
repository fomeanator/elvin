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
                card.Add(SectionTitle(LvnWords.Of("dl.offline_title", "Play offline")));
                card.Add(Hint(LvnWords.Of("dl.offline_hint", "Everything already downloaded is available: the ticked chapters below open on a plane with no network. Download the whole game and read anywhere; purchases work offline too and sync later.")));
                var chapters = ChaptersInfo?.Invoke();
                if (chapters != null)
                    foreach (var (label, cached) in chapters)
                        card.Add(ChapterRow(label, cached));
                _sections.Add(card);
            }

            if (pend > 0)
            {
                var card = SectionCard();
                card.Add(SectionTitle(LvnWords.Of("dl.pending_title", "Waiting to send")));
                card.Add(Hint(off
                    ? LvnWords.Of("dl.pending_offline", "{n} events — purchases and progress are saved on the device and leave for the server as soon as there is a network.").Replace("{n}", pend.ToString())
                    : LvnWords.Of("dl.pending_sending", "Sending to the server: {n} events (purchases, progress).").Replace("{n}", pend.ToString())));
                _sections.Add(card);
            }

            if (Center != null && Center.Queue.Count > 0)
            {
                var card = SectionCard();
                card.Add(SectionTitle(LvnWords.Of("dl.queue_title", "Download queue")));
                foreach (var e in Center.Queue)
                    card.Add(QueueRow(e));
                _sections.Add(card);
            }

            var missingPlaceholder = 0; // (маркер позиции — каскад ниже)
            var missing = MissingInfo?.Invoke() ?? (0, 0);
            if (missing.Item2 > 0 && DownloadAll != null && !(Center != null && Center.Queue.Count > 0))
            {
                var card = SectionCard();
                card.Add(SectionTitle(LvnWords.Of("dl.all_title", "The whole game with you")));
                card.Add(Hint(LvnWords.Of("dl.all_hint", "Download once and play with no network: chapters, art and music stay on the device.")));
                var offer = CurrentChapterOffer?.Invoke();
                if (offer != null)
                {
                    var chBtn = new Button { text = offer.Value.label };
                    chBtn.style.height = 48;
                    chBtn.style.fontSize = 21;
                    chBtn.style.marginTop = 8;
                    chBtn.style.color = LvnTokens.Accent;
                    chBtn.style.backgroundColor = LvnTokens.Faint;
                    LvnChrome.ClearBorder(chBtn);
                    LvnChrome.Round(chBtn, 14f);
                    var startCh = offer.Value.start;
                    chBtn.clicked += () => { chBtn.SetEnabled(false); startCh(); };
                    card.Add(chBtn);
                }
                bool partial = HasSomeDownloaded?.Invoke() ?? false;
                var btn = new Button { text =
                    (partial ? LvnWords.Of("dl.resume", "Finish downloading") : LvnWords.Of("dl.get_all", "Download all"))
                    + " " + Lvn.Content.LvnBytes.Approx(missing.Item1) };
                btn.style.height = 52;
                btn.style.fontSize = 22;
                btn.style.marginTop = 8;
                btn.style.color = LvnTokens.OnAccent;
                btn.style.backgroundColor = LvnTokens.Accent;
                LvnChrome.ClearBorder(btn);
                LvnChrome.Round(btn, 14f);
                btn.clicked += () => { btn.SetEnabled(false); _ = DownloadAll(); };
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
            LvnChrome.Round(card, 14f);
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
            c.style.fontSize = 17;
            cell.Add(c);
            var v = new Label("—");
            v.pickingMode = PickingMode.Ignore;
            v.style.color = LvnTokens.Text;
            v.style.fontSize = 21;
            v.style.unityFontStyleAndWeight = FontStyle.Bold;
            v.style.marginTop = 1;
            cell.Add(v);
            host.Add(cell);
            return v;
        }

        private Label SectionTitle(string text)
        {
            var l = new Label(text);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.Text;
            l.style.fontSize = 22;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginBottom = 4;
            return l;
        }

        private Label Hint(string text)
        {
            var l = new Label(text);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = LvnTokens.TextDim;
            l.style.fontSize = 19;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        private VisualElement ChapterRow(string label, bool cached)
        {
            var row = new VisualElement();
            row.pickingMode = PickingMode.Ignore;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 6;
            var mark = new Label(cached ? "√" : "○");
            mark.pickingMode = PickingMode.Ignore;
            mark.style.color = cached ? LvnTokens.Accent : LvnTokens.TextDim;
            mark.style.fontSize = 20;
            mark.style.width = 26;
            row.Add(mark);
            var l = new Label(label);
            l.pickingMode = PickingMode.Ignore;
            l.style.color = cached ? LvnTokens.Text : LvnTokens.TextDim;
            l.style.fontSize = 20;
            row.Add(l);
            return row;
        }

        private VisualElement QueueRow(DownloadCenter.Entry e)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
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
            l.style.fontSize = 20;
            l.style.overflow = Overflow.Hidden;
            l.style.textOverflow = TextOverflow.Ellipsis;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            l.style.flexShrink = 1;
            row.Add(l);
            var x = new Label("×");
            x.style.color = LvnTokens.TextDim;
            x.style.fontSize = 20;
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
