using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СОХРАНЕНИЯ И ПЕРЕЗАПУСК — часть <see cref="TitleDetailScreen"/>: чем
    /// продолжить, что переиграть и с какой главы начать заново.
    ///
    /// <para>Единственное место продукта, где игрок стирает свой прогресс, —
    /// поэтому со своим модальным окном, своим подтверждением и своим выбором
    /// главы.</para>
    /// </summary>
    public sealed partial class TitleDetailScreen
    {
        // ── 6. saves — continue button + the real autosave row ───────────────
        // Only the autosave is shown: it's the one slot the normal Play/Continue
        // flow restores to its exact cursor (PlayOneChapterAsync calls
        // Stage.RestoreSnapshot on it) — a named manual slot from the in-game
        // save menu would need its own precise cross-slot entry point, which
        // doesn't exist yet, so showing one here would just mislabel where
        // "Загрузить" actually lands.
        private VisualElement BuildSavesSection()
        {
            var section = ScreenUi.Section(() => LvnWords.Of("saves.title", "Saves"));

            bool hasProgress = Title != null
                && LvnProgress.Touched(Title);
            if (hasProgress)
            {
                var cont = Lvn.UI.LvnRedress.Bind(new Button(Play), () => LvnWords.Of("hub.continue", "Continue"));
                cont.style.flexShrink = 0;
                cont.style.marginTop = LvnTokens.Space2;
                cont.style.fontSize = LvnTokens.TextBase;
                LvnAir.PadY(cont, LvnTokens.Space3);
                cont.style.unityFontStyleAndWeight = FontStyle.Bold;
                LvnStyler.Primary(cont, LvnTokens.RadiusSm);
                section.Add(cont);
            }

            var auto = Title != null ? LvnSaveStore.Get(Title.id, LvnSaveStore.AutoSlot) : null;
            if (auto?.Snap != null)
                section.Add(SaveRow(LvnWords.Of("saves.auto", "Autosave"), DescribeSave(Title, auto), Play));
            else if (!hasProgress)
            {
                var empty = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("saves.empty", "No saves yet — start reading."));
                empty.style.color = LvnTokens.TextDim;
                empty.style.fontSize = LvnTokens.TextXs;
                empty.style.marginTop = LvnTokens.Space2;
                empty.style.whiteSpace = WhiteSpace.Normal;
                section.Add(empty);
            }

            return section;
        }

        private static string DescribeSave(LvnTitle t, LvnSaveSlot slot)
        {
            var chapter = t.ChaptersOf().Find(c => c.id == slot.ChapterId);
            string label = chapter != null ? ChapterLabel(chapter) : slot.ChapterId ?? "";
            return $"{label} · {RelativeTime(slot.SavedAtUnixMs)}";
        }

        private static string RelativeTime(long unixMs) => Lvn.UI.LvnTimeWords.Ago(unixMs);

        private VisualElement SaveRow(string slot, string where, System.Action onLoad)
        {
            var row = LvnStyler.ListRow(new VisualElement());
            row.style.marginTop = LvnTokens.Space2;
            row.style.paddingLeft = LvnTokens.Space3;
            row.style.paddingRight = LvnTokens.Space2;

            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;

            var slotLbl = new Label(slot);
            slotLbl.style.color = LvnTokens.Text;
            slotLbl.style.fontSize = LvnTokens.TextSm;
            col.Add(slotLbl);

            var whereLbl = new Label(where);
            whereLbl.style.color = LvnTokens.TextDim;
            whereLbl.style.fontSize = LvnTokens.TextXs;
            whereLbl.style.marginTop = 4;
            col.Add(whereLbl);
            row.Add(col);

            var load = Lvn.UI.LvnRedress.Bind(new Button(onLoad), () => LvnWords.Of("saves.load", "Load"));
            load.SetEnabled(onLoad != null);
            load.style.flexShrink = 0;
            load.style.marginLeft = LvnTokens.Space2;
            load.style.fontSize = LvnTokens.TextSm;
            LvnAir.PadX(load, LvnTokens.Space3);
            LvnAir.PadY(load, LvnTokens.Space2);
            LvnStyler.Quiet(load, LvnTokens.RadiusSm);
            row.Add(load);

            return row;
        }

        private void ShowRestartMenu()
        {
            if (Title == null) return;
            var chapters = Title.ChaptersOf();
            var panel = OpenModal(LvnWords.Of("restart.title", "Restart"));

            var msg = new Label(
                LvnWords.Of("restart.explain",
                "\"Everything\" starts over from chapter one and clears every stat. "
                + "\"From a chapter\" picks where to resume."));
            msg.style.color = LvnTokens.TextDim;
            msg.style.fontSize = LvnTokens.TextSm;
            msg.style.whiteSpace = WhiteSpace.Normal;
            msg.style.marginBottom = LvnTokens.Space1;
            panel.Add(msg);

            panel.Add(ModalButton(LvnWords.Of("restart.whole", "Restart everything"), primary: true,
                () => LvnAsync.Fire(RestartWholeAsync(), "RestartWhole")));
            if (chapters.Count > 1)
                panel.Add(ModalButton(LvnWords.Of("restart.from_chapter", "Restart from a chapter…"), primary: false,
                    () => ShowChapterPicker(chapters)));
            panel.Add(ModalButton(LvnWords.Of("common.cancel", "Cancel"), primary: false, CloseModal));
        }

        private void ShowChapterPicker(List<LvnChapter> chapters)
        {
            if (Title == null) return;
            var marks = LvnChapterMarks.ForAll(Title, chapters);
            var panel = OpenModal(LvnWords.Of("restart.pick_chapter", "Choose a chapter"));

            var scroll = Lvn.UI.LvnScroll.Vertical();
            scroll.style.flexGrow = 1;
            panel.Add(scroll);

            for (int i = 0; i < chapters.Count; i++)
            {
                var ch = chapters[i];
                // Перезапуск не вправе прыгнуть дальше пройденного — правило
                // спрашиваем у дома состояний, а не собираем здесь свою половину.
                bool unlocked = LvnChapterMarks.Playable(marks[i]);
                var row = ModalButton(ChapterLabel(ch) + (unlocked ? "" : "   ·  " + LvnWords.Of("chapter.locked", "locked")), primary: false,
                    () => { if (unlocked) LvnAsync.Fire(RestartFromChapterAsync(ch), "RestartFromChapter"); });
                row.SetEnabled(unlocked);
                row.style.unityTextAlign = TextAnchor.MiddleLeft;
                scroll.Add(row);
            }

            panel.Add(ModalButton(LvnWords.Of("common.cancel", "Cancel"), primary: false, CloseModal));
        }

        private async Task RestartWholeAsync()
        {
            var t = Title;
            CloseModal();
            if (t == null) return;
            if (OnResetProgress != null) await OnResetProgress(t); // wipe stats + saves + progress
            else LvnProgress.ResetTitle(t.id);
            Play(); // resolve → host charges entry and plays from chapter one, clean
        }

        private Task RestartFromChapterAsync(LvnChapter ch)
        {
            var t = Title;
            CloseModal();
            if (t == null || ch == null) return Task.CompletedTask;
            // Move the continue point and flag an explicit restart: the play loop
            // seeds this chapter from its entry checkpoint (stats as of first entry).
            // Та же пара, что у карусели, — теперь один обряд у прогресса.
            LvnProgress.RestartChapter(t, ch);
            Play();
            return Task.CompletedTask;
        }

        // A centered modal card over a tap-to-dismiss scrim; returns the card to
        // fill. Only one modal is up at a time.
        private VisualElement OpenModal(string heading)
        {
            CloseModal();
            var scrim = new VisualElement();
            LvnChrome.Stretch(scrim);
            scrim.style.backgroundColor = LvnTokens.Scrim;
            scrim.style.justifyContent = Justify.Center;
            scrim.style.alignItems = Align.Center;
            scrim.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                if (e.target == scrim) CloseModal();
            });
            // Наложение держит база: она одна знает все выходы из экрана.
            PutOverlay(scrim);

            var panel = new VisualElement();
            panel.style.width = Length.Percent(84f);
            panel.style.maxWidth = 560;
            panel.style.maxHeight = Length.Percent(80f);
            panel.style.backgroundColor = LvnTokens.PanelBg;
            LvnChrome.Round(panel, LvnTokens.Radius);
            LvnAir.PadX(panel, LvnTokens.Space3);
            panel.style.paddingBottom = LvnTokens.Space3;
            panel.style.paddingTop = 22;
            panel.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            scrim.Add(panel);

            if (!string.IsNullOrEmpty(heading))
                panel.Add(ScreenUi.SectionHeader(heading));
            return panel;
        }

        private void CloseModal() => DropOverlay();

        private static Button ModalButton(string text, bool primary, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = LvnTokens.TextSm;
            LvnAir.PadX(b, LvnTokens.Space3);
            LvnAir.PadY(b, LvnTokens.Space2);
            b.style.marginTop = LvnTokens.Space2;
            b.style.whiteSpace = WhiteSpace.Normal;
            LvnStyler.Choice(b, primary, LvnTokens.RadiusSm);
            return b;
        }

        private static string ChapterLabel(LvnChapter c) => Lvn.Content.LvnCaptions.Chapter(c);
    }
}
