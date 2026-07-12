using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
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
    public sealed class LeaderboardScreen : VisualElement
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

        private TaskCompletionSource<bool> _tcs;
        private bool _open;
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

            ScreenUi.Stretch(this);
            style.backgroundColor = LvnTokens.Scrim;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            // tap the scrim (not the sheet) to close
            RegisterCallback<ClickEvent>(evt => { if (evt.target == this) Close(); });

            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Length.Percent(5f);
            sheet.style.right = Length.Percent(5f);
            sheet.style.top = Length.Percent(6f);
            sheet.style.bottom = Length.Percent(6f);
            sheet.style.backgroundColor = LvnTokens.PanelBg;
            Round(sheet, LvnTokens.Radius + 4f);
            sheet.style.paddingTop = 20;
            sheet.style.paddingBottom = 18;
            sheet.style.paddingLeft = 20;
            sheet.style.paddingRight = 20;
            Add(sheet);

            // ── Header: back + title ────────────────────────────────────────
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 14;
            sheet.Add(header);

            var back = new Button(Close) { text = "‹" };
            back.style.fontSize = 34;
            back.style.width = 46;
            back.style.height = 46;
            back.style.paddingLeft = 0; back.style.paddingRight = 0;
            back.style.paddingTop = 0; back.style.paddingBottom = 0;
            back.style.marginRight = 12;
            back.style.color = LvnTokens.Text;
            back.style.backgroundColor = LvnTokens.Faint;
            ClearBorder(back);
            Round(back, LvnTokens.RadiusSm);
            header.Add(back);

            var title = new Label("Рейтинг");
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 40;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            header.Add(title);

            // ── Segment tabs: Неделя / Всё время ────────────────────────────
            var tabs = new VisualElement();
            tabs.style.flexDirection = FlexDirection.Row;
            tabs.style.alignSelf = Align.Center;
            tabs.style.marginBottom = 18;
            tabs.style.backgroundColor = LvnTokens.Surface;
            Round(tabs, LvnTokens.RadiusSm + 4f);
            tabs.style.paddingLeft = 4; tabs.style.paddingRight = 4;
            tabs.style.paddingTop = 4; tabs.style.paddingBottom = 4;
            sheet.Add(tabs);

            _tabWeek = Pill("Неделя", () => SetPeriod(true));
            _tabAll = Pill("Всё время", () => SetPeriod(false));
            tabs.Add(_tabWeek);
            tabs.Add(_tabAll);

            // ── Podium (top 3) ──────────────────────────────────────────────
            _podium = new VisualElement();
            _podium.style.flexDirection = FlexDirection.Row;
            _podium.style.justifyContent = Justify.Center;
            _podium.style.alignItems = Align.FlexEnd;
            _podium.style.marginBottom = 18;
            sheet.Add(_podium);

            // ── Ranked list (#4..) ──────────────────────────────────────────
            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.style.flexGrow = 1;
            sheet.Add(_list);

            SyncTabs();
            Rebuild();
        }

        // ── Public surface ──────────────────────────────────────────────────

        /// <summary>Open the leaderboard: fade in, wait until the player closes it,
        /// fade out. Mirrors the store overlay's lifecycle.</summary>
        public async Task ShowAsync(CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            style.display = DisplayStyle.Flex;
            Rebuild();
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);
            // Hide() during the fade-in must cancel the open, not leave this await
            // parked on a _tcs nobody will ever resolve.
            if (!_open) return;

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
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

        /// <summary>Re-render the podium and the list from <see cref="Entries"/>.</summary>
        public void Rebuild()
        {
            BuildPodium();
            BuildList();
        }

        private void Close() => _tcs?.TrySetResult(true);

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

        private static void StyleTab(Button tab, bool active)
        {
            tab.style.color = active ? LvnTokens.OnAccent : LvnTokens.TextDim;
            tab.style.backgroundColor = active ? LvnTokens.Accent : Color.clear;
            tab.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
        }

        private Button Pill(string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = 24;
            b.style.paddingTop = 10; b.style.paddingBottom = 10;
            b.style.paddingLeft = 24; b.style.paddingRight = 24;
            b.style.marginLeft = 0; b.style.marginRight = 0;
            ClearBorder(b);
            Round(b, LvnTokens.RadiusSm);
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
            col.style.marginLeft = 8; col.style.marginRight = 8;
            col.style.width = first ? 138 : 112;
            if (!first) col.style.marginBottom = 10; // sink the flanks below the winner

            // Crown for the champion.
            var crown = new Label(first ? "👑" : " ");
            crown.style.fontSize = 32;
            crown.style.marginBottom = 2;
            crown.style.unityTextAlign = TextAnchor.MiddleCenter;
            col.Add(crown);

            // Avatar with an accent gold ring on 1st.
            var ring = new VisualElement();
            ring.style.width = avatar + (first ? 12 : 8);
            ring.style.height = avatar + (first ? 12 : 8);
            ring.style.alignItems = Align.Center;
            ring.style.justifyContent = Justify.Center;
            ring.style.backgroundColor = first ? LvnTokens.Gold
                : (place == 2 ? new Color(0.78f, 0.80f, 0.86f) : new Color(0.80f, 0.55f, 0.35f));
            Round(ring, (avatar + 12f) / 2f);
            col.Add(ring);

            ring.Add(Avatar(e, avatar));

            // Rank badge.
            var badge = new Label(place.ToString());
            badge.style.fontSize = 22;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.color = LvnTokens.OnAccent;
            badge.style.backgroundColor = first ? LvnTokens.Gold
                : (place == 2 ? new Color(0.78f, 0.80f, 0.86f) : new Color(0.80f, 0.55f, 0.35f));
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.style.width = 34; badge.style.height = 34;
            badge.style.marginTop = -16;
            Round(badge, 17f);
            col.Add(badge);

            var name = new Label(e.Name);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = 24;
            name.style.marginTop = 8;
            name.style.unityFontStyleAndWeight = first ? FontStyle.Bold : FontStyle.Normal;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.maxWidth = first ? 138 : 112;
            col.Add(name);

            var score = new Label(e.Score.ToString("N0"));
            score.style.color = first ? LvnTokens.Gold : LvnTokens.TextDim;
            score.style.fontSize = first ? 26f : 18f;
            score.style.unityFontStyleAndWeight = first ? FontStyle.Bold : FontStyle.Normal;
            score.style.marginTop = 2;
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
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 8;
            row.style.paddingTop = 10; row.style.paddingBottom = 10;
            row.style.paddingLeft = 14; row.style.paddingRight = 16;
            row.style.backgroundColor = e.IsYou
                ? new Color(LvnTokens.Accent.r, LvnTokens.Accent.g, LvnTokens.Accent.b, 0.18f)
                : LvnTokens.Surface;
            Round(row, LvnTokens.RadiusSm);
            if (e.IsYou)
            {
                row.style.borderLeftWidth = 3;
                row.style.borderTopWidth = 0; row.style.borderRightWidth = 0; row.style.borderBottomWidth = 0;
                row.style.borderLeftColor = LvnTokens.Accent;
            }

            // Rank number — tabular, right-aligned in a fixed gutter.
            var rank = new Label(e.Rank.ToString());
            rank.style.width = 42;
            rank.style.fontSize = 24;
            rank.style.color = e.IsYou ? LvnTokens.Accent : LvnTokens.TextDim;
            rank.style.unityFontStyleAndWeight = e.IsYou ? FontStyle.Bold : FontStyle.Normal;
            rank.style.unityTextAlign = TextAnchor.MiddleRight;
            rank.style.marginRight = 12;
            row.Add(rank);

            row.Add(Avatar(e, 48f));

            var nameCol = new VisualElement();
            nameCol.style.flexGrow = 1;
            nameCol.style.marginLeft = 12;
            nameCol.style.flexDirection = FlexDirection.Row;
            nameCol.style.alignItems = Align.Center;
            row.Add(nameCol);

            var name = new Label(e.Name);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = 24;
            name.style.unityFontStyleAndWeight = e.IsYou ? FontStyle.Bold : FontStyle.Normal;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            nameCol.Add(name);

            if (e.IsYou)
            {
                var you = new Label("Вы");
                you.style.fontSize = 18;
                you.style.color = LvnTokens.OnAccent;
                you.style.backgroundColor = LvnTokens.Accent;
                you.style.unityFontStyleAndWeight = FontStyle.Bold;
                you.style.unityTextAlign = TextAnchor.MiddleCenter;
                you.style.marginLeft = 10;
                you.style.paddingLeft = 10; you.style.paddingRight = 10;
                you.style.paddingTop = 2; you.style.paddingBottom = 2;
                Round(you, 11f);
                nameCol.Add(you);
            }

            var score = new Label(e.Score.ToString("N0"));
            score.style.color = e.IsYou ? LvnTokens.Text : LvnTokens.TextDim;
            score.style.fontSize = 24;
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
            av.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            av.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            Round(av, size / 2f);
            ClearBorder(av);

            var initial = new Label(Initial(e.Name));
            initial.style.color = LvnTokens.OnAccent;
            initial.style.fontSize = size * 0.42f;
            initial.style.unityFontStyleAndWeight = FontStyle.Bold;
            initial.style.unityTextAlign = TextAnchor.MiddleCenter;
            initial.pickingMode = PickingMode.Ignore;
            av.Add(initial);

            if (!string.IsNullOrEmpty(e.AvatarUrl))
                _ = ScreenUi.AssignBgAsync(av, e.AvatarUrl, _assets);

            return av;
        }

        private static string Initial(string name)
            => string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();

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
                ("Виктория", 44120, false),
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
