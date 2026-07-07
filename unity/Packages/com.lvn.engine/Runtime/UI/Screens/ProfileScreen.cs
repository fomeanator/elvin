using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The player PROFILE overlay — a scrim plus a scrollable sheet, themed from
    /// <see cref="LvnTokens"/> (the "Полночь" palette). It shows an identity card
    /// (avatar + name + level + XP bar), a row of stat tiles, an achievements
    /// grid, a relationships list (affection meters), and a footer with the
    /// player's UID and a copy button. Every value here ships with a hardcoded
    /// fallback so the screen renders standalone; a host wires live data by
    /// setting the public fields and calling <see cref="Rebuild"/>.
    /// </summary>
    public sealed class ProfileScreen : VisualElement
    {
        /// <summary>One earned/locked achievement badge.</summary>
        public struct Achievement
        {
            public string Icon;
            public string Title;
            public bool Unlocked;
            public Achievement(string icon, string title, bool unlocked)
            { Icon = icon; Title = title; Unlocked = unlocked; }
        }

        /// <summary>One character relationship row (0..1 affection).</summary>
        public struct Relation
        {
            public string Name;
            public float Affection; // 0..1
            public Relation(string name, float affection)
            { Name = name; Affection = Mathf.Clamp01(affection); }
        }

        /// <summary>One stat tile: a big number over a caption.</summary>
        public struct Stat
        {
            public string Value;
            public string Caption;
            public Stat(string value, string caption)
            { Value = value; Caption = caption; }
        }

        // ── Live/overridable model (hardcoded demo fallbacks) ──────────────
        public string PlayerName = "Гость";
        public string AvatarGlyph = "👤";
        public string AvatarUrl;               // optional art; falls back to the glyph
        public int Level = 7;
        public int Xp = 1240;
        public int XpNext = 2000;
        public string Uid = "u_f025ad58dc6eb656";

        public List<Stat> Stats = new List<Stat>
        {
            new Stat("24", "Пройдено глав"),
            new Stat("6",  "Свиданий"),
            new Stat("11", "Концовок"),
            new Stat("5",  "Дней подряд"),
        };

        public List<Achievement> Achievements = new List<Achievement>
        {
            new Achievement("🌟", "Первый шаг",   true),
            new Achievement("💘", "Первое свидание", true),
            new Achievement("📖", "Знаток глав",  true),
            new Achievement("🎭", "Все концовки", false),
            new Achievement("🔥", "Неделя подряд", true),
            new Achievement("👑", "Максимум любви", false),
            new Achievement("🗝️", "Тайный путь",  false),
            new Achievement("🏆", "Мастер новелл", false),
        };

        public List<Relation> Relations = new List<Relation>
        {
            new Relation("Виктория", 0.80f),
            new Relation("Леонардо", 0.45f),
            new Relation("Ада",      0.62f),
            new Relation("Маркус",   0.30f),
        };

        private readonly ILvnAssets _assets;
        private readonly ScrollView _body;

        private TaskCompletionSource<bool> _tcs;
        private bool _open;

        public ProfileScreen(ILvnAssets assets)
        {
            _assets = assets;

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
            sheet.style.top = Length.Percent(7f);
            sheet.style.bottom = Length.Percent(7f);
            sheet.style.backgroundColor = LvnTokens.PanelBg;
            Round(sheet, LvnTokens.Radius + 4f);
            sheet.style.paddingTop = 20;
            sheet.style.paddingBottom = 16;
            sheet.style.paddingLeft = 20;
            sheet.style.paddingRight = 20;
            Add(sheet);

            // ── Top bar: back (‹) + "Профиль" ─────────────────────────────
            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 14;
            sheet.Add(top);

            var back = new Button(Close) { text = "‹" };
            back.style.fontSize = 36;
            back.style.width = 48;
            back.style.height = 48;
            back.style.marginRight = 8;
            back.style.paddingTop = 0;
            back.style.paddingBottom = 0;
            back.style.paddingLeft = 0;
            back.style.paddingRight = 0;
            back.style.color = LvnTokens.Text;
            back.style.backgroundColor = LvnTokens.Faint;
            ClearBorder(back);
            Round(back, LvnTokens.RadiusSm);
            top.Add(back);

            var title = new Label("Профиль");
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 42;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            top.Add(title);

            // ── Scrollable body ───────────────────────────────────────────
            _body = new ScrollView(ScrollViewMode.Vertical);
            _body.style.flexGrow = 1;
            sheet.Add(_body);

            Rebuild();
        }

        /// <summary>Tear down and rebuild the whole body from the current model.
        /// Cheap enough to call after mutating any of the public fields.</summary>
        public void Rebuild()
        {
            _body.Clear();
            _body.Add(BuildIdentityCard());
            _body.Add(BuildStatRow());

            _body.Add(SectionHeader("Достижения"));
            _body.Add(BuildAchievements());

            _body.Add(SectionHeader("Отношения"));
            _body.Add(BuildRelations());

            _body.Add(BuildFooter());
        }

        // ── Section 2: identity card ───────────────────────────────────────
        private VisualElement BuildIdentityCard()
        {
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = LvnTokens.Surface;
            Round(card, LvnTokens.Radius);
            card.style.paddingTop = 18;
            card.style.paddingBottom = 18;
            card.style.paddingLeft = 18;
            card.style.paddingRight = 18;
            card.style.marginBottom = 16;

            // Circular avatar with an Accent ring.
            const float avatarSize = 96f;
            var avatar = new VisualElement();
            avatar.style.width = avatarSize;
            avatar.style.height = avatarSize;
            avatar.style.marginRight = 18;
            avatar.style.alignItems = Align.Center;
            avatar.style.justifyContent = Justify.Center;
            avatar.style.backgroundColor = LvnTokens.SurfaceHi;
            Round(avatar, avatarSize / 2f);
            avatar.style.borderTopWidth = 3;
            avatar.style.borderBottomWidth = 3;
            avatar.style.borderLeftWidth = 3;
            avatar.style.borderRightWidth = 3;
            avatar.style.borderTopColor = LvnTokens.Accent;
            avatar.style.borderBottomColor = LvnTokens.Accent;
            avatar.style.borderLeftColor = LvnTokens.Accent;
            avatar.style.borderRightColor = LvnTokens.Accent;

            var glyph = new Label(string.IsNullOrEmpty(AvatarGlyph) ? "👤" : AvatarGlyph);
            glyph.style.fontSize = 50;
            glyph.style.color = LvnTokens.Text;
            glyph.pickingMode = PickingMode.Ignore;
            avatar.Add(glyph);
            if (!string.IsNullOrEmpty(AvatarUrl))
            {
                avatar.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                _ = ScreenUi.AssignBgAsync(avatar, AvatarUrl, _assets);
            }
            card.Add(avatar);

            // Name + level + XP.
            var col = new VisualElement();
            col.style.flexGrow = 1;
            card.Add(col);

            var name = new Label(string.IsNullOrEmpty(PlayerName) ? "Гость" : PlayerName);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = 34;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            col.Add(name);

            var level = new Label($"Уровень {Level}");
            level.style.color = LvnTokens.Accent;
            level.style.fontSize = 26;
            level.style.marginTop = 2;
            level.style.marginBottom = 12;
            col.Add(level);

            // XP progress: a track with an Accent fill.
            int next = XpNext > 0 ? XpNext : 1;
            float frac = Mathf.Clamp01((float)Xp / next);

            var track = new VisualElement();
            track.style.height = 16;
            track.style.backgroundColor = LvnTokens.SurfaceHi;
            Round(track, 8f);
            track.style.overflow = Overflow.Hidden;
            col.Add(track);

            var fill = new VisualElement();
            fill.style.height = 16;
            fill.style.width = Length.Percent(frac * 100f);
            fill.style.backgroundColor = LvnTokens.Accent;
            Round(fill, 8f);
            track.Add(fill);

            var xpLabel = new Label($"{Xp:N0} / {next:N0} XP");
            xpLabel.style.color = LvnTokens.TextDim;
            xpLabel.style.fontSize = 20;
            xpLabel.style.marginTop = 6;
            col.Add(xpLabel);

            return card;
        }

        // ── Section 3: stat tiles ──────────────────────────────────────────
        private VisualElement BuildStatRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 8;

            foreach (var s in Stats) row.Add(StatTile(s));
            return row;
        }

        private VisualElement StatTile(Stat s)
        {
            var tile = new VisualElement();
            tile.style.flexGrow = 1;
            tile.style.flexBasis = Length.Percent(22f);
            tile.style.minWidth = 120;
            tile.style.marginBottom = 10;
            tile.style.marginRight = 8;
            tile.style.alignItems = Align.Center;
            tile.style.backgroundColor = LvnTokens.Surface;
            Round(tile, LvnTokens.RadiusSm);
            tile.style.paddingTop = 16;
            tile.style.paddingBottom = 16;
            tile.style.paddingLeft = 8;
            tile.style.paddingRight = 8;

            var value = new Label(s.Value);
            value.style.color = LvnTokens.Gold;
            value.style.fontSize = 36;
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            tile.Add(value);

            var caption = new Label(s.Caption);
            caption.style.color = LvnTokens.TextDim;
            caption.style.fontSize = 20;
            caption.style.marginTop = 4;
            caption.style.whiteSpace = WhiteSpace.Normal;
            caption.style.unityTextAlign = TextAnchor.MiddleCenter;
            tile.Add(caption);

            return tile;
        }

        // ── Section 4: achievements grid ───────────────────────────────────
        private VisualElement BuildAchievements()
        {
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.justifyContent = Justify.FlexStart;
            grid.style.marginBottom = 8;

            foreach (var a in Achievements) grid.Add(Badge(a));
            return grid;
        }

        private VisualElement Badge(Achievement a)
        {
            var badge = new VisualElement();
            badge.style.flexBasis = Length.Percent(23f);
            badge.style.flexGrow = 1;
            badge.style.minWidth = 110;
            badge.style.marginRight = 8;
            badge.style.marginBottom = 8;
            badge.style.alignItems = Align.Center;
            badge.style.backgroundColor = a.Unlocked ? LvnTokens.SurfaceHi : LvnTokens.Surface;
            Round(badge, LvnTokens.RadiusSm);
            badge.style.paddingTop = 14;
            badge.style.paddingBottom = 14;
            badge.style.paddingLeft = 6;
            badge.style.paddingRight = 6;
            if (!a.Unlocked) badge.style.opacity = 0.55f;

            var icon = new Label(a.Unlocked ? a.Icon : "🔒");
            icon.style.fontSize = 32;
            icon.style.color = a.Unlocked ? LvnTokens.Accent : LvnTokens.TextDim;
            badge.Add(icon);

            var label = new Label(a.Title);
            label.style.color = a.Unlocked ? LvnTokens.Text : LvnTokens.TextDim;
            label.style.fontSize = 20;
            label.style.marginTop = 6;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.Add(label);

            return badge;
        }

        // ── Section 5: relationships ───────────────────────────────────────
        private VisualElement BuildRelations()
        {
            var list = new VisualElement();
            list.style.marginBottom = 8;
            foreach (var r in Relations) list.Add(RelationRow(r));
            return list;
        }

        private VisualElement RelationRow(Relation r)
        {
            var row = new VisualElement();
            row.style.backgroundColor = LvnTokens.Surface;
            Round(row, LvnTokens.RadiusSm);
            row.style.paddingTop = 14;
            row.style.paddingBottom = 14;
            row.style.paddingLeft = 16;
            row.style.paddingRight = 16;
            row.style.marginBottom = 10;

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.marginBottom = 8;
            row.Add(head);

            var name = new Label($"♥ {r.Name}");
            name.style.color = LvnTokens.Text;
            name.style.fontSize = 26;
            head.Add(name);

            var pct = new Label($"{Mathf.RoundToInt(r.Affection * 100f)}%");
            pct.style.color = LvnTokens.Accent;
            pct.style.fontSize = 24;
            pct.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.Add(pct);

            var track = new VisualElement();
            track.style.height = 14;
            track.style.backgroundColor = LvnTokens.SurfaceHi;
            Round(track, 7f);
            track.style.overflow = Overflow.Hidden;
            row.Add(track);

            var fill = new VisualElement();
            fill.style.height = 14;
            fill.style.width = Length.Percent(r.Affection * 100f);
            fill.style.backgroundColor = LvnTokens.Accent;
            Round(fill, 7f);
            track.Add(fill);

            return row;
        }

        // ── Section 6: footer (UID + copy) ─────────────────────────────────
        private VisualElement BuildFooter()
        {
            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.alignItems = Align.Center;
            footer.style.justifyContent = Justify.SpaceBetween;
            footer.style.marginTop = 8;
            footer.style.paddingTop = 12;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = LvnTokens.Border;

            var id = string.IsNullOrEmpty(Uid) ? "u_unknown" : Uid;
            var idLabel = new Label($"ID: {Shorten(id)}");
            idLabel.style.color = LvnTokens.TextDim;
            idLabel.style.fontSize = 20;
            idLabel.style.flexGrow = 1;
            footer.Add(idLabel);

            var copy = new Button { text = "Копировать" };
            copy.style.fontSize = 20;
            copy.style.paddingTop = 10;
            copy.style.paddingBottom = 10;
            copy.style.paddingLeft = 16;
            copy.style.paddingRight = 16;
            copy.style.color = LvnTokens.OnAccent;
            copy.style.backgroundColor = LvnTokens.Accent;
            ClearBorder(copy);
            Round(copy, LvnTokens.RadiusSm);
            copy.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = id;
                var was = copy.text;
                copy.text = "Скопировано ✓";
                copy.schedule.Execute(() => copy.text = was).ExecuteLater(1400);
            };
            footer.Add(copy);

            return footer;
        }

        private Label SectionHeader(string text)
        {
            var lbl = new Label(text);
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = 28;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.marginTop = 8;
            lbl.style.marginBottom = 10;
            return lbl;
        }

        private static string Shorten(string id)
            => id != null && id.Length > 12 ? id.Substring(0, 12) + "…" : id;

        // ── Overlay lifecycle (mirrors StoreScreen) ────────────────────────

        /// <summary>Open the profile: fade the scrim in and park on a TCS that
        /// <see cref="Close"/> resolves, then fade back out.</summary>
        public async Task ShowAsync(CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            style.display = DisplayStyle.Flex;
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);
            // Hide() during the fade-in must cancel the open, not leave this
            // await parked on a _tcs nobody will ever resolve.
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

        private void Close() => _tcs?.TrySetResult(true);

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
