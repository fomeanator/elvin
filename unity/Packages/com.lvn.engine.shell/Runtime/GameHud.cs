using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The in-game top HUD, themed from a <see cref="HudConfig"/> (manifest
    /// <c>ui.hud</c>): a thin strip with chapter progress on the left (optional
    /// icon + percent) and a row of currency pills (icon + amount) on the right.
    /// Pills are created on demand. <see cref="SetProgress"/> uses the shared
    /// <see cref="Percent"/> rule so every "%" in the UI matches the loading bar.
    /// </summary>
    /// <summary>
    /// ⚠ ПОЛОСА БОЛЬШЕ НЕ ЖИВЁТ В ИГРЕ (решение Ильи 26.08). Её работу —
    /// прогресс главы и балансы валют — взял единый навбар
    /// (<see cref="LvnTopBar"/>): в сцене они висят мини-бабликами по углам,
    /// а затемняющая полоса сверху убрана совсем.
    ///
    /// <para>Класс оставлен как ПУБЛИЧНЫЙ ШОВ: встраивающая игра вправе взять
    /// его и показать сама (<c>NovelShell.Hud</c>). Но кормить его движок
    /// больше не будет — до 28.08 он молча слал сюда балансы на каждое
    /// движение кошелька и прогресс на каждый шаг главы, в экран, у которого
    /// нет ни одного вызова <c>Show</c>. Хост, решивший его показать, ставит
    /// значения сам: <see cref="SetProgress"/>, <see cref="SetBalance"/>,
    /// <see cref="SetStats"/>.</para>
    /// </summary>
    public sealed class GameHud : VisualElement
    {
        private readonly HudConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _progressIcon;
        private readonly Label _progressLabel;
        private readonly VisualElement _statsBtn;
        private readonly VisualElement _pillsRow;
        private readonly Color _pillBg;
        private readonly Color _pillText;
        private List<LvnStatDef> _stats;
        private System.Func<string, JToken> _getVar;

        private readonly Dictionary<string, LvnWalletPill> _pills = new Dictionary<string, LvnWalletPill>();
        private float _baseHeight;       // designed bar height (reference px)

        public GameHud(HudConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new HudConfig();
            _assets = assets;
            _pillBg = UiColor.Named(_cfg.pill_bg_color, LvnTokens.Veil(0.40f));
            _pillText = UiColor.Named(_cfg.pill_text_color, LvnTokens.Text);

            // Designed bar height in REFERENCE pixels (panel units track the
            // 1080×1920 reference, so this is device-independent). The bar bleeds
            // under a notch/Dynamic Island — it's just a dark strip — but the
            // content row is padded below the safe-area inset and the bar grows
            // by the same amount, keeping the content strip its designed height.
            _baseHeight = Mathf.Round((_cfg.height ?? 0.07f) * LvnPanel.ReferenceHeight);
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0;
            style.height = _baseHeight;
            // Кромку ведёт КРОМОЧНИК — и повод пересчитать тоже. Здесь стояли
            // три подписки и своё поле «что уже применено»: третья копия того
            // же механизма, причём собственный комментарий ниже уже утверждал
            // обратное.
            Lvn.UI.LvnEdges.Follow(this, insets =>
            {
                style.paddingTop = insets.x;
                style.height = _baseHeight + insets.x;
            });
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.SpaceBetween;
            style.paddingLeft = 24; style.paddingRight = 24;
            style.backgroundColor = UiColor.Named(_cfg.bg_color, LvnTokens.Veil(0.53f));
            pickingMode = PickingMode.Ignore;

            // left: progress
            var left = new VisualElement { pickingMode = PickingMode.Ignore };
            left.style.flexDirection = FlexDirection.Row;
            left.style.alignItems = Align.Center;
            left.style.display = (_cfg.show_progress ?? true) ? DisplayStyle.Flex : DisplayStyle.None;
            Add(left);

            _progressIcon = new VisualElement { pickingMode = PickingMode.Ignore };
            _progressIcon.style.width = 28; _progressIcon.style.height = 28;
            _progressIcon.style.marginRight = 8;
            LvnPicture.Fit(_progressIcon, cover: false);
            _progressIcon.style.display = string.IsNullOrEmpty(_cfg.progress_icon_url) ? DisplayStyle.None : DisplayStyle.Flex;
            left.Add(_progressIcon);

            _progressLabel = new Label("0%") { pickingMode = PickingMode.Ignore };
            _progressLabel.style.color = UiColor.Named(_cfg.progress_color, LvnTokens.Text);
            _progressLabel.style.fontSize = Lvn.UI.LvnFonts.Size(24f);
            left.Add(_progressLabel);

            // Tap to see the title's live stats (trait pairs + relationships) —
            // hidden until SetStats hands over a non-empty list (an unconfigured
            // title never grows this button).
            _statsBtn = new VisualElement { pickingMode = PickingMode.Position };
            _statsBtn.style.width = 26; _statsBtn.style.height = 26;
            _statsBtn.style.marginLeft = 16;
            LvnIcons.Paint(_statsBtn, LvnIcon.Chart, LvnTokens.Text);
            _statsBtn.style.display = DisplayStyle.None;
            _statsBtn.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                if (_stats != null) StatsPanel.Show(_stats, _getVar);
            });
            left.Add(_statsBtn);

            // right: currency pills
            _pillsRow = new VisualElement { pickingMode = PickingMode.Ignore };
            _pillsRow.style.flexDirection = FlexDirection.Row;
            _pillsRow.style.alignItems = Align.Center;
            Add(_pillsRow);

            LvnPicture.Photo(_progressIcon, _cfg.progress_icon_url, _assets, cover: false);
        }

        /// <summary>Update the chapter-progress percent (current command / total).</summary>
        public void SetProgress(int currentIndex, int totalCommands)
        {
            if (_progressLabel != null) _progressLabel.text = Percent.Text(currentIndex, totalCommands);
        }

        /// <summary>Arm (or disarm) the stats button for the chapter now playing.
        /// <paramref name="getVar"/> resolves a dotted var path against the LIVE
        /// player — the panel always reads fresh values at the moment it's
        /// opened, never a stale snapshot from when the chapter started. Null/empty
        /// stats hides the button and closes an already-open panel (the chapter
        /// that owned it just ended).</summary>
        public void SetStats(List<LvnStatDef> stats, System.Func<string, JToken> getVar)
        {
            _stats = (stats != null && stats.Count > 0) ? stats : null;
            _getVar = getVar;
            if (_statsBtn != null) _statsBtn.style.display = _stats != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (_stats == null) StatsPanel.Hide();
        }

        public void SetBalances(IDictionary<string, long> balances)
        {
            if (balances == null) return;
            foreach (var kv in balances) SetBalance(kv.Key, kv.Value);
        }

        /// <summary>Set (creating if needed) a currency pill's amount. <paramref
        /// name="iconUrl"/> overrides the default icon for this currency.</summary>
        public void SetBalance(string currency, long amount, string iconUrl = null)
        {
            if (string.IsNullOrEmpty(currency) || _pillsRow == null) return;
            if (!_pills.TryGetValue(currency, out var p))
            {
                p = SpawnPill(iconUrl ?? _cfg.default_currency_icon_url, currency);
                _pills[currency] = p;
            }
            // Число и отсчёт плашка знает сама — она читает кошелёк напрямую.
            p.Refresh();
        }

        // Плашка кошелька — общий компонент оболочки (LvnWalletPill): здесь
        // только метрика игрового HUD. Своя копия жила тут вместе с отсчётом
        // восполнения, форматом времени и троттлингом дозапроса баланса — а
        // всё это свойства кошелька, а не полоски над сценой.
        private LvnWalletPill SpawnPill(string iconUrl, string currency = null)
        {
            var pill = new LvnWalletPill(currency, new LvnWalletPill.Look
            {
                MarginLeft = 10,
                Radius = 14f,
                IconSize = 22f,
                FontSize = 22f,
                Background = _pillBg,
                TextColor = _pillText,
                IconUrl = iconUrl,
                IconTint = _pillText,
                ShowTimer = true,
                TimerReadyText = LvnWords.Pick("hud.regen_ready", _cfg.regen_ready_text, "…"),
            }, _assets);
            _pillsRow.Add(pill);
            return pill;
        }
    }
}
