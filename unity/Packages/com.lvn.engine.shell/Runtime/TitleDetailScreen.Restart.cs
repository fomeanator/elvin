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
            var section = new VisualElement();
            section.style.flexShrink = 0;
            section.style.marginTop = 36;
            section.Add(SectionHeader(LvnWords.Of("saves.title", "Saves")));

            bool hasProgress = Title != null
                && (LvnProgress.Current(Title) != null || LvnProgress.Reached(Title) > 0);
            if (hasProgress)
            {
                var cont = new Button(Play) { text = LvnWords.Of("hub.continue", "Continue") };
                cont.style.flexShrink = 0;
                cont.style.marginTop = 14;
                cont.style.fontSize = 28;
                cont.style.paddingTop = 18;
                cont.style.paddingBottom = 18;
                cont.style.unityFontStyleAndWeight = FontStyle.Bold;
                cont.style.color = LvnTokens.OnAccent;
                cont.style.backgroundColor = LvnTokens.Accent;
                LvnChrome.ClearBorder(cont);
                LvnChrome.Round(cont, LvnTokens.RadiusSm);
                section.Add(cont);
            }

            var auto = Title != null ? LvnSaveStore.Get(Title.id, LvnSaveStore.AutoSlot) : null;
            if (auto?.Snap != null)
                section.Add(SaveRow(LvnWords.Of("saves.auto", "Autosave"), DescribeSave(Title, auto), Play));
            else if (!hasProgress)
            {
                var empty = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("saves.empty", "No saves yet — start reading."));
                empty.style.color = LvnTokens.TextDim;
                empty.style.fontSize = 20;
                empty.style.marginTop = 10;
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
            var row = new VisualElement();
            row.style.flexShrink = 0;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.backgroundColor = LvnTokens.Surface;
            LvnChrome.Round(row, LvnTokens.RadiusSm);
            row.style.marginTop = 12;
            row.style.paddingLeft = 18;
            row.style.paddingRight = 14;
            row.style.paddingTop = 14;
            row.style.paddingBottom = 14;

            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;

            var slotLbl = new Label(slot);
            slotLbl.style.color = LvnTokens.Text;
            slotLbl.style.fontSize = 24;
            col.Add(slotLbl);

            var whereLbl = new Label(where);
            whereLbl.style.color = LvnTokens.TextDim;
            whereLbl.style.fontSize = 20;
            whereLbl.style.marginTop = 4;
            col.Add(whereLbl);
            row.Add(col);

            var load = new Button(onLoad) { text = LvnWords.Of("saves.load", "Load") };
            load.SetEnabled(onLoad != null);
            load.style.flexShrink = 0;
            load.style.marginLeft = 12;
            load.style.fontSize = 22;
            load.style.paddingTop = 10;
            load.style.paddingBottom = 10;
            load.style.paddingLeft = 18;
            load.style.paddingRight = 18;
            load.style.color = LvnTokens.Text;
            load.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(load);
            LvnChrome.Round(load, LvnTokens.RadiusSm);
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
            msg.style.fontSize = 22;
            msg.style.whiteSpace = WhiteSpace.Normal;
            msg.style.marginBottom = 8;
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
            int reached = LvnProgress.Reached(Title);
            int firstNumber = Lvn.Content.LvnGatekeeper.FirstNumber(Title);
            var panel = OpenModal(LvnWords.Of("restart.pick_chapter", "Choose a chapter"));

            var scroll = Lvn.UI.LvnScroll.Vertical();
            scroll.style.flexGrow = 1;
            panel.Add(scroll);

            foreach (var c in chapters)
            {
                var ch = c;
                // Перезапуск не вправе прыгнуть дальше пройденного — правило
                // спрашиваем у Привратника, а не повторяем здесь.
                bool unlocked = Lvn.Content.LvnGatekeeper.ChapterOpen(ch.number, reached, firstNumber);
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
            scrim.style.position = Position.Absolute;
            scrim.style.left = 0; scrim.style.right = 0; scrim.style.top = 0; scrim.style.bottom = 0;
            scrim.style.backgroundColor = LvnTokens.Scrim;
            scrim.style.justifyContent = Justify.Center;
            scrim.style.alignItems = Align.Center;
            scrim.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                if (e.target == scrim) CloseModal();
            });
            Add(scrim);
            _modal = scrim;

            var panel = new VisualElement();
            panel.style.width = Length.Percent(84f);
            panel.style.maxWidth = 560;
            panel.style.maxHeight = Length.Percent(80f);
            panel.style.backgroundColor = LvnTokens.PanelBg;
            LvnChrome.Round(panel, LvnTokens.RadiusSm + 4f);
            panel.style.paddingTop = 22; panel.style.paddingBottom = 18;
            panel.style.paddingLeft = 20; panel.style.paddingRight = 20;
            panel.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            scrim.Add(panel);

            if (!string.IsNullOrEmpty(heading))
            {
                var h = new Label(heading);
                h.style.color = LvnTokens.Text;
                h.style.fontSize = 30;
                h.style.unityFontStyleAndWeight = FontStyle.Bold;
                h.style.whiteSpace = WhiteSpace.Normal;
                h.style.marginBottom = 12;
                panel.Add(h);
            }
            return panel;
        }

        private void CloseModal()
        {
            if (_modal != null) { _modal.RemoveFromHierarchy(); _modal = null; }
        }

        private static Button ModalButton(string text, bool primary, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = 24;
            b.style.marginTop = 10;
            b.style.paddingTop = 14; b.style.paddingBottom = 14;
            b.style.paddingLeft = 16; b.style.paddingRight = 16;
            b.style.whiteSpace = WhiteSpace.Normal;
            LvnStyler.Choice(b, primary, LvnTokens.RadiusSm);
            return b;
        }

        private static string ChapterLabel(LvnChapter c) => Lvn.Content.LvnCaptions.Chapter(c);
    }
}
