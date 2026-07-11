using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The unlockable CG art gallery — a full-screen overlay (scrim + sheet) in the
    /// "Полночь" palette. Shows a responsive grid of art tiles: unlocked ones render
    /// the CG with a caption, locked ones read as dimmed "???" placeholders behind a
    /// lock. Tapping an unlocked tile opens a fullscreen viewer with ‹ › paging over
    /// the unlocked set. Ships with hardcoded demo data so it looks complete before a
    /// host feeds it real entries via <see cref="SetEntries"/>.
    ///
    /// Mirrors <see cref="StoreScreen"/>'s overlay contract: a TCS-gated
    /// <see cref="ShowAsync"/> that fades in, parks until Close, then fades out.
    /// </summary>
    public sealed class CgGalleryScreen : VisualElement
    {
        /// <summary>One gallery entry: its art url, a caption, and whether the player
        /// has unlocked it. Locked entries never reveal the image.</summary>
        public sealed class Entry
        {
            public string Url;
            public string Caption;
            public bool Unlocked;
        }

        private readonly ILvnAssets _assets;
        private readonly List<Entry> _entries = new List<Entry>();

        private readonly Label _counter;
        private readonly ScrollView _grid;

        // Fullscreen viewer overlay (built once, toggled).
        private readonly VisualElement _viewer;
        private readonly VisualElement _viewerImage;
        private readonly Label _viewerCaption;

        private TaskCompletionSource<bool> _tcs;
        private bool _open;

        // Which unlocked entry the viewer is currently showing (index into _entries).
        private int _viewIndex = -1;

        public CgGalleryScreen(ILvnAssets assets)
        {
            _assets = assets;
            LoadDemoData();

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
            sheet.style.paddingTop = 22;
            sheet.style.paddingBottom = 18;
            sheet.style.paddingLeft = 20;
            sheet.style.paddingRight = 20;
            Add(sheet);

            // ── Header: ‹ back · "Галерея" · counter ────────────────────────────
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 16;
            sheet.Add(header);

            var back = new Button(Close) { text = "‹" };
            back.style.fontSize = 36;
            back.style.width = 52;
            back.style.height = 52;
            back.style.marginRight = 12;
            back.style.paddingTop = 0;
            back.style.paddingBottom = 0;
            back.style.paddingLeft = 0;
            back.style.paddingRight = 0;
            back.style.color = LvnTokens.Text;
            back.style.backgroundColor = LvnTokens.Faint;
            ClearBorder(back);
            Round(back, LvnTokens.RadiusSm);
            header.Add(back);

            var title = new Label("Галерея");
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 42;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            header.Add(title);

            _counter = new Label();
            _counter.style.color = LvnTokens.Gold;
            _counter.style.fontSize = 20;
            _counter.style.paddingLeft = 14;
            _counter.style.paddingRight = 14;
            _counter.style.paddingTop = 7;
            _counter.style.paddingBottom = 7;
            _counter.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            Round(_counter, 14f);
            header.Add(_counter);

            // ── Grid of tiles ──────────────────────────────────────────────────
            _grid = new ScrollView(ScrollViewMode.Vertical);
            _grid.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _grid.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _grid.style.flexGrow = 1;
            _grid.contentContainer.style.flexDirection = FlexDirection.Row;
            _grid.contentContainer.style.flexWrap = Wrap.Wrap;
            _grid.contentContainer.style.justifyContent = Justify.FlexStart;
            sheet.Add(_grid);

            // ── Fullscreen viewer (hidden until a tile is opened) ──────────────
            _viewer = ScreenUi.Stretch(new VisualElement());
            _viewer.style.backgroundColor = new Color(0f, 0f, 0f, 0.92f);
            _viewer.style.display = DisplayStyle.None;
            _viewer.RegisterCallback<ClickEvent>(evt => { if (evt.target == _viewer) CloseViewer(); });
            Add(_viewer);

            _viewerImage = new VisualElement { pickingMode = PickingMode.Ignore };
            _viewerImage.style.position = Position.Absolute;
            _viewerImage.style.left = Length.Percent(8f);
            _viewerImage.style.right = Length.Percent(8f);
            _viewerImage.style.top = Length.Percent(10f);
            _viewerImage.style.bottom = Length.Percent(14f);
            _viewerImage.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            _viewerImage.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _viewerImage.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _viewerImage.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            _viewer.Add(_viewerImage);

            var close = new Button(CloseViewer) { text = "✕" };
            close.style.position = Position.Absolute;
            close.style.top = Length.Percent(4f);
            close.style.right = Length.Percent(6f);
            close.style.fontSize = 32;
            close.style.width = 56;
            close.style.height = 56;
            close.style.paddingTop = 0;
            close.style.paddingBottom = 0;
            close.style.paddingLeft = 0;
            close.style.paddingRight = 0;
            close.style.color = LvnTokens.Text;
            close.style.backgroundColor = new Color(1f, 1f, 1f, 0.12f);
            ClearBorder(close);
            Round(close, 28f);
            _viewer.Add(close);

            var prev = new Button(() => Page(-1)) { text = "‹" };
            StyleArrow(prev);
            prev.style.left = Length.Percent(3f);
            _viewer.Add(prev);

            var next = new Button(() => Page(1)) { text = "›" };
            StyleArrow(next);
            next.style.right = Length.Percent(3f);
            _viewer.Add(next);

            _viewerCaption = new Label();
            _viewerCaption.style.position = Position.Absolute;
            _viewerCaption.style.left = 0;
            _viewerCaption.style.right = 0;
            _viewerCaption.style.bottom = Length.Percent(5f);
            _viewerCaption.style.unityTextAlign = TextAnchor.MiddleCenter;
            _viewerCaption.style.color = LvnTokens.Text;
            _viewerCaption.style.fontSize = 28;
            _viewerCaption.pickingMode = PickingMode.Ignore;
            _viewer.Add(_viewerCaption);

            Rebuild();
        }

        // ── Public overlay contract ────────────────────────────────────────────

        /// <summary>Open the gallery: rebuild the grid, fade in, and resolve when the
        /// player closes it (mirrors <see cref="StoreScreen.ShowAsync"/>).</summary>
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
                CloseViewer();
                _open = false;
            }
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            CloseViewer();
            _open = false;
            _tcs?.TrySetResult(false);
        }

        /// <summary>Replace the gallery entries and re-render. Null clears to empty.</summary>
        public void SetEntries(IEnumerable<Entry> entries)
        {
            _entries.Clear();
            if (entries != null) _entries.AddRange(entries);
            Rebuild();
        }

        /// <summary>Re-render the grid + counter from the current entries. Safe to call
        /// repeatedly (e.g. after an unlock).</summary>
        public void Rebuild()
        {
            if (_grid == null) return;
            _grid.Clear();

            int unlocked = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Unlocked) unlocked++;
                _grid.Add(Tile(i));
            }
            _counter.text = $"{unlocked} / {_entries.Count} открыто";
        }

        private void Close() => _tcs?.TrySetResult(true);

        // ── Grid tile ──────────────────────────────────────────────────────────

        private VisualElement Tile(int index)
        {
            var entry = _entries[index];

            var cell = new VisualElement();
            cell.style.width = Length.Percent(31.5f);
            cell.style.height = 150;
            cell.style.marginRight = Length.Percent(1.5f);
            cell.style.marginBottom = 12;
            cell.style.backgroundColor = LvnTokens.Surface;
            cell.style.overflow = Overflow.Hidden;
            Round(cell, LvnTokens.RadiusSm);
            cell.style.borderTopWidth = 1;
            cell.style.borderBottomWidth = 1;
            cell.style.borderLeftWidth = 1;
            cell.style.borderRightWidth = 1;
            cell.style.borderTopColor = LvnTokens.Border;
            cell.style.borderBottomColor = LvnTokens.Border;
            cell.style.borderLeftColor = LvnTokens.Border;
            cell.style.borderRightColor = LvnTokens.Border;

            var art = ScreenUi.Stretch(new VisualElement());
            art.pickingMode = PickingMode.Ignore;
            art.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            art.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            art.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            art.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            cell.Add(art);

            if (entry.Unlocked)
            {
                _ = ScreenUi.AssignBgAsync(art, entry.Url, _assets);

                // Caption strip along the bottom.
                var cap = new VisualElement();
                cap.style.position = Position.Absolute;
                cap.style.left = 0;
                cap.style.right = 0;
                cap.style.bottom = 0;
                cap.style.paddingTop = 6;
                cap.style.paddingBottom = 6;
                cap.style.paddingLeft = 10;
                cap.style.paddingRight = 10;
                cap.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
                cap.pickingMode = PickingMode.Ignore;
                cell.Add(cap);

                var capLabel = new Label(entry.Caption);
                capLabel.style.color = LvnTokens.Text;
                capLabel.style.fontSize = 20;
                capLabel.pickingMode = PickingMode.Ignore;
                cap.Add(capLabel);

                cell.RegisterCallback<ClickEvent>(evt => OpenViewer(index));
            }
            else
            {
                // Locked: keep the art dark (never load it) and stamp a lock + "???".
                art.style.opacity = 0.18f;
                art.style.backgroundColor = LvnTokens.SurfaceHi;

                var lockL = new Label("🔒");
                lockL.style.position = Position.Absolute;
                lockL.style.left = 0;
                lockL.style.right = 0;
                lockL.style.top = Length.Percent(32f);
                lockL.style.unityTextAlign = TextAnchor.MiddleCenter;
                lockL.style.fontSize = 36;
                lockL.style.color = LvnTokens.TextDim;
                lockL.pickingMode = PickingMode.Ignore;
                cell.Add(lockL);

                var q = new Label("???");
                q.style.position = Position.Absolute;
                q.style.left = 0;
                q.style.right = 0;
                q.style.bottom = Length.Percent(14f);
                q.style.unityTextAlign = TextAnchor.MiddleCenter;
                q.style.fontSize = 20;
                q.style.color = LvnTokens.TextDim;
                q.pickingMode = PickingMode.Ignore;
                cell.Add(q);
            }

            return cell;
        }

        // ── Fullscreen viewer ──────────────────────────────────────────────────

        private void OpenViewer(int index)
        {
            if (index < 0 || index >= _entries.Count || !_entries[index].Unlocked) return;
            _viewIndex = index;
            _viewer.style.display = DisplayStyle.Flex;
            RenderViewer();
        }

        private void CloseViewer()
        {
            _viewIndex = -1;
            if (_viewer != null) _viewer.style.display = DisplayStyle.None;
        }

        // Page to the next/previous UNLOCKED entry, wrapping around.
        private void Page(int dir)
        {
            if (_viewIndex < 0 || _entries.Count == 0) return;
            int n = _entries.Count;
            for (int step = 1; step <= n; step++)
            {
                int i = ((_viewIndex + dir * step) % n + n) % n;
                if (_entries[i].Unlocked)
                {
                    _viewIndex = i;
                    RenderViewer();
                    return;
                }
            }
        }

        private void RenderViewer()
        {
            if (_viewIndex < 0 || _viewIndex >= _entries.Count) return;
            var entry = _entries[_viewIndex];
            _viewerImage.style.backgroundImage = new StyleBackground((Texture2D)null);
            _ = ScreenUi.AssignBgAsync(_viewerImage, entry.Url, _assets);
            _viewerCaption.text = entry.Caption;
        }

        private static void StyleArrow(Button b)
        {
            b.style.position = Position.Absolute;
            b.style.top = Length.Percent(46f);
            b.style.fontSize = 42;
            b.style.width = 60;
            b.style.height = 60;
            b.style.paddingTop = 0;
            b.style.paddingBottom = 0;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.color = LvnTokens.Text;
            b.style.backgroundColor = new Color(1f, 1f, 1f, 0.12f);
            ClearBorder(b);
            Round(b, 30f);
        }

        // ── Demo data ──────────────────────────────────────────────────────────

        // Hardcoded fallback so the gallery looks complete before a host supplies
        // real entries: 8 unlocked + 4 locked = 12, cycling the demo card art.
        private void LoadDemoData()
        {
            _entries.Clear();
            const int total = 12;
            const int unlocked = 8;
            for (int i = 0; i < total; i++)
            {
                _entries.Add(new Entry
                {
                    Url = $"/content/cards/card{i % 9}.png",
                    Caption = $"CG {i + 1}",
                    Unlocked = i < unlocked,
                });
            }
        }

        // ── Local helpers (copied from StoreScreen conventions) ────────────────

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
            el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0;
            el.style.borderRightWidth = 0;
        }
    }
}
