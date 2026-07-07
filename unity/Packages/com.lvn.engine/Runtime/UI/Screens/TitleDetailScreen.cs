using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The novel DETAIL page — the screen a player lands on after tapping a title
    /// card (like the Chapters/Episode/RomanceClub detail sheets): a full-bleed hero
    /// image, the title + genre chips, a synopsis, the player's accumulated stats,
    /// the chapter list with per-chapter state, the save slots, and a sticky
    /// "play/continue" action bar with its energy cost.
    ///
    /// Structurally it mirrors <see cref="StoreScreen"/> — a TCS-gated
    /// <see cref="ShowAsync"/> that fades in, parks on a
    /// <see cref="TaskCompletionSource{TResult}"/> resolved by Close/back, then fades
    /// out. Content is built by <see cref="Rebuild"/> so tests and hosts can render
    /// it without driving the fade. Every colour comes from <see cref="LvnTokens"/>
    /// (the "Полночь" palette); the fields below are HARDCODED demo fallbacks so the
    /// screen reads as complete before the real data plumbing lands.
    ///
    /// LAYOUT RULES (learned the hard way):
    ///  - every child of the ScrollView content gets flex-shrink 0, or Yoga
    ///    compresses the whole column to the viewport and rows collapse into
    ///    each other;
    ///  - the hero's height derives from the resolved page width (a fixed aspect),
    ///    never Length.Percent — percent heights inside scroll content are circular;
    ///  - the action bar lives OUTSIDE the scroll so it is actually sticky;
    ///  - the back button and the action bar respect Screen.safeArea (notch /
    ///    home indicator).
    /// </summary>
    public sealed class TitleDetailScreen : VisualElement
    {
        private const float HeroAspect = 0.68f; // hero height = page width × this

        private readonly ILvnAssets _assets;
        private readonly ScrollView _scroll;
        private readonly VisualElement _actionBar;
        private VisualElement _hero;
        private Button _backBtn;

        private TaskCompletionSource<bool> _tcs;
        private bool _open;
        private VisualElement _modal; // the restart overlay, while it's up

        /// <summary>The real title behind this page — set by the host before
        /// <see cref="Rebuild"/> so the Restart menu can list the actual chapters
        /// and read/clear reading progress. Null → the Restart affordance hides.</summary>
        public LvnTitle Title;

        /// <summary>Host hook for "restart the whole expedition": wipe this title's
        /// persisted stats and save slots (progress/checkpoints are cleared via
        /// <see cref="LvnProgress.ResetTitle"/>). Null → progress-only reset.</summary>
        public System.Func<LvnTitle, Task> OnResetProgress;

        // ── Hardcoded demo data (real wiring comes later) ────────────────────
        public string HeroImageUrl = "/content/cards/card0.png";
        public int EnergyCost = 1;
        public string TitleName = "Полночь в Вентспилсе";
        public string Chips = "Романтика · 18+ · 3 главы";
        public string Synopsis =
            "Ты приезжаешь в приморский город на последнее лето перед взрослой жизнью. " +
            "Старый маяк, чужие тайны и человек, которого ты не должна была встретить — " +
            "каждый твой выбор перепишет эту историю. Кому ты доверишься, когда стемнеет?";

        private static readonly (string Name, int Value, int Max)[] DemoStats =
        {
            ("Репутация", 7, 10),
            ("Доверие", 4, 10),
            ("Смелость", 9, 10),
        };

        // state: 0 = пройдено, 1 = текущая, 2 = закрыто
        private static readonly (int No, string Name, int State)[] DemoChapters =
        {
            (1, "Прибытие", 0),
            (2, "Огни маяка", 1),
            (3, "Шёпот прилива", 2),
            (4, "Буря", 2),
            (5, "Рассвет", 2),
        };

        private static readonly (string Slot, string Where)[] DemoSaves =
        {
            ("Автосохранение", "Глава 2 · 45%"),
            ("Слот 1", "Глава 1 · 100%"),
        };

        public TitleDetailScreen(ILvnAssets assets)
        {
            _assets = assets;

            ScreenUi.Stretch(this);
            style.backgroundColor = LvnTokens.Bg; // full-screen opaque page
            style.opacity = 0f;
            style.display = DisplayStyle.None;

            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.style.flexGrow = 1;
            _scroll.style.flexShrink = 1;
            Add(_scroll);

            // Sticky action bar — a sibling of the scroll, so it never scrolls away.
            _actionBar = new VisualElement();
            _actionBar.style.flexShrink = 0;
            Add(_actionBar);

            // Safe-area: keep the back button below the notch and the action bar
            // above the home indicator. Re-resolves whenever geometry changes.
            RegisterCallback<GeometryChangedEvent>(_ => ApplySafeArea());

            Rebuild();
        }

        /// <summary>Open the detail page: fade in, then park on the TCS until the
        /// player taps back/close (or <paramref name="ct"/> cancels), then fade out.
        /// Returns true when the player asked to play/continue, false when they
        /// backed out — mirrors <see cref="StoreScreen.ShowAsync"/>.</summary>
        public async Task<bool> ShowAsync(CancellationToken ct = default)
        {
            if (_open) return false;
            _open = true;
            style.display = DisplayStyle.Flex;
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);
            // Hide() during the fade-in must cancel the open, not leave this await
            // parked on a _tcs nobody will ever resolve.
            if (!_open) return false;

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { return await _tcs.Task; }
            finally
            {
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.25f, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
            }
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(false);
        }

        private void Close() => _tcs?.TrySetResult(false);
        private void Play() => _tcs?.TrySetResult(true);

        /// <summary>(Re)build the whole content column. Public so tests/hosts can
        /// render the page without driving <see cref="ShowAsync"/>.</summary>
        public void Rebuild()
        {
            _scroll.Clear();

            _scroll.Add(BuildHero()); // the back button lives on the hero

            var body = new VisualElement();
            body.style.flexShrink = 0;
            body.style.paddingLeft = 30;
            body.style.paddingRight = 30;
            body.style.paddingTop = 20;
            body.style.paddingBottom = 34;
            _scroll.Add(body);

            body.Add(BuildTitleBlock());
            body.Add(BuildSynopsis());
            body.Add(BuildStatsSection());
            body.Add(BuildChaptersSection());
            body.Add(BuildSavesSection());

            BuildActionBar(_actionBar);
            ApplySafeArea();
        }

        private void ApplySafeArea()
        {
            var insets = ScreenUi.SafeVerticalInsets(this);
            if (_backBtn != null) _backBtn.style.top = 16 + insets.x;
            _actionBar.style.paddingBottom = 18 + insets.y;
        }

        // ── 1. hero image: full-bleed cover, gradient scrim, title + back over it ──
        private VisualElement BuildHero()
        {
            var hero = new VisualElement();
            _hero = hero;
            hero.style.flexShrink = 0;
            hero.style.height = 700; // placeholder until the width resolves below
            hero.style.backgroundColor = LvnTokens.Surface;
            hero.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            hero.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            hero.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            hero.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            hero.style.overflow = Overflow.Hidden;
            // fixed aspect: height follows the resolved page width (NOT a percent —
            // percent heights inside scroll content collapse the layout)
            hero.RegisterCallback<GeometryChangedEvent>(e =>
            {
                float w = e.newRect.width;
                if (w > 1f) hero.style.height = Mathf.Round(w * HeroAspect);
            });
            _ = ScreenUi.AssignBgAsync(hero, HeroImageUrl, _assets);

            // bottom gradient scrim so the overlaid title reads (a real gradient —
            // a flat half-black band leaves an ugly hard edge across the art)
            var scrim = new VisualElement { pickingMode = PickingMode.Ignore };
            scrim.style.position = Position.Absolute;
            scrim.style.left = 0;
            scrim.style.right = 0;
            scrim.style.bottom = 0;
            scrim.style.height = Length.Percent(62f);
            scrim.style.backgroundImage = BottomScrim();
            hero.Add(scrim);

            var overTitle = new Label(TitleName);
            overTitle.style.position = Position.Absolute;
            overTitle.style.left = 30;
            overTitle.style.right = 30;
            overTitle.style.bottom = 22;
            overTitle.style.color = LvnTokens.Text;
            overTitle.style.fontSize = 46;
            overTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            overTitle.style.whiteSpace = WhiteSpace.Normal;
            overTitle.pickingMode = PickingMode.Ignore;
            hero.Add(overTitle);

            // back button, floated on the image (top-left, below the notch)
            var back = new Button(Close) { text = "‹" };
            _backBtn = back;
            back.style.position = Position.Absolute; back.style.left = 20; back.style.top = 16;
            back.style.fontSize = 36; back.style.width = 56; back.style.height = 56;
            back.style.paddingTop = 0; back.style.paddingBottom = 0;
            back.style.unityTextAlign = TextAnchor.MiddleCenter;
            back.style.color = LvnTokens.Text;
            back.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            ClearBorder(back); Round(back, 28f);
            hero.Add(back);

            return hero;
        }

        // ── 2. genre chips row (the title itself sits over the hero) ────────────
        private VisualElement BuildTitleBlock()
        {
            var chips = new VisualElement();
            chips.style.flexShrink = 0;
            chips.style.flexDirection = FlexDirection.Row;
            chips.style.flexWrap = Wrap.Wrap;
            foreach (var part in Chips.Split('·'))
            {
                var t = part.Trim();
                if (t.Length == 0) continue;
                chips.Add(Chip(t));
            }
            return chips;
        }

        private VisualElement Chip(string text)
        {
            var chip = new VisualElement();
            chip.style.marginRight = 10;
            chip.style.marginBottom = 10;
            chip.style.paddingLeft = 14;
            chip.style.paddingRight = 14;
            chip.style.paddingTop = 7;
            chip.style.paddingBottom = 7;
            chip.style.backgroundColor = LvnTokens.SurfaceHi;
            chip.style.borderTopWidth = 1; chip.style.borderBottomWidth = 1;
            chip.style.borderLeftWidth = 1; chip.style.borderRightWidth = 1;
            chip.style.borderTopColor = LvnTokens.Border; chip.style.borderBottomColor = LvnTokens.Border;
            chip.style.borderLeftColor = LvnTokens.Border; chip.style.borderRightColor = LvnTokens.Border;
            Round(chip, 999f); // pill

            var lbl = new Label(text);
            lbl.style.color = LvnTokens.TextDim;
            lbl.style.fontSize = 20;
            chip.Add(lbl);
            return chip;
        }

        // ── 3. synopsis paragraph ────────────────────────────────────────────
        private VisualElement BuildSynopsis()
        {
            var p = new Label(Synopsis);
            p.style.flexShrink = 0;
            p.style.color = LvnTokens.TextDim;
            p.style.fontSize = 24;
            p.style.whiteSpace = WhiteSpace.Normal;
            p.style.marginTop = 10;
            return p;
        }

        // ── 4. accumulated stats — labelled bar meters ───────────────────────
        private VisualElement BuildStatsSection()
        {
            var section = new VisualElement();
            section.style.flexShrink = 0;
            section.style.marginTop = 34;
            section.Add(SectionHeader("Твои статы"));

            foreach (var s in DemoStats)
            {
                var row = new VisualElement();
                row.style.flexShrink = 0;
                row.style.flexDirection = FlexDirection.Column;
                row.style.marginTop = 20;

                var head = new VisualElement();
                head.style.flexShrink = 0;
                head.style.flexDirection = FlexDirection.Row;
                head.style.justifyContent = Justify.SpaceBetween;
                head.style.alignItems = Align.Center;
                head.style.marginBottom = 10;

                var name = new Label(s.Name);
                name.style.color = LvnTokens.Text;
                name.style.fontSize = 24;
                head.Add(name);

                var value = new Label($"{s.Value}/{s.Max}");
                value.style.color = LvnTokens.Accent;
                value.style.fontSize = 22;
                value.style.unityFontStyleAndWeight = FontStyle.Bold;
                head.Add(value);
                row.Add(head);

                // meter: a track with an Accent-filled portion (its own line, below the head)
                var track = new VisualElement();
                track.style.height = 12;
                track.style.flexShrink = 0;
                track.style.backgroundColor = LvnTokens.SurfaceHi;
                Round(track, 6f);
                track.style.overflow = Overflow.Hidden;

                var fill = new VisualElement();
                fill.style.height = Length.Percent(100f);
                float pct = s.Max > 0 ? Mathf.Clamp01((float)s.Value / s.Max) : 0f;
                fill.style.width = Length.Percent(pct * 100f);
                fill.style.backgroundColor = LvnTokens.Accent;
                Round(fill, 6f);
                track.Add(fill);
                row.Add(track);

                section.Add(row);
            }

            return section;
        }

        // ── 5. chapters list ─────────────────────────────────────────────────
        private VisualElement BuildChaptersSection()
        {
            var section = new VisualElement();
            section.style.flexShrink = 0;
            section.style.marginTop = 36;
            section.Add(SectionHeader("Главы"));

            foreach (var ch in DemoChapters)
                section.Add(ChapterRow(ch.No, ch.Name, ch.State));

            return section;
        }

        private VisualElement ChapterRow(int no, string name, int state)
        {
            bool locked = state == 2;

            var row = new VisualElement();
            row.style.flexShrink = 0;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.backgroundColor = LvnTokens.Surface;
            Round(row, LvnTokens.RadiusSm);
            row.style.marginTop = 12;
            row.style.paddingLeft = 16;
            row.style.paddingRight = 16;
            row.style.paddingTop = 14;
            row.style.paddingBottom = 14;

            var numBadge = new Label(no.ToString());
            numBadge.style.width = 48;
            numBadge.style.height = 48;
            numBadge.style.flexShrink = 0;
            numBadge.style.marginRight = 16;
            numBadge.style.unityTextAlign = TextAnchor.MiddleCenter;
            numBadge.style.fontSize = 24;
            numBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            numBadge.style.color = state == 1 ? LvnTokens.OnAccent : LvnTokens.Text;
            numBadge.style.backgroundColor = state == 1 ? LvnTokens.Accent : LvnTokens.SurfaceHi;
            Round(numBadge, 24f);
            row.Add(numBadge);

            var nameLbl = new Label(name);
            nameLbl.style.flexGrow = 1;
            nameLbl.style.flexShrink = 1;
            nameLbl.style.fontSize = 26;
            nameLbl.style.overflow = Overflow.Hidden;
            nameLbl.style.textOverflow = TextOverflow.Ellipsis;
            nameLbl.style.whiteSpace = WhiteSpace.NoWrap;
            nameLbl.style.color = locked ? LvnTokens.TextDim : LvnTokens.Text;
            row.Add(nameLbl);

            string glyph = state == 0 ? "✓ пройдено" : state == 1 ? "▸ текущая" : "🔒 закрыто";
            var stateLbl = new Label(glyph);
            stateLbl.style.flexShrink = 0;
            stateLbl.style.marginLeft = 12;
            stateLbl.style.fontSize = 20;
            stateLbl.style.color = state == 0 ? LvnTokens.Gold
                : state == 1 ? LvnTokens.Accent
                : LvnTokens.TextDim;
            row.Add(stateLbl);

            if (!locked)
            {
                row.RegisterCallback<ClickEvent>(evt => Play());
                row.style.opacity = 1f;
            }
            else
            {
                row.style.opacity = 0.6f;
            }

            return row;
        }

        // ── 6. saves — continue button + save-slot rows ──────────────────────
        private VisualElement BuildSavesSection()
        {
            var section = new VisualElement();
            section.style.flexShrink = 0;
            section.style.marginTop = 36;
            section.Add(SectionHeader("Сохранения"));

            var cont = new Button(Play) { text = "Продолжить" };
            cont.style.flexShrink = 0;
            cont.style.marginTop = 14;
            cont.style.fontSize = 28;
            cont.style.paddingTop = 18;
            cont.style.paddingBottom = 18;
            cont.style.unityFontStyleAndWeight = FontStyle.Bold;
            cont.style.color = LvnTokens.OnAccent;
            cont.style.backgroundColor = LvnTokens.Accent;
            ClearBorder(cont);
            Round(cont, LvnTokens.RadiusSm);
            section.Add(cont);

            foreach (var save in DemoSaves)
                section.Add(SaveRow(save.Slot, save.Where));

            return section;
        }

        private VisualElement SaveRow(string slot, string where)
        {
            var row = new VisualElement();
            row.style.flexShrink = 0;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.backgroundColor = LvnTokens.Surface;
            Round(row, LvnTokens.RadiusSm);
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

            var load = new Button(Play) { text = "Загрузить" };
            load.style.flexShrink = 0;
            load.style.marginLeft = 12;
            load.style.fontSize = 22;
            load.style.paddingTop = 10;
            load.style.paddingBottom = 10;
            load.style.paddingLeft = 18;
            load.style.paddingRight = 18;
            load.style.color = LvnTokens.Text;
            load.style.backgroundColor = LvnTokens.Faint;
            ClearBorder(load);
            Round(load, LvnTokens.RadiusSm);
            row.Add(load);

            return row;
        }

        // ── 7. sticky bottom action bar (sibling of the scroll) ──────────────
        private void BuildActionBar(VisualElement bar)
        {
            bar.Clear();
            bar.style.flexDirection = FlexDirection.Column; // restart row stacks over the play row
            bar.style.paddingLeft = 30;
            bar.style.paddingRight = 30;
            bar.style.paddingTop = 16;
            bar.style.paddingBottom = 18; // + safe inset via ApplySafeArea
            bar.style.borderTopWidth = 1;
            bar.style.borderTopColor = LvnTokens.Border;
            bar.style.backgroundColor = LvnTokens.Bg;

            // "Начать заново" — only once there's progress worth restarting; sits
            // right under the Play action so it reads as a secondary option.
            if (Title != null && (LvnProgress.Current(Title) != null || LvnProgress.Reached(Title) > 0))
            {
                var restart = new Button(ShowRestartMenu) { text = "↻  Начать заново" };
                restart.style.marginBottom = 12;
                restart.style.fontSize = 24;
                restart.style.paddingTop = 12;
                restart.style.paddingBottom = 12;
                restart.style.color = LvnTokens.Text;
                restart.style.backgroundColor = LvnTokens.Faint;
                ClearBorder(restart);
                Round(restart, LvnTokens.RadiusSm);
                bar.Add(restart);
            }

            var actionRow = new VisualElement();
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.alignItems = Align.Center;
            bar.Add(actionRow);

            var play = new Button(Play) { text = "Играть" };
            play.style.flexGrow = 1;
            play.style.flexShrink = 1;
            play.style.fontSize = 30;
            play.style.paddingTop = 18;
            play.style.paddingBottom = 18;
            play.style.marginRight = 14;
            play.style.unityFontStyleAndWeight = FontStyle.Bold;
            play.style.color = LvnTokens.OnAccent;
            play.style.backgroundColor = LvnTokens.Accent;
            ClearBorder(play);
            Round(play, LvnTokens.RadiusSm);
            actionRow.Add(play);

            var cost = new VisualElement();
            cost.style.flexShrink = 0;
            cost.style.flexDirection = FlexDirection.Row;
            cost.style.alignItems = Align.Center;
            cost.style.paddingLeft = 16;
            cost.style.paddingRight = 16;
            cost.style.paddingTop = 14;
            cost.style.paddingBottom = 14;
            cost.style.backgroundColor = LvnTokens.SurfaceHi;
            Round(cost, LvnTokens.RadiusSm);

            var costLbl = new Label("⚡ " + EnergyCost);
            costLbl.style.color = LvnTokens.Gold;
            costLbl.style.fontSize = 26;
            costLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            cost.Add(costLbl);
            actionRow.Add(cost);
        }

        // ── restart flow ─────────────────────────────────────────────────────
        // A modal offering the two genre-standard restarts: wipe the whole
        // expedition (chapter one, empty stats) or roll back to a chosen chapter
        // (its entry-checkpoint stats). Both launch through Play() — the host's
        // normal entry gate then charges and runs the chapter.

        private void ShowRestartMenu()
        {
            if (Title == null) return;
            var chapters = ChaptersOf(Title);
            var panel = OpenModal("Перезапуск экспедиции");

            var msg = new Label(
                "«Всю экспедицию» — с первой главы, все статы сбросятся. " +
                "«С главы» — выбрать главу и начать с неё.");
            msg.style.color = LvnTokens.TextDim;
            msg.style.fontSize = 22;
            msg.style.whiteSpace = WhiteSpace.Normal;
            msg.style.marginBottom = 8;
            panel.Add(msg);

            panel.Add(ModalButton("Перезапустить всю экспедицию", primary: true,
                () => _ = RestartWholeAsync()));
            if (chapters.Count > 1)
                panel.Add(ModalButton("Перезапустить с главы…", primary: false,
                    () => ShowChapterPicker(chapters)));
            panel.Add(ModalButton("Отмена", primary: false, CloseModal));
        }

        private void ShowChapterPicker(List<LvnChapter> chapters)
        {
            if (Title == null) return;
            int reached = LvnProgress.Reached(Title);
            int firstNumber = chapters.Count > 0 ? chapters[0].number : 0;
            var panel = OpenModal("Выберите главу");

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            panel.Add(scroll);

            foreach (var c in chapters)
            {
                var ch = c;
                // The first chapter is always open; later ones unlock as reached —
                // a restart must not jump ahead of where the player has actually been.
                bool unlocked = ch.number <= reached || ch.number == firstNumber;
                var row = ModalButton(ChapterLabel(ch) + (unlocked ? "" : "   🔒"), primary: false,
                    () => { if (unlocked) _ = RestartFromChapterAsync(ch); });
                row.SetEnabled(unlocked);
                row.style.unityTextAlign = TextAnchor.MiddleLeft;
                scroll.Add(row);
            }

            panel.Add(ModalButton("Отмена", primary: false, CloseModal));
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
            LvnProgress.SetCurrent(t, ch);
            LvnProgress.RequestRestart(t.id, ch.id);
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
            Round(panel, LvnTokens.RadiusSm + 4f);
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
            b.style.color = primary ? LvnTokens.OnAccent : LvnTokens.Text;
            b.style.backgroundColor = primary ? LvnTokens.Accent : LvnTokens.Faint;
            ClearBorder(b);
            Round(b, LvnTokens.RadiusSm);
            return b;
        }

        private static string ChapterLabel(LvnChapter c) =>
            !string.IsNullOrEmpty(c?.name) ? c.name
            : (c != null && c.number > 0 ? "Глава " + c.number : c?.id ?? "");

        private static List<LvnChapter> ChaptersOf(LvnTitle t)
        {
            var list = new List<LvnChapter>();
            if (t?.seasons == null) return list;
            foreach (var s in t.seasons)
                if (s?.chapters != null)
                    foreach (var c in s.chapters)
                        if (c != null) list.Add(c);
            list.Sort((a, b) => a.number.CompareTo(b.number));
            return list;
        }

        // ── shared bits ──────────────────────────────────────────────────────
        private static Label SectionHeader(string text)
        {
            var lbl = new Label(text);
            lbl.style.flexShrink = 0;
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = 30;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            return lbl;
        }

        // A cached bottom-anchored black gradient (transparent → dark) so hero
        // text reads without a hard band edge across the art.
        private static Texture2D _scrimTex;
        private static StyleBackground BottomScrim()
        {
            if (_scrimTex == null)
            {
                const int h = 128;
                _scrimTex = new Texture2D(1, h, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                for (int y = 0; y < h; y++)
                {
                    // y=0 is the bottom row: darkest there, fading out towards the top
                    float t = (float)y / (h - 1);
                    float a = Mathf.Lerp(0.88f, 0f, Mathf.SmoothStep(0f, 1f, t));
                    _scrimTex.SetPixel(0, y, new Color(0.05f, 0.02f, 0.05f, a));
                }
                _scrimTex.Apply();
            }
            return new StyleBackground(Background.FromTexture2D(_scrimTex));
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderTopWidth = 0;
            el.style.borderRightWidth = 0;
            el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0;
        }
    }
}
