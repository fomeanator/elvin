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
    /// The hub browse flow (an alternative to <see cref="TitleCarousel"/>, selected
    /// by <c>ui.browse.layout = "hub"</c>): three themeable screens —
    ///   1. HUB      — the app title + a tile per collection (Expeditions/Dates/…),
    ///   2. COLLECTION — the collection's titles as cards,
    ///   3. DETAIL   — a title's image + description + Play.
    /// Locked titles (their <c>unlock</c> expression over the player's
    /// <c>global.*</c> stats is false) show a lock badge and explain themselves on
    /// tap; Play runs the host's <see cref="OnPlay"/> gate (energy cost / store)
    /// and only then resolves. <see cref="PickTitleAsync"/> returns the chosen,
    /// unlocked, paid-for title (or null if cancelled).
    /// </summary>
    public sealed class BrowseHub : VisualElement
    {
        /// <summary>Loads the player's global stat flags (the <c>__global</c> blob)
        /// so <c>unlock</c> conditions can be evaluated. Null → everything unlocked.</summary>
        public System.Func<Task<JObject>> GlobalStatsProvider;
        /// <summary>The Play gate: charge the entry cost / confirm. Returns true to
        /// launch. Null → always launch (free).</summary>
        public System.Func<LvnTitle, Task<bool>> OnPlay;
        /// <summary>Show a message when a locked card is tapped. Null → silent.</summary>
        public System.Func<string, string, Task> OnLockedHint;

        private readonly BrowseConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly Color _bg, _titleColor, _text, _dim, _card, _cardText, _accent, _accentText;
        private readonly float _radius;

        private readonly VisualElement _hubView, _collectionView, _detailView;
        private readonly Label _hubTitle, _hubSubtitle;
        private readonly VisualElement _hubTiles;
        private readonly Label _collectionTitle;
        private readonly ScrollView _collectionList;
        private readonly VisualElement _detailImage;
        private readonly Label _detailTitle, _detailDesc;
        private readonly Button _detailPlay;

        private readonly Dictionary<string, LvnTitle> _titles = new Dictionary<string, LvnTitle>();
        private List<LvnCollection> _collections = new List<LvnCollection>();
        private JObject _globalVars = new JObject(); // cached flags for unlock eval
        private LvnTitle _detailTarget;

        private TaskCompletionSource<LvnTitle> _tcs;

        public BrowseHub(BrowseConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new BrowseConfig();
            _assets = assets;
            _bg = UiColor.Parse(_cfg.bg_color, new Color(0.063f, 0.063f, 0.082f));
            _titleColor = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            _text = UiColor.Parse(_cfg.text_color, new Color(0.91f, 0.89f, 0.85f));
            _dim = UiColor.Parse(_cfg.dim_text_color, new Color(0.60f, 0.58f, 0.54f));
            _card = UiColor.Parse(_cfg.card_color, new Color(0.208f, 0.784f, 0.561f));
            _cardText = UiColor.Parse(_cfg.card_text_color, new Color(0.08f, 0.08f, 0.10f));
            _accent = UiColor.Parse(_cfg.accent_color, new Color(0.208f, 0.784f, 0.561f));
            _accentText = UiColor.Parse(_cfg.accent_text_color, new Color(0.08f, 0.08f, 0.10f));
            _radius = _cfg.card_radius ?? 16f;

            ScreenUi.Stretch(this);
            style.backgroundColor = _bg;

            // ── HUB ──
            _hubView = Column();
            _hubView.style.justifyContent = Justify.Center;
            _hubView.style.alignItems = Align.Center;
            _hubTitle = Heading(_cfg.title ?? "", 44);
            _hubTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _hubView.Add(_hubTitle);
            _hubSubtitle = new Label(_cfg.subtitle ?? "Выбери…");
            _hubSubtitle.style.color = _dim; _hubSubtitle.style.fontSize = 22;
            _hubSubtitle.style.marginBottom = 24;
            _hubView.Add(_hubSubtitle);
            _hubTiles = new VisualElement();
            _hubTiles.style.width = Length.Percent(72);
            _hubView.Add(_hubTiles);
            Add(_hubView);

            // ── COLLECTION ──
            _collectionView = Column();
            _collectionView.Add(BackBar(out _collectionTitle, () => ShowHub()));
            _collectionList = new ScrollView(ScrollViewMode.Vertical);
            _collectionList.style.flexGrow = 1;
            _collectionView.Add(_collectionList);
            Add(_collectionView);

            // ── DETAIL ──
            _detailView = Column();
            _detailView.Add(BackBar(out _detailTitle, BackFromDetail));
            _detailImage = new VisualElement { pickingMode = PickingMode.Ignore };
            _detailImage.style.height = Length.Percent(42);
            _detailImage.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            Round(_detailImage, _radius);
            _detailImage.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            _detailImage.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _detailImage.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _detailImage.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            _detailImage.style.marginBottom = 16;
            _detailView.Add(_detailImage);
            _detailDesc = new Label(string.Empty);
            _detailDesc.style.color = _text; _detailDesc.style.fontSize = 22;
            _detailDesc.style.whiteSpace = WhiteSpace.Normal;
            _detailDesc.style.flexGrow = 1;
            _detailView.Add(_detailDesc);
            _detailPlay = AccentButton(_cfg.play_text ?? "Играть", () => _ = PlayTappedAsync());
            _detailView.Add(_detailPlay);
            Add(_detailView);

            ShowHub();
        }

        public void SetData(List<LvnCollection> collections, List<LvnTitle> titles)
        {
            _titles.Clear();
            if (titles != null)
                foreach (var t in titles)
                    if (t != null && !string.IsNullOrEmpty(t.id))
                        _titles[t.id] = t;
            _collections = collections ?? new List<LvnCollection>();
            BuildHubTiles();
        }

        /// <summary>Run the hub flow; resolves with the chosen title (unlocked and
        /// paid via <see cref="OnPlay"/>), or null if the player never picks one.</summary>
        public async Task<LvnTitle> PickTitleAsync(CancellationToken ct = default)
        {
            _globalVars = (GlobalStatsProvider != null ? await GlobalStatsProvider() : null) ?? new JObject();
            ShowHub();
            BuildHubTiles(); // refresh lock states against the latest flags
            _tcs = new TaskCompletionSource<LvnTitle>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs?.TrySetResult(null));
            return await _tcs.Task;
        }

        // ── navigation ──────────────────────────────────────────────────────────
        private void ShowHub()
        {
            _hubView.style.display = DisplayStyle.Flex;
            _collectionView.style.display = DisplayStyle.None;
            _detailView.style.display = DisplayStyle.None;
        }

        private void ShowCollection(LvnCollection c)
        {
            _collectionTitle.text = c.name ?? c.id;
            _collectionList.Clear();
            if (c.titles != null)
                foreach (var id in c.titles)
                    if (_titles.TryGetValue(id, out var t))
                        _collectionList.Add(TitleCard(t));
            _hubView.style.display = DisplayStyle.None;
            _collectionView.style.display = DisplayStyle.Flex;
            _detailView.style.display = DisplayStyle.None;
        }

        private LvnCollection _detailFrom;
        private void ShowDetail(LvnTitle t, LvnCollection from)
        {
            _detailTarget = t;
            _detailFrom = from;
            _detailTitle.text = t.name ?? t.id;
            var art = t.card;
            _detailDesc.text = art?.description ?? t.subtitle ?? "";
            var img = art?.image ?? t.cover_url;
            if (!string.IsNullOrEmpty(img)) _ = ScreenUi.AssignBgAsync(_detailImage, img, _assets);
            bool locked = IsLocked(t);
            _detailPlay.SetEnabled(!locked);
            _detailPlay.text = locked ? (_cfg.locked_text ?? "🔒")
                : PlayLabel(t);
            _hubView.style.display = DisplayStyle.None;
            _collectionView.style.display = DisplayStyle.None;
            _detailView.style.display = DisplayStyle.Flex;
        }

        private void BackFromDetail()
        {
            if (_detailFrom != null) ShowCollection(_detailFrom);
            else ShowHub();
        }

        private async Task PlayTappedAsync()
        {
            var t = _detailTarget;
            if (t == null || IsLocked(t)) return;
            _detailPlay.SetEnabled(false);
            bool go = OnPlay == null || await OnPlay(t);
            _detailPlay.SetEnabled(true);
            if (go) _tcs?.TrySetResult(t);
        }

        // ── unlock ──────────────────────────────────────────────────────────────
        private bool IsLocked(LvnTitle t)
        {
            if (t == null || string.IsNullOrEmpty(t.unlock)) return false;
            try
            {
                var vars = new Dictionary<string, JToken> { ["global"] = _globalVars };
                return !Lvn.LvnExpression.EvaluateBool(t.unlock, vars);
            }
            catch { return false; } // a bad expression never bricks the hub
        }

        // ── builders ──────────────────────────────────────────────────────────────
        private void BuildHubTiles()
        {
            if (_hubTiles == null) return;
            _hubTiles.Clear();
            foreach (var c in _collections)
                _hubTiles.Add(CollectionTile(c));
        }

        private VisualElement CollectionTile(LvnCollection c)
        {
            var tile = new VisualElement();
            tile.style.backgroundColor = _accent;
            Round(tile, _radius);
            tile.style.marginBottom = 12;
            tile.style.paddingTop = 18; tile.style.paddingBottom = 18;
            tile.style.paddingLeft = 20; tile.style.paddingRight = 20;
            tile.style.alignItems = Align.Center;

            var name = new Label(c.name ?? c.id);
            name.style.color = _accentText;
            name.style.fontSize = 26;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            tile.Add(name);
            if (!string.IsNullOrEmpty(c.subtitle))
            {
                var sub = new Label(c.subtitle);
                sub.style.color = _accentText; sub.style.fontSize = 18; sub.style.opacity = 0.8f;
                tile.Add(sub);
            }
            tile.RegisterCallback<ClickEvent>(_ => ShowCollection(c));
            return tile;
        }

        private VisualElement TitleCard(LvnTitle t)
        {
            bool locked = IsLocked(t);
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = _card;
            card.style.opacity = locked ? 0.55f : 1f;
            Round(card, _radius);
            card.style.marginBottom = 10;
            card.style.paddingTop = 18; card.style.paddingBottom = 18;
            card.style.paddingLeft = 20; card.style.paddingRight = 20;

            var name = new Label(t.name ?? t.id);
            name.style.color = _cardText; name.style.fontSize = 24;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.flexGrow = 1;
            card.Add(name);

            if (locked)
                card.Add(Chip(_cfg.locked_text ?? "🔒"));
            else if (t.cost != null && t.cost.amount > 0)
                card.Add(Chip(string.Format(_cfg.cost_text ?? "{0}", t.cost.amount)));

            card.RegisterCallback<ClickEvent>(evt =>
            {
                if (locked) { _ = OnLockedHint?.Invoke(t.name ?? t.id, t.locked_hint ?? ""); }
                else ShowDetail(t, CurrentCollectionOf(t));
            });
            return card;
        }

        private LvnCollection CurrentCollectionOf(LvnTitle t)
        {
            foreach (var c in _collections)
                if (c.titles != null && c.titles.Contains(t.id)) return c;
            return null;
        }

        private string PlayLabel(LvnTitle t) =>
            t.cost != null && t.cost.amount > 0
                ? (_cfg.play_text ?? "Играть") + "  ·  " + string.Format(_cfg.cost_text ?? "{0}", t.cost.amount)
                : (_cfg.play_text ?? "Играть");

        private Label Chip(string text)
        {
            var chip = new Label(text);
            chip.style.color = _cardText;
            chip.style.fontSize = 20;
            chip.style.backgroundColor = new Color(0f, 0f, 0f, 0.18f);
            chip.style.paddingLeft = 10; chip.style.paddingRight = 10;
            chip.style.paddingTop = 4; chip.style.paddingBottom = 4;
            Round(chip, 10f);
            return chip;
        }

        // ── shared layout bits ──
        private VisualElement Column()
        {
            var col = new VisualElement();
            ScreenUi.Stretch(col);
            col.style.flexDirection = FlexDirection.Column;
            col.style.paddingTop = 28; col.style.paddingBottom = 24;
            col.style.paddingLeft = 22; col.style.paddingRight = 22;
            return col;
        }

        private VisualElement BackBar(out Label title, System.Action onBack)
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.marginBottom = 14;
            var back = new Button(onBack) { text = _cfg.back_text ?? "‹" };
            back.style.fontSize = 30; back.style.minWidth = 52;
            back.style.color = _titleColor;
            back.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            ClearBorder(back); Round(back, _radius);
            bar.Add(back);
            title = Heading("", 30);
            title.style.marginLeft = 12;
            bar.Add(title);
            return bar;
        }

        private Label Heading(string text, int size)
        {
            var l = new Label(text);
            l.style.color = _titleColor; l.style.fontSize = size;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            return l;
        }

        private Button AccentButton(string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.fontSize = 26;
            b.style.marginTop = 14;
            b.style.paddingTop = 14; b.style.paddingBottom = 14;
            b.style.color = _accentText;
            b.style.backgroundColor = _accent;
            ClearBorder(b); Round(b, _radius);
            return b;
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r; el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r; el.style.borderBottomRightRadius = r;
        }

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderTopWidth = 0; el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0; el.style.borderRightWidth = 0;
        }
    }
}
