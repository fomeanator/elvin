using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// A full-screen leaderboard / rankings overlay in the engine's "Полночь"
    /// palette: a scrim, a scrollable sheet, a segment toggle (week / all-time),
    /// a podium for the top three (1st centred, taller, crowned with a gold ring),
    /// and a ranked list of the rest. The viewer's own row is Accent-tinted,
    /// labelled "Вы", and pinned in-list at rank #7.
    ///
    /// The screen renders from a hardcoded fallback dataset so it looks complete
    /// out of the box; a host swaps <see cref="Entries"/> for the live standings
    /// and calls <see cref="Rebuild"/>. Fade / show / hide mirror the other shell
    /// overlays (see <see cref="StoreScreen"/>).
    /// </summary>
    public sealed class LeaderboardScreen : LvnOverlayScreen
    {
        /// <summary>One row in the standings.</summary>
        public sealed class Entry
        {
            public int Rank;
            public string Name;
            public long Score;
            public string AvatarUrl; // optional; falls back to a coloured initial
            public bool IsYou;
        }

        private readonly ILvnAssets _assets;

        private readonly VisualElement _podium;
        private readonly ScrollView _list;
        private readonly Button _tabWeek;
        private readonly Button _tabAll;

        private bool _weekly = true;

        /// <summary>The current standings, ordered by rank ascending. Defaults to a
        /// demo set; a host assigns the live board and calls <see cref="Rebuild"/>.</summary>
        public List<Entry> Entries;

        // A small deterministic palette for fallback avatar circles, so a given
        // name always lands on the same colour.
        private static readonly Color[] AvatarPalette =
        {
            new Color(0.92f, 0.35f, 0.57f), // rose
            new Color(0.46f, 0.55f, 0.95f), // indigo
            new Color(0.38f, 0.74f, 0.60f), // teal
            new Color(0.95f, 0.70f, 0.36f), // amber
            new Color(0.72f, 0.50f, 0.92f), // violet
            new Color(0.90f, 0.52f, 0.44f), // coral
        };

        public LeaderboardScreen(ILvnAssets assets)
        {
            _assets = assets;
            Entries = DemoEntries();

            Lvn.UI.LvnChrome.Scrim(this, Close);
            // tap the scrim (not the sheet) to close

            var sheet = Sheet();   // положение и вид — общие; поля свои
            LvnAir.Pad(sheet, LvnTokens.Space3);

            // ── Header: back + title ────────────────────────────────────────
            var header = ScreenUi.Row();
            header.style.marginBottom = LvnTokens.Space2;
            sheet.Add(header);

            var back = ScreenUi.BackButton(Close, 52f, 36f);
            back.style.marginRight = LvnTokens.Space2;
            header.Add(back);

            var title = SectionTitle(() => LvnWords.Of("leaderboard.title", "Leaderboard"));
            title.style.flexGrow = 1;
            header.Add(title);

            // ── Segment tabs: Неделя / Всё время ────────────────────────────
            var tabs = new VisualElement();
            tabs.style.flexDirection = FlexDirection.Row;
            tabs.style.alignSelf = Align.Center;
            tabs.style.marginBottom = LvnTokens.Space3;
            tabs.style.backgroundColor = LvnTokens.Surface;
            LvnChrome.Edge(tabs);
            LvnChrome.Round(tabs, LvnTokens.Radius);
            LvnAir.Pad(tabs, LvnTokens.Tight);
            sheet.Add(tabs);

            _tabWeek = Pill(LvnWords.Of("board.week", "This week"), () => SetPeriod(true));
            _tabAll = Pill(LvnWords.Of("board.all_time", "All time"), () => SetPeriod(false));
            tabs.Add(_tabWeek);
            tabs.Add(_tabAll);

            // ── Podium (top 3) ──────────────────────────────────────────────
            _podium = new VisualElement();
            _podium.style.flexDirection = FlexDirection.Row;
            _podium.style.justifyContent = Justify.Center;
            _podium.style.alignItems = Align.FlexEnd;
            _podium.style.marginBottom = LvnTokens.Space3;
            sheet.Add(_podium);

            // ── Ranked list (#4..) ──────────────────────────────────────────
            _list = Lvn.UI.LvnScroll.Vertical();
            _list.style.flexGrow = 1;
            sheet.Add(_list);

            SyncTabs();
            Rebuild();
        }

        // ── Public surface ──────────────────────────────────────────────────

        /// <summary>Re-render the podium and the list from <see cref="Entries"/>.</summary>
        /// <summary>Слова, шрифт или размеры сменились — перечитать их.</summary>

        public override void Rebuild()
        {
            BuildPodium();
            BuildList();
        }


        // ── Period toggle ───────────────────────────────────────────────────

        private void SetPeriod(bool weekly)
        {
            if (_weekly == weekly) return;
            _weekly = weekly;
            SyncTabs();
            // A live host refetches the period's board here; the demo regenerates
            // its fallback so the toggle visibly changes the numbers.
            Entries = DemoEntries();
            Rebuild();
        }

        private void SyncTabs()
        {
            StyleTab(_tabWeek, _weekly);
            StyleTab(_tabAll, !_weekly);
        }

        // Вид вкладки — у Стилизатора: здесь стояло СВОЁ правило, и невыбранная
        // вкладка выходила прозрачной, а не приглушённой, как в магазине и на
        // витрине скинов.
        private static void StyleTab(Button tab, bool active) => LvnStyler.Tab(tab, active);

        private Button Pill(string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = LvnTokens.TextSm;
            LvnAir.PadX(b, LvnTokens.Space4);
            LvnAir.PadY(b, LvnTokens.Space2);
            LvnAir.MarginX(b, 0);
            LvnChrome.ClearBorder(b);
            LvnChrome.Round(b, LvnTokens.RadiusSm);
            return b;
        }

        // ── Podium ──────────────────────────────────────────────────────────

        private void BuildPodium()
        {
            _podium.Clear();
            var top = TopN(3);
            if (top.Count == 0) return;

            // Visual order: 2nd on the left, 1st centre, 3rd on the right.
            if (top.Count > 1) _podium.Add(PodiumColumn(top[1], 2));
            _podium.Add(PodiumColumn(top[0], 1));
            if (top.Count > 2) _podium.Add(PodiumColumn(top[2], 3));
        }

        private VisualElement PodiumColumn(Entry e, int place)
        {
            bool first = place == 1;
            float avatar = first ? 108f : 84f;

            var col = new VisualElement();
            col.style.alignItems = Align.Center;
            LvnAir.MarginX(col, LvnTokens.Space1);
            col.style.width = first ? 138 : 112;
            if (!first) col.style.marginBottom = LvnTokens.Space2; // sink the flanks below the winner

            // Crown for the champion.
            // Место под корону занято и у не-победителей: иначе первый столбец
            // выше остальных на высоту иконки и ряд перекашивает.
            var crown = first
                ? LvnIcons.Make(LvnIcon.Crown, 30f, LvnTokens.Gold)
                : new VisualElement { style = { width = 30, height = 30 } };
            crown.style.marginBottom = LvnTokens.Hair;
            crown.style.alignSelf = Align.Center;
            col.Add(crown);

            // Avatar with an accent gold ring on 1st.
            var ring = new VisualElement();
            ring.style.width = avatar + (first ? 12 : 8);
            ring.style.height = avatar + (first ? 12 : 8);
            ring.style.alignItems = Align.Center;
            ring.style.justifyContent = Justify.Center;
            ring.style.backgroundColor = first ? LvnTokens.Gold
                : (place == 2 ? new Color(0.78f, 0.80f, 0.86f) : new Color(0.80f, 0.55f, 0.35f));
            LvnChrome.Round(ring, (avatar + 12f) / 2f);
            col.Add(ring);

            ring.Add(Avatar(e, avatar));

            // Rank badge.
            var badge = new Label(place.ToString());
            badge.style.fontSize = LvnTokens.TextSm;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = LvnTokens.OnAccent;
            badge.style.backgroundColor = first ? LvnTokens.Gold
                : (place == 2 ? new Color(0.78f, 0.80f, 0.86f) : new Color(0.80f, 0.55f, 0.35f));
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.style.width = 34; badge.style.height = 34;
            badge.style.marginTop = -16;
            LvnChrome.Round(badge, LvnTokens.Radius);
            col.Add(badge);

            var name = new Label(e.Name);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextSm;
            name.style.marginTop = LvnTokens.Space1;
            name.style.unityFontStyleAndWeight = first ? FontStyle.Bold : FontStyle.Normal;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.maxWidth = first ? 138 : 112;
            col.Add(name);

            var score = new Label(LvnPriceTag.Amount(e.Score));
            score.style.color = first ? LvnTokens.Gold : LvnTokens.TextDim;
            score.style.fontSize = first ? 26f : 18f;
            score.style.unityFontStyleAndWeight = first ? FontStyle.Bold : FontStyle.Normal;
            score.style.marginTop = LvnTokens.Hair;
            score.style.unityTextAlign = TextAnchor.MiddleCenter;
            col.Add(score);

            return col;
        }

        // ── List ────────────────────────────────────────────────────────────

        private void BuildList()
        {
            _list.Clear();
            var rest = Rest(4);
            foreach (var e in rest) _list.Add(Row(e));
        }

        private VisualElement Row(Entry e)
        {
            // Своя строка подсвечена акцентом — единственное отличие от
            // прочих строк списка, и оно стоит прямо в вызове.
            var row = LvnStyler.ListRow(ScreenUi.Row(),
                e.IsYou ? UiColor.WithAlpha(LvnTokens.Accent, 0.18f) : (Color?)null);
            row.style.marginBottom = LvnTokens.Space1;
            // Поля врозь: слева место под номер, справа — под значение.
            row.style.paddingLeft = LvnTokens.Space2;
            row.style.paddingRight = LvnTokens.Space3;
            if (e.IsYou)
            {
                LvnChrome.Stripe(row);
            }

            // Rank number — tabular, right-aligned in a fixed gutter.
            var rank = new Label(e.Rank.ToString());
            rank.style.width = 42;
            rank.style.fontSize = LvnTokens.TextSm;
            rank.style.color = e.IsYou ? LvnTokens.Accent : LvnTokens.TextDim;
            rank.style.unityFontStyleAndWeight = e.IsYou ? FontStyle.Bold : FontStyle.Normal;
            rank.style.unityTextAlign = TextAnchor.MiddleRight;
            rank.style.marginRight = LvnTokens.Space2;
            row.Add(rank);

            row.Add(Avatar(e, 48f));

            var nameCol = new VisualElement();
            nameCol.style.flexGrow = 1;
            nameCol.style.marginLeft = LvnTokens.Space2;
            ScreenUi.Row(nameCol);
            row.Add(nameCol);

            var name = new Label(e.Name);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextSm;
            name.style.unityFontStyleAndWeight = e.IsYou ? FontStyle.Bold : FontStyle.Normal;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            nameCol.Add(name);

            if (e.IsYou)
            {
                var you = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("leaderboard.you", "You"));
                you.style.fontSize = LvnTokens.TextXs;
                you.style.color = LvnTokens.OnAccent;
                you.style.backgroundColor = LvnTokens.Accent;
                you.style.unityFontStyleAndWeight = FontStyle.Bold;
                you.style.unityTextAlign = TextAnchor.MiddleCenter;
                LvnAir.PadX(you, LvnTokens.Space2);
                LvnAir.PadY(you, LvnTokens.Hair);
                you.style.marginLeft = LvnTokens.Space2;
                LvnChrome.Round(you, LvnTokens.RadiusSm);
                nameCol.Add(you);
            }

            var score = new Label(LvnPriceTag.Amount(e.Score));
            score.style.color = e.IsYou ? LvnTokens.Text : LvnTokens.TextDim;
            score.style.fontSize = LvnTokens.TextSm;
            score.style.unityFontStyleAndWeight = e.IsYou ? FontStyle.Bold : FontStyle.Normal;
            score.style.minWidth = 110;
            score.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(score);

            return row;
        }

        // ── Avatar ──────────────────────────────────────────────────────────

        // A circular avatar: the loaded portrait if a url is set, otherwise a
        // coloured circle with the name's initial. The url path loads async and
        // paints over the fallback when it lands (missing art stays as the circle).
        private VisualElement Avatar(Entry e, float size)
        {
            var av = new VisualElement { pickingMode = PickingMode.Ignore };
            av.style.width = size;
            av.style.height = size;
            av.style.alignItems = Align.Center;
            av.style.justifyContent = Justify.Center;
            av.style.backgroundColor = ColorFor(e.Name);
            LvnPicture.Fit(av);
            LvnChrome.Round(av, size / 2f);
            LvnChrome.ClearBorder(av);

            var initial = new Label(Initial(e.Name));
            initial.style.color = LvnTokens.OnAccent;
            initial.style.fontSize = size * 0.42f;
            initial.style.unityFontStyleAndWeight = FontStyle.Bold;
            initial.style.unityTextAlign = TextAnchor.MiddleCenter;
            initial.pickingMode = PickingMode.Ignore;
            av.Add(initial);

            if (!string.IsNullOrEmpty(e.AvatarUrl))
                LvnPicture.Photo(av, e.AvatarUrl, _assets);
            return av;
        }

        // Первая БУКВА, а не первая единица UTF-16: имя с эмодзи в начале
        // давало в кружке «□» — знак, которого в имени не было.
        private static string Initial(string name)
            => Lvn.LvnClip.FirstLetter(name).ToUpperInvariant();

        private static Color ColorFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return AvatarPalette[0];
            int h = 0;
            foreach (var c in name) h = (h * 31 + c) & 0x7fffffff;
            return AvatarPalette[h % AvatarPalette.Length];
        }

        // ── Data ────────────────────────────────────────────────────────────

        private List<Entry> Sorted()
        {
            var list = Entries ?? new List<Entry>();
            var copy = new List<Entry>(list);
            copy.Sort((a, b) => b.Score.CompareTo(a.Score));
            for (int i = 0; i < copy.Count; i++) copy[i].Rank = i + 1;
            return copy;
        }

        private List<Entry> TopN(int n)
        {
            var s = Sorted();
            var top = new List<Entry>();
            for (int i = 0; i < n && i < s.Count; i++) top.Add(s[i]);
            return top;
        }

        private List<Entry> Rest(int fromRank)
        {
            var s = Sorted();
            var rest = new List<Entry>();
            for (int i = fromRank - 1; i < s.Count; i++) rest.Add(s[i]);
            return rest;
        }

        // Hardcoded fallback standings — Russian names, descending scores, with
        // the viewer pinned at #7 ("Вы"). The weekly / all-time toggle scales the
        // numbers so the segment control visibly does something in the demo.
        private List<Entry> DemoEntries()
        {
            var raw = new (string name, long score, bool you)[]
            {
                ("Аврора", 48210, false),
                ("Максим", 45980, false),
                ("Алиса", 44120, false),
                ("Дмитрий", 41770, false),
                ("Елена", 39640, false),
                ("Артём", 37510, false),
                ("Вы", 35980, true),
                ("София", 34220, false),
                ("Николай", 32890, false),
                ("Полина", 31450, false),
                ("Григорий", 29870, false),
                ("Марина", 28330, false),
                ("Тимур", 26910, false),
                ("Алиса", 25480, false),
                ("Роман", 24020, false),
                ("Ксения", 22760, false),
                ("Лев", 21390, false),
                ("Дарья", 20110, false),
            };

            var list = new List<Entry>(raw.Length);
            foreach (var r in raw)
            {
                // All-time board reads higher than the weekly snapshot.
                long score = _weekly ? r.score : r.score * 6 + 12000;
                list.Add(new Entry { Name = r.name, Score = score, IsYou = r.you, AvatarUrl = null });
            }
            return list;
        }

        // ── Style helpers (copied verbatim across the shell screens) ────────
    }
}
