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

            VisualElement root;
            (_go, root) = LvnFloor.Open("LvnStatsPanel", LvnFloor.Panel);
            Lvn.UI.LvnChrome.Scrim(root, Hide);
            root.pickingMode = PickingMode.Position;

            var panel = LvnChrome.Sheet(new VisualElement());
            panel.style.top = Length.Percent(14f);
            panel.style.maxHeight = Length.Percent(72f);
            panel.style.backgroundColor = LvnTokens.PanelBg;
            LvnAir.PadX(panel, 22);
            panel.style.paddingBottom = LvnTokens.Space3;
            panel.style.paddingTop = 22;
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
            close.style.marginTop = LvnTokens.Space3;
            close.style.fontSize = LvnTokens.TextSm;
            LvnAir.PadY(close, LvnTokens.Space2);
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
