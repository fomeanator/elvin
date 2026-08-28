using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
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
    /// (the "Полночь" palette). Stats/chapters/saves are all read from the real
    /// <see cref="Title"/> + <see cref="StatVars"/> the host seeds before Rebuild —
    /// no section renders placeholder data; an unconfigured one just stays hidden.
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
    public sealed partial class TitleDetailScreen : LvnOverlayScreen, Lvn.UI.ILvnRedress
    {
        private const float HeroAspect = 0.68f; // hero height = page width × this

        private readonly ILvnAssets _assets;
        private readonly ScrollView _scroll;
        private readonly VisualElement _actionBar;
        private VisualElement _hero;
        private Button _backBtn;

        private VisualElement _modal; // the restart overlay, while it's up

        /// <summary>The real title behind this page — set by the host before
        /// <see cref="Rebuild"/> so the Restart menu can list the actual chapters
        /// and read/clear reading progress. Null → the Restart affordance hides.</summary>
        public LvnTitle Title;

        /// <summary>Блок «Сохранения» на карточке (TR-32): партнёр прячет его
        /// данными (ui.browse.detail_saves=false). Хост ставит до открытия.</summary>
        public bool ShowSaves = true;

        /// <summary>Host hook for "restart the whole expedition": wipe this title's
        /// persisted stats and save slots (progress/checkpoints are cleared via
        /// <see cref="LvnProgress.ResetTitle"/>). Null → progress-only reset.</summary>
        public System.Func<LvnTitle, Task> OnResetProgress;

        // Fallback text for a title the host hasn't seeded yet — real values
        // (TitleName/HeroImageUrl/Synopsis) are overwritten by NovelApp from
        // the actual LvnTitle before every Rebuild().
        public string HeroImageUrl = "/content/cards/card0.png";
        public int EnergyCost = 1;
        public string TitleName = "";

        // ── КАРТОЧКА ЧИТАЕТ НОВЕЛЛУ САМА ────────────────────────────────────
        // Хост кормил экран шестью присваиваниями подряд: имя, картинка,
        // синопсис, цена — и ТУТ ЖЕ сам объект новеллы, где всё это уже есть.
        // Дублирование держалось на памяти вызывающего: забыл одну строчку —
        // и карточка показывает имя от прошлой новеллы. Теперь поля —
        // ПЕРЕОПРЕДЕЛЕНИЯ: заполнены хостом (встраивающая игра вправе показать
        // своё) — берём их, пусты — спрашиваем саму новеллу.
        private string ShownName
            => !string.IsNullOrEmpty(TitleName) ? TitleName : Lvn.Content.LvnWords.Name("title", Title?.id, Title?.name);
        private string ShownHero
        {
            get
            {
                var own = Title.CardArt();
                return !string.IsNullOrEmpty(own) ? own : HeroImageUrl;
            }
        }
        private string ShownSynopsis
            => !string.IsNullOrEmpty(Title?.card?.description) ? Title.card.description : Synopsis;
        private int ShownCost
            => Title?.cost?.amount > 0 ? (int)Title.cost.amount : EnergyCost;
        public string Chips = "";
        public string Synopsis = "";

        /// <summary>The player's live vars for THIS title (title-scope + a nested
        /// "global" for cross-title stats), as loaded by the host from the state
        /// store. Null/empty → every stat reads as its zero value. Set before
        /// <see cref="Rebuild"/>.</summary>
        public JObject StatVars;

        public TitleDetailScreen(ILvnAssets assets)
        {
            _assets = assets;

            ScreenUi.Stretch(this);
            style.backgroundColor = LvnTokens.Bg; // full-screen opaque page
            LvnChrome.Backdrop(this);   // тема без атмосферы не делает ничего
            style.opacity = 0f;
            style.display = DisplayStyle.None;

            _scroll = Lvn.UI.LvnScroll.Vertical();
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

        // Жизненный цикл — в LvnOverlayScreen. Здесь остаётся только смысл
        // ДВУХ исходов: «играть» подтверждает ожидание, «назад» отменяет.
        private void Play() => Close();
        private void Back() => Cancel();


        /// <summary>(Re)build the whole content column. Public so tests/hosts can
        /// render the page without driving <see cref="ShowAsync"/>.</summary>
        /// <summary>Слова, шрифт или размеры сменились — перечитать их.</summary>
        public void Redress() { RedressChrome(); Rebuild(); }

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
            var stats = BuildStatsSection();
            if (stats != null) body.Add(stats);
            var chapters = BuildChaptersSection();
            if (chapters != null) body.Add(chapters);
            if (ShowSaves) body.Add(BuildSavesSection());

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
            LvnChrome.Edge(hero, 0.8f);   // кадр — часть интерфейса, а не картинка сверху
            hero.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            hero.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            LvnPicture.Fit(hero);
            hero.style.overflow = Overflow.Hidden;
            // fixed aspect: height follows the resolved page width (NOT a percent —
            // percent heights inside scroll content collapse the layout)
            hero.RegisterCallback<GeometryChangedEvent>(e =>
            {
                float w = e.newRect.width;
                if (w > 1f) hero.style.height = Mathf.Round(w * HeroAspect);
            });
            LvnAsync.Fire(ScreenUi.AssignBgAsync(hero, ShownHero, _assets), "AssignBg");
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

            var overTitle = new Label(ShownName);
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
            // Back — это ОТМЕНА. В прежнем коде метод отмены назывался Close и
            // возвращал false; в базовом классе Close означает подтверждение,
            // поэтому кнопка обязана звать Back, иначе «назад» запускало бы игру.
            var back = new Button(Back) { text = "‹" };
            _backBtn = back;
            back.style.position = Position.Absolute; back.style.left = 20; back.style.top = 16;
            back.style.fontSize = 36; back.style.width = 56; back.style.height = 56;
            back.style.paddingTop = 0; back.style.paddingBottom = 0;
            back.style.unityTextAlign = TextAnchor.MiddleCenter;
            back.style.color = LvnTokens.Text;
            back.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            LvnChrome.ClearBorder(back); LvnChrome.Round(back, 28f);
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
            LvnChrome.Round(chip, 999f); // pill

            var lbl = new Label(text);
            lbl.style.color = LvnTokens.TextDim;
            lbl.style.fontSize = 20;
            chip.Add(lbl);
            return chip;
        }

        // ── 3. synopsis paragraph ────────────────────────────────────────────
        private VisualElement BuildSynopsis()
        {
            var p = new Label(ShownSynopsis);
            p.style.flexShrink = 0;
            p.style.color = LvnTokens.TextDim;
            p.style.fontSize = 24;
            p.style.whiteSpace = WhiteSpace.Normal;
            p.style.marginTop = 10;
            return p;
        }

        // ── 4. player stats — trait pairs (proportional bar, no fixed max) plus
        // per-character relationship meters (0..max bar), driven entirely by
        // Title.stats + StatVars. A title with no stats configured renders no
        // section at all — never placeholder numbers.
        private VisualElement BuildStatsSection()
        {
            if (Title?.stats == null || Title.stats.Count == 0) return null;

            var section = new VisualElement();
            section.style.flexShrink = 0;
            section.style.marginTop = 34;
            section.Add(SectionHeader(LvnWords.Of("stats.title", "Your stats")));

            foreach (var s in Title.stats)
                if (s != null)
                    section.Add(StatRows.Row(s, key => StatVars?.SelectToken(key)));

            return section;
        }

        // ── 5. chapters list — real chapters + real reading progress ─────────
        private VisualElement BuildChaptersSection()
        {
            var chapterList = Title.ChaptersOf();
            if (chapterList.Count == 0) return null;

            var section = new VisualElement();
            section.style.flexShrink = 0;
            section.style.marginTop = 36;
            section.Add(SectionHeader(LvnWords.Of("chapters.title", "Chapters")));

            int reached = LvnProgress.Reached(Title);
            var current = LvnProgress.Current(Title);
            // Завершённая новелла: тогда и глава на границе достигнутого честно
            // «пройдена». Спрашиваем прогресс, а не считаем сами: своё правило
            // не знало, что НЕПОЧАТАЯ новелла не пройдена, и новелла с первой
            // главой под номером 0 показывала все главы галочками на чистом
            // устройстве.
            bool finished = LvnProgress.Finished(Title);
            // Доступность спрашиваем у Швейцара, а не считаем сами. Своё правило
            // («номер не больше достигнутого») забывало про ПЕРВУЮ главу, которая
            // открыта всегда, — и у новеллы, к которой ещё не притрагивались
            // (reached = 0), список рисовал замок на главе, играбельной кнопкой
            // рядом. Соседнее окно «перезапустить с главы» в этом же экране
            // спрашивало Швейцара и замка не рисовало.
            int firstNumber = Lvn.Content.LvnGatekeeper.FirstNumber(Title);
            foreach (var ch in chapterList)
            {
                // state: 1 = точка продолжения; 0 = ПРОЙДЕНА (строго раньше
                // достигнутой — сама достигнутая ещё не сыграна: партнёр прошёл
                // гл.2, перезапустил её — и «пройденной» рисовалась гл.3);
                // 3 = доступна, но не пройдена; 2 = закрыта.
                int state = current != null && ch.id == current.id ? 1
                    : ch.number < reached || (finished && ch.number <= reached) ? 0
                    : Lvn.Content.LvnGatekeeper.ChapterOpen(ch.number, reached, firstNumber) ? 3
                    : 2;
                section.Add(ChapterRow(ch.number, ChapterLabel(ch), state));
            }

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
            LvnChrome.Round(row, LvnTokens.RadiusSm);
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
            LvnChrome.Round(numBadge, 24f);
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

            // Состояние главы: иконка И слово. Одной иконки мало — «пройдено» и
            // «текущая» слишком близки по смыслу, чтобы различаться только
            // фигуркой; одного слова мало — глаз ищет метку слева от текста.
            var stateColor = state == 0 ? LvnTokens.Gold
                : state == 1 ? LvnTokens.Accent
                : state == 3 ? LvnTokens.Text
                : LvnTokens.TextDim;
            var stateBox = new VisualElement();
            stateBox.style.flexDirection = FlexDirection.Row;
            stateBox.style.alignItems = Align.Center;
            stateBox.style.flexShrink = 0;
            stateBox.style.marginLeft = 12;
            var stateIcon = LvnIcons.Make(
                state == 0 ? LvnIcon.Check : state == 1 || state == 3 ? LvnIcon.Play : LvnIcon.Lock,
                17f, stateColor, 0f, LvnTheme.Current.IconGlow);
            stateIcon.style.marginRight = 5;
            stateBox.Add(stateIcon);
            var stateLbl = new Label(state == 0 ? LvnWords.Of("chapter.done", "finished") : state == 1 ? LvnWords.Of("chapter.current", "current")
                : state == 3 ? LvnWords.Of("chapter.available", "available") : LvnWords.Of("chapter.locked", "locked"));
            stateLbl.style.fontSize = 20;
            stateLbl.style.color = stateColor;
            stateBox.Add(stateLbl);
            row.Add(stateBox);

            // Demo placeholder rows: NOT clickable — a tap here used to launch
            // (and charge) the CURRENT chapter regardless of the row's label.
            // Real chapter navigation arrives with the manifest-driven list.
            row.style.opacity = locked ? 0.6f : 1f;

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
                var restart = new Button(ShowRestartMenu) { text = LvnWords.Of("title.restart", "Start over") };
                restart.style.marginBottom = 12;
                restart.style.fontSize = 24;
                restart.style.paddingTop = 12;
                restart.style.paddingBottom = 12;
                restart.style.color = LvnTokens.Text;
                restart.style.backgroundColor = LvnTokens.Faint;
                LvnChrome.ClearBorder(restart);
                LvnChrome.Round(restart, LvnTokens.RadiusSm);
                bar.Add(restart);
            }

            var actionRow = new VisualElement();
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.alignItems = Align.Center;
            bar.Add(actionRow);

            var play = new Button(Play) { text = LvnWords.Of("hub.play", "Play") };
            play.style.flexGrow = 1;
            play.style.flexShrink = 1;
            play.style.fontSize = 30;
            play.style.paddingTop = 18;
            play.style.paddingBottom = 18;
            play.style.marginRight = 14;
            play.style.unityFontStyleAndWeight = FontStyle.Bold;
            play.style.color = LvnTokens.OnAccent;
            play.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.ClearBorder(play);
            LvnChrome.Round(play, LvnTokens.RadiusSm);
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
            LvnChrome.Round(cost, LvnTokens.RadiusSm);

            cost.style.flexDirection = FlexDirection.Row;
            cost.style.alignItems = Align.Center;
            var costIcon = LvnIcons.Make(LvnIcon.Energy, 22f, LvnTokens.Gold, 0f, LvnTheme.Current.IconGlow);
            costIcon.style.marginRight = 6;
            cost.Add(costIcon);
            var costLbl = new Label(ShownCost.ToString());
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









        // ── shared bits ──────────────────────────────────────────────────────
        private static Label SectionHeader(string text)
        {
            var lbl = new Label(text);
            lbl.style.flexShrink = 0;
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = 30;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            return LvnChrome.Heading(lbl);
        }

        // Затемнение под подписью героя: тот же дом, что рисует фон витрины.
        // Здесь стояла своя текстура со своим кэшем и своей кривой — два
        // рисователя одного градиента, и правка попадала в один из них.
        private static StyleBackground BottomScrim()
            => Lvn.UI.LvnBackdrop.Vertical(
                top: new Color(0.05f, 0.02f, 0.05f, 0f),
                bottom: new Color(0.05f, 0.02f, 0.05f, 0.88f),
                smooth: true);
    }
}
