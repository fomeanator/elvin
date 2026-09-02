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
    public sealed partial class TitleDetailScreen : LvnOverlayScreen
    {
        private const float HeroAspect = 0.68f; // hero height = page width × this

        private readonly ILvnAssets _assets;
        private readonly ScrollView _scroll;
        private readonly VisualElement _actionBar;
        private VisualElement _hero;
        private Button _backBtn;

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
        // Пусто — значит обложки нет: экран покажет подложку. Здесь стоял
        // «/content/cards/card0.png» — файл из демо-набора, которого в живом
        // контенте не существует, и новелла без своей обложки уходила за ним в
        // сеть (permanent 404 на каждом открытии карточки).
        public string HeroImageUrl;
        /// <summary>Экономика новеллы (гейт входа в главу) — нужна ценнику на
        /// кнопке: без неё он показывал выдуманную единицу.</summary>
        public Lvn.Content.LvnEconomyConfig Economy;
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
        // ЦЕНУ СЧИТАЕТ ДОМ, тот же, что списывает: здесь стояло «цена новеллы,
        // иначе поле EnergyCost», а поле это никто никогда не задавал — новелла
        // без своей цены рисовала игроку «1» независимо от того, сколько
        // спишется, а бесплатная глава показывала цену и не списывала ничего.
        private Lvn.Content.LvnEntryPrice.Price ShownPrice
            => Lvn.Content.LvnEntryPrice.Shown(Title, Economy, LvnProgress.Current(Title)?.id);
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

            style.backgroundColor = LvnTokens.Bg; // full-screen opaque page
            LvnChrome.Backdrop(this);   // тема без атмосферы не делает ничего

            _scroll = Lvn.UI.LvnScroll.Vertical();
            _scroll.style.flexGrow = 1;
            _scroll.style.flexShrink = 1;
            Add(_scroll);

            // Sticky action bar — a sibling of the scroll, so it never scrolls away.
            _actionBar = new VisualElement();
            _actionBar.style.flexShrink = 0;
            Add(_actionBar);

            // Кромка — у Кромочника: он же будит на повороте, которого смена
            // геометрии не замечает.
            Lvn.UI.LvnEdges.Follow(this, _ => ApplySafeArea());

            Rebuild();
        }

        // Жизненный цикл — в LvnOverlayScreen. Здесь остаётся только смысл
        // ДВУХ исходов: «играть» подтверждает ожидание, «назад» отменяет.
        private void Play() => Close();
        private void Back() => Cancel();


        /// <summary>Слова, шрифт или размеры сменились — перечитать их.</summary>

        public override void Rebuild()
        {
            _scroll.Clear();

            _scroll.Add(BuildHero()); // the back button lives on the hero

            var body = new VisualElement();
            body.style.flexShrink = 0;
            LvnAir.PadX(body, LvnEdges.PageSide);
            body.style.paddingBottom = LvnEdges.PageSide;
            body.style.paddingTop = LvnTokens.Space3;
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
            if (_backBtn != null) _backBtn.style.top = Lvn.UI.LvnEdges.Top(this, air: BackAir);
            _actionBar.style.paddingBottom = Lvn.UI.LvnEdges.Bottom(this, BarBottomAir);
        }

        // Воздух вокруг кромки — свой у страницы новеллы: кнопка «назад» стоит
        // на картинке, панель действия прижата к домашней полосе.
        private const float BackAir = 16f, BarBottomAir = 18f;

        // ── 1. hero image: full-bleed cover, gradient scrim, title + back over it ──
        private VisualElement BuildHero()
        {
            var hero = new VisualElement();
            _hero = hero;
            hero.style.flexShrink = 0;
            hero.style.height = 700; // placeholder until the width resolves below
            hero.style.backgroundColor = LvnTokens.Surface;
            LvnChrome.Edge(hero, 0.8f);   // кадр — часть интерфейса, а не картинка сверху
            LvnPicture.Fit(hero);
            hero.style.overflow = Overflow.Hidden;
            // fixed aspect: height follows the resolved page width (NOT a percent —
            // percent heights inside scroll content collapse the layout)
            hero.RegisterCallback<GeometryChangedEvent>(e =>
            {
                float w = e.newRect.width;
                if (w > 1f) hero.style.height = Mathf.Round(w * HeroAspect);
            });
            LvnPicture.Photo(hero, ShownHero, _assets);
            // bottom gradient scrim so the overlaid title reads (a real gradient —
            // a flat half-black band leaves an ugly hard edge across the art)
            var scrim = new VisualElement { pickingMode = PickingMode.Ignore };
            LvnChrome.BottomStrip(scrim);
            scrim.style.height = Length.Percent(62f);
            scrim.style.backgroundImage = BottomScrim();
            hero.Add(scrim);

            var overTitle = new Label(ShownName);
            LvnChrome.BottomStrip(overTitle, 30f, 22f);
            overTitle.style.color = LvnTokens.Text;
            overTitle.style.fontSize = LvnTokens.TextXl;
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
            back.style.fontSize = LvnTokens.TextLg; back.style.width = LvnTokens.TouchLg; back.style.height = LvnTokens.TouchLg;
            LvnAir.PadY(back, 0);
            back.style.unityTextAlign = TextAnchor.MiddleCenter;
            LvnStyler.Plate(back, LvnTokens.Veil(0.45f), LvnTokens.Text, LvnTokens.RadiusLg);
            hero.Add(back);

            return hero;
        }

        // ── 2. genre chips row (the title itself sits over the hero) ────────────
        private VisualElement BuildTitleBlock()
        {
            var chips = new VisualElement();
            chips.style.flexShrink = 0;
            LvnFlow.Wrap(chips);
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
            // Отличия от прочих ярлыков — прямо здесь: пилюля вместо
            // скругления, рамка и вертикальный отступ пошире.
            var chip = LvnStyler.Chip(new VisualElement(), LvnTokens.SurfaceHi,
                                      LvnTokens.RadiusPill, LvnTokens.Border,
                                      padY: LvnTokens.Space1);
            chip.style.marginBottom = LvnTokens.Space2;
            chip.style.marginRight = LvnTokens.Space2;

            var lbl = new Label(text);
            lbl.style.color = LvnTokens.TextDim;
            lbl.style.fontSize = LvnTokens.TextXs;
            chip.Add(lbl);
            return chip;
        }

        // ── 3. synopsis paragraph ────────────────────────────────────────────
        private VisualElement BuildSynopsis()
        {
            var p = new Label(ShownSynopsis);
            p.style.flexShrink = 0;
            ScreenUi.Quiet(p, LvnTokens.TextSm);
            p.style.marginTop = LvnTokens.Space2;
            return p;
        }

        // ── 4. player stats — trait pairs (proportional bar, no fixed max) plus
        // per-character relationship meters (0..max bar), driven entirely by
        // Title.stats + StatVars. A title with no stats configured renders no
        // section at all — never placeholder numbers.
        private VisualElement BuildStatsSection()
        {
            if (Title?.stats == null || Title.stats.Count == 0) return null;

            var section = ScreenUi.Section(() => LvnWords.Of("stats.title", "Your stats"));

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

            var section = ScreenUi.Section(() => LvnWords.Of("chapters.title", "Chapters"));

            // «Что с этой главой» спрашиваем у дома: правило одно на все три
            // списка глав в приложении, а жило оно здесь одним выражением, и
            // соседи знали от него только половину.
            var marks = LvnChapterMarks.ForAll(Title, chapterList);
            for (int i = 0; i < chapterList.Count; i++)
                section.Add(ChapterRow(chapterList[i].number, ChapterLabel(chapterList[i]), marks[i]));

            return section;
        }

        private VisualElement ChapterRow(int no, string name, LvnChapterMark state)
        {
            bool locked = state == LvnChapterMark.Locked;

            var row = LvnStyler.ListRow(new VisualElement());
            LvnAir.PadX(row, LvnTokens.Space3);
            row.style.marginTop = LvnTokens.Space2;

            var numBadge = new Label(no.ToString());
            numBadge.style.width = 48;
            numBadge.style.height = 48;
            numBadge.style.flexShrink = 0;
            numBadge.style.marginRight = LvnTokens.Space3;
            numBadge.style.unityTextAlign = TextAnchor.MiddleCenter;
            numBadge.style.fontSize = LvnTokens.TextSm;
            numBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            numBadge.style.color = state == LvnChapterMark.Current ? LvnTokens.OnAccent : LvnTokens.Text;
            numBadge.style.backgroundColor = state == LvnChapterMark.Current ? LvnTokens.Accent : LvnTokens.SurfaceHi;
            LvnChrome.Round(numBadge, LvnTokens.RadiusLg);
            row.Add(numBadge);

            var nameLbl = new Label(name);
            nameLbl.style.flexGrow = 1;
            nameLbl.style.flexShrink = 1;
            nameLbl.style.fontSize = LvnTokens.TextSm;
            nameLbl.style.overflow = Overflow.Hidden;
            nameLbl.style.textOverflow = TextOverflow.Ellipsis;
            nameLbl.style.whiteSpace = WhiteSpace.NoWrap;
            nameLbl.style.color = locked ? LvnTokens.TextDim : LvnTokens.Text;
            row.Add(nameLbl);

            // Состояние главы: иконка И слово. Одной иконки мало — «пройдено» и
            // «текущая» слишком близки по смыслу, чтобы различаться только
            // фигуркой; одного слова мало — глаз ищет метку слева от текста.
            var stateColor = state == LvnChapterMark.Done ? LvnTokens.Gold
                : state == LvnChapterMark.Current ? LvnTokens.Accent
                : state == LvnChapterMark.Open ? LvnTokens.Text
                : LvnTokens.TextDim;
            var stateBox = ScreenUi.Row();
            stateBox.style.flexShrink = 0;
            stateBox.style.marginLeft = LvnTokens.Space2;
            var stateIcon = LvnIcons.Make(
                state == LvnChapterMark.Done ? LvnIcon.Check
                    : state == LvnChapterMark.Current || state == LvnChapterMark.Open ? LvnIcon.Play
                    : LvnIcon.Lock,
                17f, stateColor);
            stateIcon.style.marginRight = LvnTokens.Tight;
            stateBox.Add(stateIcon);
            var stateLbl = new Label(state == LvnChapterMark.Done ? LvnWords.Of("chapter.done", "finished")
                : state == LvnChapterMark.Current ? LvnWords.Of("chapter.current", "current")
                : state == LvnChapterMark.Open ? LvnWords.Of("chapter.available", "available") : LvnWords.Of("chapter.locked", "locked"));
            stateLbl.style.fontSize = LvnTokens.TextXs;
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
            LvnAir.PadX(bar, LvnEdges.PageSide);
            bar.style.paddingTop = LvnTokens.Space3;
            bar.style.paddingBottom = LvnTokens.Space3; // + safe inset via ApplySafeArea
            LvnChrome.Divider(bar, LvnSide.Top);
            bar.style.backgroundColor = LvnTokens.Bg;

            // "Начать заново" — only once there's progress worth restarting; sits
            // right under the Play action so it reads as a secondary option.
            if (LvnProgress.Touched(Title))
            {
                var restart = Lvn.UI.LvnRedress.Bind(new Button(ShowRestartMenu), () => LvnWords.Of("title.restart", "Start over"));
                restart.style.marginBottom = LvnTokens.Space2;
                restart.style.fontSize = LvnTokens.TextSm;
                LvnAir.PadY(restart, LvnTokens.Space2);
                LvnStyler.Quiet(restart, LvnTokens.RadiusSm);
                bar.Add(restart);
            }

            var actionRow = ScreenUi.Row();
            bar.Add(actionRow);

            var play = Lvn.UI.LvnRedress.Bind(new Button(Play), () => LvnWords.Of("hub.play", "Play"));
            play.style.flexGrow = 1;
            play.style.flexShrink = 1;
            play.style.fontSize = LvnTokens.TextBase;
            LvnAir.PadY(play, LvnTokens.Space3);
            play.style.marginRight = LvnTokens.Space2;
            play.style.unityFontStyleAndWeight = FontStyle.Bold;
            LvnStyler.Primary(play, LvnTokens.RadiusSm);
            actionRow.Add(play);

            // БЕСПЛАТНЫЙ ВХОД НЕ ПОКАЗЫВАЕТ ЦЕНУ. Раньше плашка стояла всегда:
            // у новеллы без своей цены она рисовала выдуманную единицу, а
            // бесплатная по free_chapters глава показывала цену и не списывала
            // ничего.
            var price = ShownPrice;
            if (price.Free) return;

            var cost = new VisualElement();
            cost.style.flexShrink = 0;
            LvnAir.Pad(cost, LvnTokens.Space3, LvnTokens.Space2);
            cost.style.backgroundColor = LvnTokens.SurfaceHi;
            LvnChrome.Round(cost, LvnTokens.RadiusSm);

            // Значок — ВАЛЮТЫ ЭТОЙ ЦЕНЫ, а не прибитая молния: гейт вправе
            // стоять в любой валюте, и энергия здесь была догадкой экрана.
            cost.Add(LvnPriceTag.Tag(price.Currency, price.Amount,
                new LvnPriceTag.Row { FontSize = 26f, IconSize = 22f, Gap = 6f }));
            actionRow.Add(cost);
        }

        // ── restart flow ─────────────────────────────────────────────────────
        // A modal offering the two genre-standard restarts: wipe the whole
        // expedition (chapter one, empty stats) or roll back to a chosen chapter
        // (its entry-checkpoint stats). Both launch through Play() — the host's
        // normal entry gate then charges and runs the chapter.









        // ── shared bits ──────────────────────────────────────────────────────
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
