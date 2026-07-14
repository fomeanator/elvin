using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The very first paint. <see cref="NovelApp"/> raises this on its FIRST
    /// frame — before the connectivity probe, the manifest fetch and the shell
    /// build — so a device never sits on a raw black screen while the network
    /// round-trips run (2-5s on mobile). It is deliberately dumb: no manifest,
    /// no theme, no assets — just a dark plate with a percent readout that the
    /// boot milestones advance. The shell's real BootScreen replaces it.
    /// </summary>
    internal static class BootVeil
    {
        private static GameObject _go;
        private static Label _pct;
        private static Label _status;

        public static void Show()
        {
            if (_go != null) return;
            _go = new GameObject("LvnBootVeil");
            var doc = _go.AddComponent<UIDocument>();
            doc.panelSettings = LvnPanel.Shared;
            doc.sortingOrder = 100; // above everything until the shell paints

            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;
            root.style.backgroundColor = new Color(0.063f, 0.063f, 0.082f); // #101015
            root.style.alignItems = Align.Center;
            root.style.justifyContent = Justify.Center;

            _pct = new Label("0%");
            _pct.style.fontSize = 30;
            _pct.style.color = new Color(0.81f, 0.78f, 0.74f); // #cfc8bd
            _pct.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(_pct);

            _status = new Label("");
            _status.style.fontSize = 14;
            _status.style.marginTop = 6;
            _status.style.color = new Color(0.60f, 0.58f, 0.54f); // #9a948a
            root.Add(_status);
        }

        /// <summary>Advance the milestone readout ("30%", "подключение…").</summary>
        public static void Progress(int percent, string status = null)
        {
            if (_pct != null) _pct.text = percent + "%";
            if (_status != null && status != null) _status.text = status;
        }

        public static void Hide()
        {
            if (_go != null) Object.Destroy(_go);
            _go = null; _pct = null; _status = null;
        }
    }
}
