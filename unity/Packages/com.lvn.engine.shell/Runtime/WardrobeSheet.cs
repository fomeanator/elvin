using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The IN-STORY wardrobe: a bottom sheet over the running scene — the
    /// genre-standard "dress up mid-chapter" moment. No preview pane: the LIVE
    /// actor on stage is the mirror. Browsing writes try-on values into
    /// <see cref="LvnWardrobe.Preview"/>, which the stage picks up instantly;
    /// the confirm button buys whatever's previewed-but-unowned (wallet skus),
    /// commits every previewed slot and closes; the collapse arrow cancels and
    /// the actor snaps back. Layout mirrors the reference flow: icon tabs per
    /// slot, a ◀ item name ▶ carousel, one big confirm with the total price.
    /// Opened by <c>ext wardrobe_show char=id</c> (the story holds meanwhile).
    /// </summary>
    public sealed class WardrobeSheet : VisualElement
    {
        private readonly WardrobeConfig _cfg;
        private readonly DialogueConfig _dlg;
        private readonly ChoicesConfig _ch;
        private readonly ILvnAssets _assets;
        private readonly Color _text, _dim, _accent, _accentText;
        private readonly float _radius;

        private readonly Label _title;
        private readonly VisualElement _tabs;
        private readonly Label _itemName;
        private readonly Button _confirm;
        private readonly VisualElement _balances;

        /// <summary>Host hook: open the currency store (the pills' "+" tap).
        /// NovelShell wires this to its StoreScreen.</summary>
        public System.Func<Task> OpenStore;
        /// <summary>Fired on confirm, once per equipped axis that carries a story `var`
        /// (entity, storyVar, value) — the host writes it back into the novel's state.
        /// Not fired on cancel/collapse or for a skipped axis, so an unchanged slot
        /// keeps its current value.</summary>
        public System.Action<string, string, string> OnEquip;

        /// <summary>Fired when the player switches the character pill
        /// (fromEntity, toEntity) — the host swaps who stands on the canvas.
        /// The sheet clears the outgoing character's previews itself.</summary>
        public System.Action<string, string> OnCharacterPicked;

        /// <summary>The character currently being dressed (BuildFor target) —
        /// hosts read it on close to strike the right actor from the stage.</summary>
        public string CurrentEntity => _entity;

        /// <summary>The always-open (quick-menu) mode: list ONLY outfits that
        /// crossed the player's path — worn by an actor, offered by a story
        /// wardrobe moment, or already owned. A story-opened sheet (false)
        /// shows the author's full catalog for the beat and MARKS it seen.
        /// Set before every ShowAsync — the instance is shared between paths.</summary>
        public bool OnlySeen;

        private LvnManifest _manifest;
        private string _entity;
        private LvnSpriteEntity _def;
        private string _tab;
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(); // axis → carousel pos

        private TaskCompletionSource<bool> _tcs;
        private bool _open;
        private bool _buying;

        public WardrobeSheet(WardrobeConfig cfg, ILvnAssets assets)
            : this(cfg, null, null, assets) { }

        /// <summary>The NATIVE skin: the sheet dresses itself in the game's own
        /// dialogue form (panel art/colours from <paramref name="dlg"/>) with
        /// choice-styled buttons (<paramref name="ch"/>) — a themed title (the
        /// gothic frame, the parchment box…) skins the wardrobe for free, like
        /// every other piece of chrome. ui.wardrobe fields stay as overrides.
        /// The sheet is CONTENT for the stage's shared window
        /// (<c>VnPanelHost</c>) — the frame, position and show/hide transitions
        /// belong to the host, so the sheet draws no panel of its own and its
        /// ShowAsync only runs the logic.</summary>
        public WardrobeSheet(WardrobeConfig cfg, DialogueConfig dlg, ChoicesConfig ch, ILvnAssets assets)
        {
            _cfg = cfg ?? new WardrobeConfig();
            _dlg = dlg;
            _ch = ch;
            _assets = assets;
            _text = UiColor.Parse(_cfg.text_color ?? _dlg?.text_color, new Color(0.95f, 0.93f, 0.88f));
            _dim = UiColor.Parse(_cfg.dim_text_color, new Color(0.60f, 0.58f, 0.54f));
            _accent = UiColor.Parse(_cfg.accent_color ?? _dlg?.speaker_color, new Color(0.78f, 0.63f, 0.31f));
            _accentText = UiColor.Parse(_cfg.accent_text_color, new Color(0.08f, 0.08f, 0.10f));
            _radius = _cfg.corner_radius ?? _dlg?.corner_radius ?? 12f;

            // balance pills FLOAT above the sheet (the genre-standard "wallet
            // over the wardrobe"), including zero balances for any currency the
            // wardrobe charges in — so "not enough crystals" is never a mystery.
            _balances = new VisualElement();
            _balances.style.position = Position.Absolute;
            _balances.style.left = 0;
            _balances.style.bottom = Length.Percent(100f);
            _balances.style.marginBottom = 10;
            _balances.style.flexDirection = FlexDirection.Row;
            _balances.style.alignItems = Align.Center;
            Add(_balances);

            var headRow = new VisualElement();
            headRow.style.flexDirection = FlexDirection.Row;
            headRow.style.alignItems = Align.Center;
            headRow.style.justifyContent = Justify.Center;
            Add(headRow);

            _title = new Label(_cfg.title ?? "Wardrobe");
            _title.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            _title.style.fontSize = 28;
            _title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _title.style.paddingLeft = 24; _title.style.paddingRight = 24;
            _title.style.paddingTop = 4; _title.style.paddingBottom = 4;
            _title.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            Round(_title, _radius);
            headRow.Add(_title);

            var collapse = new Button(Cancel) { text = "▼" };
            collapse.style.position = Position.Absolute;
            collapse.style.right = 0;
            collapse.style.fontSize = 22;
            collapse.style.paddingLeft = 14; collapse.style.paddingRight = 14;
            collapse.style.paddingTop = 6; collapse.style.paddingBottom = 6;
            SkinButton(collapse, false);
            headRow.Add(collapse);

            // Character pills — ONLY the always-open wardrobe shows them, and
            // only when several dressable characters have a collection. A story
            // moment dresses exactly who the author says.
            _rosterRow = new VisualElement();
            _rosterRow.style.flexDirection = FlexDirection.Row;
            _rosterRow.style.flexWrap = Wrap.Wrap;
            _rosterRow.style.justifyContent = Justify.Center;
            _rosterRow.style.marginTop = 14;
            _rosterRow.style.display = DisplayStyle.None;
            Add(_rosterRow);

            _tabs = new VisualElement();
            _tabs.style.flexDirection = FlexDirection.Row;
            _tabs.style.justifyContent = Justify.Center;
            _tabs.style.marginTop = 24; // breathing room under the title before the tabs
            Add(_tabs);

            // ◀ item name ▶
            var carousel = new VisualElement();
            carousel.style.flexDirection = FlexDirection.Row;
            carousel.style.alignItems = Align.Center;
            carousel.style.marginTop = 12;
            Add(carousel);

            var prev = new Button(() => Step(-1)) { text = "◀" };
            var next = new Button(() => Step(+1)) { text = "▶" };
            foreach (var b in new[] { prev, next })
            {
                b.style.fontSize = 24;
                b.style.paddingLeft = 16; b.style.paddingRight = 16;
                b.style.paddingTop = 10; b.style.paddingBottom = 10;
                SkinButton(b, false);
            }
            carousel.Add(prev);

            _itemName = new Label("");
            _itemName.style.flexGrow = 1;
            _itemName.style.color = _text;
            _itemName.style.fontSize = 28;
            _itemName.style.unityTextAlign = TextAnchor.MiddleCenter;
            _itemName.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            _itemName.style.marginLeft = 10; _itemName.style.marginRight = 10;
            _itemName.style.paddingTop = 10; _itemName.style.paddingBottom = 10;
            Round(_itemName, _radius);
            carousel.Add(_itemName);
            carousel.Add(next);

            _confirm = new Button(() => _ = ConfirmAsync());
            _confirm.style.fontSize = 28;
            _confirm.style.marginTop = 12;
            _confirm.style.paddingTop = 14;
            _confirm.style.paddingBottom = 14;
            SkinButton(_confirm, true);
            Add(_confirm);
        }

        public void SetManifest(LvnManifest manifest) => _manifest = manifest;

        private VisualElement _rosterRow;
        private List<(string id, string name)> _roster;

        /// <summary>Give the sheet a character roster (menu/hub mode). Null or a
        /// single entry hides the pills. Call before ShowAsync — cleared state
        /// persists on the shared instance otherwise.</summary>
        public void SetRoster(List<(string id, string name)> roster) => _roster = roster;

        private void RebuildRoster()
        {
            if (_rosterRow == null) return;
            _rosterRow.Clear();
            int shown = 0;
            if (_roster != null && _roster.Count > 1)
            {
                foreach (var (id, name) in _roster)
                {
                    if (OnlySeen && id != _entity && !HasAnyCollected(id)) continue;
                    var pid = id;
                    var b = new Button(() => SwitchTo(pid)) { text = name };
                    b.style.height = 44;
                    b.style.marginLeft = 4; b.style.marginRight = 4; b.style.marginBottom = 6;
                    b.style.paddingLeft = 16; b.style.paddingRight = 16;
                    b.style.fontSize = 20;
                    bool active = pid == _entity;
                    SkinButton(b, active);
                    Border(b, active ? _accent : new Color(1f, 1f, 1f, 0.15f), 2f);
                    _rosterRow.Add(b);
                    shown++;
                }
            }
            _rosterRow.style.display = shown > 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SwitchTo(string id)
        {
            if (string.IsNullOrEmpty(id) || id == _entity) return;
            var from = _entity;
            LvnWardrobe.ClearPreview(from); // the outgoing look snaps back
            OnCharacterPicked?.Invoke(from, id);
            BuildFor(id);
            RefreshBalances();
        }

        // Does this entity have anything to show in collection mode? Mirrors
        // Items()' Encountered rule without switching the sheet to it.
        private bool HasAnyCollected(string id)
        {
            if (_manifest?.sprites == null || !_manifest.sprites.TryGetValue(id, out var d)
                || d?.wardrobe == null) return false;
            foreach (var kv in d.wardrobe)
                if (kv.Value?.items != null)
                    foreach (var it in kv.Value.items)
                        if (it != null && !string.IsNullOrEmpty(it.value) && Encountered(id, kv.Key, it.value))
                            return true;
            return false;
        }

        /// <summary>Open the sheet for a character; resolves when the player
        /// confirms or collapses it. The story op awaits this.</summary>
        public async Task ShowAsync(string entityId, CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            _confirm.SetEnabled(true); // never inherit a dead button from a past session
            BuildFor(entityId);
            // A story wardrobe moment IS the outfits crossing the player's path —
            // everything it offers joins the always-open wardrobe's collection.
            if (!OnlySeen && _def?.wardrobe != null)
                foreach (var kv in _def.wardrobe)
                    if (kv.Value?.items != null)
                        foreach (var it in kv.Value.items)
                            if (it != null && !string.IsNullOrEmpty(it.value))
                                LvnWardrobe.MarkSeen(_entity, kv.Key, it.value);
            RefreshBalances();
            LvnWallet.Changed += OnWalletChanged;
            _ = LvnWallet.RefreshAsync();

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
            finally
            {
                LvnWallet.Changed -= OnWalletChanged;
                LvnWardrobe.ClearPreview(_entity); // confirm already committed; cancel snaps back
                _open = false;
            }
        }

        public void Hide()
        {
            LvnWallet.Changed -= OnWalletChanged;
            if (!string.IsNullOrEmpty(_entity)) LvnWardrobe.ClearPreview(_entity);
            _open = false;
            _tcs?.TrySetResult(false);
        }

        private void Cancel() => _tcs?.TrySetResult(false);
        private void OnWalletChanged() { RefreshBalances(); RefreshConfirm(); }

        // Every currency the wardrobe charges in + everything the player holds.
        private void RefreshBalances()
        {
            _balances.Clear();
            var currencies = new List<string>();
            if (_def?.wardrobe != null)
                foreach (var slot in _def.wardrobe.Values)
                    if (slot?.items != null)
                        foreach (var it in slot.items)
                            if (it != null && it.price > 0 && !string.IsNullOrEmpty(it.currency)
                                && !currencies.Contains(it.currency))
                                currencies.Add(it.currency);
            foreach (var kv in LvnWallet.Balances)
                if (!currencies.Contains(kv.Key)) currencies.Add(kv.Key);

            foreach (var cur in currencies)
            {
                LvnWallet.Balances.TryGetValue(cur, out var amount);
                var pill = new VisualElement();
                pill.style.flexDirection = FlexDirection.Row;
                pill.style.alignItems = Align.Center;
                pill.style.marginRight = 8;
                pill.style.paddingLeft = 12; pill.style.paddingRight = 6;
                pill.style.paddingTop = 5; pill.style.paddingBottom = 5;
                pill.style.backgroundColor = UiColor.Parse(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f));
                Round(pill, 16f);

                string iconUrl = _cfg.currency_icons != null
                                 && _cfg.currency_icons.TryGetValue(cur, out var u) ? u : null;
                if (!string.IsNullOrEmpty(iconUrl))
                {
                    var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                    icon.style.width = 26; icon.style.height = 26; icon.style.marginRight = 6;
                    icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    icon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    pill.Add(icon);
                    _ = ScreenUi.AssignBgAsync(icon, iconUrl, _assets);
                }
                var label = new Label(amount.ToString("N0") + (iconUrl == null ? " " + cur : ""));
                label.style.color = _text;
                label.style.fontSize = 22;
                pill.Add(label);

                if (OpenStore != null)
                {
                    var plus = new Button(() => _ = OpenStore()) { text = "+" };
                    plus.style.fontSize = 22;
                    plus.style.marginLeft = 8;
                    plus.style.paddingLeft = 10; plus.style.paddingRight = 10;
                    plus.style.paddingTop = 1; plus.style.paddingBottom = 1;
                    plus.style.color = _accentText;
                    plus.style.backgroundColor = _accent;
                    Round(plus, 12f);
                    pill.Add(plus);
                }
                _balances.Add(pill);
            }
        }

        /// <summary>Build tabs/carousel for a character. Public for tests.</summary>
        public void BuildFor(string entityId)
        {
            _entity = entityId;
            if ((string.IsNullOrEmpty(_entity) || _manifest?.sprites == null
                 || !_manifest.sprites.ContainsKey(_entity)) && _manifest?.sprites != null)
            {
                foreach (var kv in _manifest.sprites) // fallback: first with a wardrobe
                    if (kv.Value?.wardrobe != null && kv.Value.wardrobe.Count > 0) { _entity = kv.Key; break; }
            }
            _def = _entity != null && _manifest?.sprites != null
                   && _manifest.sprites.TryGetValue(_entity, out var d) ? d : null;
            _index.Clear();
            _tabs.Clear();
            _tab = null;
            _title.text = _cfg.title ?? "Wardrobe";

            RebuildRoster();
            if (_def?.wardrobe == null || _def.wardrobe.Count == 0)
            {
                _itemName.text = _cfg.empty_text ?? "The wardrobe is empty";
                RefreshConfirm();
                return;
            }

            foreach (var kv in _def.wardrobe)
            {
                var axis = kv.Key;
                if (Items(axis).Count == 0) continue; // nothing collected here yet
                if (_tab == null) _tab = axis;
                var slot = kv.Value;
                var b = new Button(() => SelectTab(axis));
                b.style.width = 92; b.style.height = 92;
                b.style.marginLeft = 6; b.style.marginRight = 6;
                Round(b, _radius);
                b.userData = axis;
                if (!string.IsNullOrEmpty(slot?.icon))
                {
                    b.text = "";
                    b.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    b.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    _ = ScreenUi.AssignBgAsync(b, slot.icon, _assets);
                }
                else
                {
                    b.text = slot?.name ?? axis;
                    b.style.fontSize = 20;
                    b.style.whiteSpace = WhiteSpace.Normal;
                }
                _tabs.Add(b);
            }

            // The hero must OPEN the sheet already dressed from THIS sheet: an
            // axis whose worn value isn't among the scene's items previews its
            // FIRST item right away, for every axis — not just the active tab.
            // Otherwise she stands in last chapter's (possibly retired) outfit
            // until the player taps that tab, and the swap lands as a jump.
            foreach (var kv in _def.wardrobe)
            {
                var axis = kv.Key;
                var items = Items(axis);
                if (items.Count == 0) continue;
                LvnWardrobe.Previewed(_entity).TryGetValue(axis, out var worn);
                if (worn == null) LvnWardrobe.Equipped(_entity).TryGetValue(axis, out worn);
                bool inList = false;
                foreach (var it in items)
                    if (it.value == worn) { inList = true; break; }
                if (!inList)
                {
                    _index[axis] = 0;
                    LvnWardrobe.Preview(_entity, axis, items[0].value);
                }
            }

            SelectTab(_tab);
        }

        private void SelectTab(string axis)
        {
            _tab = axis;
            foreach (var c in _tabs.Children())
            {
                var b = c as Button;
                if (b == null) continue;
                bool active = (string)b.userData == _tab;
                SkinButton(b, active);
                Border(b, active ? _accent : new Color(1f, 1f, 1f, 0.15f), 2f);
            }

            var items = Items(_tab);
            if (items.Count == 0) { _itemName.text = ""; RefreshConfirm(); return; }
            if (!_index.ContainsKey(_tab))
            {
                // start the carousel on what's worn (previewed beats equipped)
                LvnWardrobe.Previewed(_entity).TryGetValue(_tab, out var current);
                if (current == null) LvnWardrobe.Equipped(_entity).TryGetValue(_tab, out current);
                int at = 0;
                for (int i = 0; i < items.Count; i++) if (items[i].value == current) { at = i; break; }
                _index[_tab] = at;
            }
            ShowItem(); // also previews it, so the carousel and the actor agree
        }

        private void Step(int dir)
        {
            var items = Items(_tab);
            if (items.Count == 0) return;
            _index[_tab] = ((_index.TryGetValue(_tab, out var i) ? i : 0) + dir + items.Count) % items.Count;
            ShowItem();
        }

        private void ShowItem()
        {
            var items = Items(_tab);
            if (items.Count == 0) return;
            var item = items[Mathf.Clamp(_index.TryGetValue(_tab, out var i) ? i : 0, 0, items.Count - 1)];
            _itemName.text = item.name ?? item.value;
            bool owned = item.price <= 0
                || LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, _tab, item.value));
            Debug.Log($"[lvn-wardrobe] sheet preview {_entity}.{_tab} = '{item.value}' " +
                      $"(price={item.price} {item.currency ?? "-"}, owned={owned})");
            LvnWardrobe.Preview(_entity, _tab, item.value); // the live actor is the mirror
            RefreshConfirm();
        }

        // The confirm button carries the total price of everything previewed
        // but not yet owned, per currency — free/owned try-ons confirm at 0.
        private void RefreshConfirm()
        {
            // Self-healing: any refresh (browse, wallet change, reopen) revives
            // the button unless a confirm is genuinely in flight — a missed
            // delayed-enable can never leave it dead again.
            if (!_buying) _confirm.SetEnabled(true);

            var costs = UnownedPreviewCosts();
            var text = _cfg.confirm_text ?? "Choose";
            if (costs.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in costs) parts.Add($"{kv.Value:N0} {kv.Key}");
                text += ":  " + string.Join(" + ", parts);
                var have = new List<string>();
                foreach (var kv in costs)
                    have.Add($"{kv.Key}: need {kv.Value}, have {(LvnWallet.Balances.TryGetValue(kv.Key, out var b) ? b : 0)}");
                Debug.Log($"[lvn-wardrobe] sheet confirm price → {string.Join("; ", have)}");
            }
            _confirm.text = text;
        }

        private Dictionary<string, long> UnownedPreviewCosts()
        {
            var costs = new Dictionary<string, long>();
            if (_def?.wardrobe == null) return costs;
            foreach (var kv in LvnWardrobe.Previewed(_entity))
            {
                var item = Find(kv.Key, kv.Value);
                if (item == null || item.price <= 0) continue;
                if (LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, kv.Key, item.value))) continue;
                costs.TryGetValue(item.currency ?? "", out var sum);
                costs[item.currency ?? ""] = sum + item.price;
            }
            return costs;
        }

        private async Task ConfirmAsync()
        {
            if (_buying) return;
            _buying = true;
            var label = _confirm.text;
            _confirm.SetEnabled(false);
            _confirm.text = "…";
            try
            {
                // Re-sync the wallet FIRST: ownership decisions below must run
                // against the server's inventory, not a stale mirror — otherwise
                // an already-bought item could be charged twice.
                await LvnWallet.RefreshAsync();

                // buy everything previewed-but-unowned, then commit the lot
                var previewed = new Dictionary<string, string>();
                foreach (var kv in LvnWardrobe.Previewed(_entity)) previewed[kv.Key] = kv.Value;
                Debug.Log($"[lvn-wardrobe] sheet CONFIRM: previewed [{string.Join(", ", ToPairs(previewed))}], " +
                          $"balances [{string.Join(", ", ToPairs(LvnWallet.Balances))}], " +
                          $"inventory [{string.Join(", ", LvnWallet.Inventory.Keys)}]");
                // Per-slot commit: a failed buy rolls only ITS slot back to
                // what's equipped and never blocks the rest of the look — the
                // player keeps the free/owned pieces they picked.
                var failed = new List<string>();
                foreach (var kv in previewed)
                {
                    var item = Find(kv.Key, kv.Value);
                    if (item == null)
                    {
                        Debug.LogWarning($"[lvn-wardrobe] previewed {kv.Key}='{kv.Value}' has NO catalog item — skipped");
                        continue;
                    }
                    var sku = LvnWardrobe.Sku(_entity, kv.Key, item.value);
                    if (item.price > 0 && !LvnWallet.Inventory.ContainsKey(sku))
                    {
                        Debug.Log($"[lvn-wardrobe] buying {sku}: {item.price} {item.currency ?? "(null currency!)"}");
                        bool ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
                        Debug.Log($"[lvn-wardrobe] buy {sku} → {(ok ? "OK" : "FAILED")}; " +
                                  $"balances now [{string.Join(", ", ToPairs(LvnWallet.Balances))}]");
                        LvnAnalytics.Track(ok ? "wardrobe_buy" : "wardrobe_buy_fail",
                            ("entity", _entity), ("sku", sku));
                        if (!ok)
                        {
                            failed.Add($"{item.price:N0} {item.currency}");
                            LvnWardrobe.Preview(_entity, kv.Key, null); // snap this slot back
                            continue;
                        }
                    }
                    LvnWardrobe.Equip(_entity, kv.Key, kv.Value);
                    // Write the pick back into the novel's story state (nested var) so
                    // its downstream logic reads the choice — only for axes bound to one.
                    string sv = _def?.wardrobe != null && _def.wardrobe.TryGetValue(kv.Key, out var slot)
                        ? slot.storyVar : null;
                    if (!string.IsNullOrEmpty(sv)) OnEquip?.Invoke(_entity, sv, kv.Value);
                }
                Debug.Log($"[lvn-wardrobe] sheet confirm DONE — equipped [{string.Join(", ", ToPairs(LvnWardrobe.Equipped(_entity)))}]" +
                          (failed.Count > 0 ? $", failed [{string.Join(", ", failed)}]" : ""));
                if (failed.Count == 0)
                {
                    LvnWardrobe.ClearPreview(_entity); // equips now cover the look
                    _tcs?.TrySetResult(true);
                }
                else
                {
                    // The affordable part is applied; say WHY the rest didn't
                    // land and stay open so the player can rethink or top up.
                    _confirm.text = (_cfg.insufficient_text ?? "Not enough") + ": " + string.Join(" + ", failed);
                    _confirm.schedule.Execute(() =>
                    {
                        _confirm.SetEnabled(true);
                        RefreshConfirm();
                    }).ExecuteLater(1800);
                }
            }
            finally
            {
                _buying = false;
                if (_confirm.enabledSelf == false && _confirm.text == "…")
                { _confirm.SetEnabled(true); _confirm.text = label; }
            }
        }

        private static IEnumerable<string> ToPairs<T>(IReadOnlyDictionary<string, T> map)
        {
            foreach (var kv in map) yield return $"{kv.Key}={kv.Value}";
        }

        private List<LvnWardrobeItem> Items(string axis)
        {
            var list = new List<LvnWardrobeItem>();
            if (axis != null && _def?.wardrobe != null
                && _def.wardrobe.TryGetValue(axis, out var slot) && slot?.items != null)
                foreach (var it in slot.items)
                    if (it != null && !string.IsNullOrEmpty(it.value)
                        && (!OnlySeen || Encountered(axis, it.value)))
                        list.Add(it);
            return list;
        }

        // Part of the player's collection: seen along the way, bought (the
        // wallet remembers across reinstalls), or what they're wearing right now.
        private bool Encountered(string axis, string value) => Encountered(_entity, axis, value);

        private static bool Encountered(string entity, string axis, string value)
        {
            if (LvnWardrobe.IsSeen(entity, axis, value)) return true;
            if (LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(entity, axis, value))) return true;
            return LvnWardrobe.Equipped(entity).TryGetValue(axis, out var worn) && worn == value;
        }

        private LvnWardrobeItem Find(string axis, string value)
        {
            foreach (var it in Items(axis)) if (it.value == value) return it;
            return null;
        }

        // Choice-button skin: the same fill/text/art the story's choices use,
        // so the wardrobe's controls read as the game's own buttons.
        private void SkinButton(Button b, bool accent)
        {
            b.style.color = accent ? _accentText : UiColor.Parse(_ch?.text_color, _text);
            b.style.backgroundColor = accent
                ? _accent
                : UiColor.Parse(_ch?.color, new Color(1f, 1f, 1f, 0.07f));
            Round(b, _ch?.corner_radius ?? _radius);
            if (!accent && !string.IsNullOrEmpty(_ch?.button_image))
                _ = ApplyNineSliceAsync(b, _ch.button_image, _ch.button_slice ?? 0);
            else
                b.style.backgroundImage = new StyleBackground(StyleKeyword.None); // an accent tab drops the art
        }

        private async Task ApplyNineSliceAsync(VisualElement el, string url, int slice)
        {
            if (el == null || string.IsNullOrEmpty(url) || _assets == null) return;
            try
            {
                var sprite = await _assets.LoadSpriteAsync(url, CancellationToken.None);
                if (sprite == null) return;
                el.style.backgroundImage = new StyleBackground(sprite);
                el.style.backgroundColor = Color.clear; // the art replaces the flat fill
                if (slice > 0)
                {
                    el.style.unitySliceLeft = slice;
                    el.style.unitySliceRight = slice;
                    el.style.unitySliceTop = slice;
                    el.style.unitySliceBottom = slice;
                }
            }
            catch { /* missing art keeps the flat look */ }
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        private static void Border(VisualElement el, Color c, float w)
        {
            el.style.borderTopColor = c; el.style.borderBottomColor = c;
            el.style.borderLeftColor = c; el.style.borderRightColor = c;
            el.style.borderTopWidth = w; el.style.borderBottomWidth = w;
            el.style.borderLeftWidth = w; el.style.borderRightWidth = w;
        }
    }
}
