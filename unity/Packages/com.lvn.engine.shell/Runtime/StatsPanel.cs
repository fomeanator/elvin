using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The in-game "Твои статы" overlay, opened from <see cref="GameHud"/>'s
    /// stats button. A standalone UIDocument (like <see cref="BootVeil"/>/
    /// <see cref="ServerSelectScreen"/>) so it draws over the dialogue/choice UI
    /// regardless of the shell's own panel depth, and reads LIVE values every
    /// time it opens rather than a snapshot from chapter start.
    /// </summary>
    internal static class StatsPanel
    {
        private static GameObject _go;

        public static void Show(System.Collections.Generic.List<LvnStatDef> stats, System.Func<string, JToken> getVar)
        {
            Hide();
            if (stats == null || stats.Count == 0) return;

            _go = new GameObject("LvnStatsPanel");
            var doc = _go.AddComponent<UIDocument>();
            doc.panelSettings = LvnPanel.Shared;
            doc.sortingOrder = 50; // above the shell (30), below boot-time overlays
            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;
            Lvn.UI.LvnChrome.Scrim(root, Hide);
            root.pickingMode = PickingMode.Position;

            var panel = LvnChrome.Sheet(new VisualElement());
            panel.style.top = Length.Percent(14f);
            panel.style.maxHeight = Length.Percent(72f);
            panel.style.backgroundColor = LvnTokens.PanelBg;
            panel.style.paddingTop = 22; panel.style.paddingBottom = 18;
            panel.style.paddingLeft = 22; panel.style.paddingRight = 22;
            LvnChrome.Round(panel, LvnTokens.Radius);
            root.Add(panel);

            panel.Add(ScreenUi.SectionHeader(() => LvnWords.Of("stats.title", "Your stats")));

            var scroll = Lvn.UI.LvnScroll.Vertical(showScroller: true);
            scroll.style.flexShrink = 1;
            panel.Add(scroll);
            foreach (var s in stats)
                if (s != null)
                    scroll.Add(StatRows.Row(s, getVar));

            var close = Lvn.UI.LvnRedress.Bind(new Button(Hide), () => LvnWords.Of("common.close", "Close"));
            close.style.marginTop = 16;
            close.style.fontSize = LvnTokens.TextSm;
            close.style.paddingTop = 12; close.style.paddingBottom = 12;
            close.style.color = LvnTokens.Text;
            close.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Round(close, LvnTokens.RadiusSm);
            LvnChrome.ClearBorder(close);
            panel.Add(close);
        }

        public static void Hide()
        {
            if (_go != null) Object.Destroy(_go);
            _go = null;
        }
    }
}
