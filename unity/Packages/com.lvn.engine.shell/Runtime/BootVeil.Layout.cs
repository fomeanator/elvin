using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    internal static partial class BootVeil
    {
        private static VisualElement _identity, _progress;
        private static bool _brandShown;

        // A title page, not a percentage with a name attached. The identity,
        // working area and colophon have independent anchors: revealing a
        // lengthy download must never push the title upwards.
        private static void BuildLayout()
        {
            LvnFonts.Apply(_root, Resources.Load<Font>(LvnFonts.EngineFontPath));
            var safe = new SafeAreaElement();
            _root.Add(safe);

            _identity = new VisualElement { name = "boot-identity", pickingMode = PickingMode.Ignore };
            _identity.style.position = Position.Absolute;
            _identity.style.left = Length.Percent(8);
            _identity.style.right = Length.Percent(8);
            _identity.style.top = Length.Percent(6);
            _identity.style.bottom = Length.Percent(26);
            _identity.style.alignItems = Align.Center;
            _identity.style.justifyContent = Justify.Center;
            safe.Add(_identity);

            var book = LvnIcons.Make(LvnIcon.Book, 108f, LvnDawn.Brand, stroke: 3f, glow: 0f);
            book.name = "boot-book";
            book.style.marginBottom = LvnTokens.Space5;
            _identity.Add(book);

            _brandTitle = new Label { name = "boot-title", pickingMode = PickingMode.Ignore, enableRichText = false };
            LvnFonts.Apply(_brandTitle, Resources.Load<Font>(LvnFonts.FamilyOf("literata").Path));
            _brandTitle.style.width = Length.Percent(100);
            _brandTitle.style.maxWidth = 850f;
            _brandTitle.style.whiteSpace = WhiteSpace.Normal;
            _brandTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _brandTitle.style.fontSize = LvnTokens.TextDisplay;
            _brandTitle.style.letterSpacing = 0;
            _brandTitle.style.color = LvnDawn.Ink;
            _identity.Add(_brandTitle);

            // Sizes are panel points, not device pixels. Constrain unusually
            // narrow embeddings too; don't change the shared panel's scale.
            var title = _brandTitle;
            _identity.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                if (evt.newRect.width <= 0f) return;
                title.style.fontSize = Mathf.Clamp(evt.newRect.width / 8.5f, LvnTokens.TextBase, LvnTokens.TextDisplay);
            });

            var work = new VisualElement { name = "boot-work", pickingMode = PickingMode.Ignore };
            work.style.position = Position.Absolute;
            work.style.left = Length.Percent(10);
            work.style.right = Length.Percent(10);
            work.style.bottom = Length.Percent(13);
            work.style.alignItems = Align.Center;
            safe.Add(work);

            _progress = new VisualElement { name = "boot-progress", pickingMode = PickingMode.Ignore };
            _progress.style.width = Length.Percent(100);
            _progress.style.maxWidth = 640f;
            work.Add(_progress);
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.FlexEnd;
            row.style.marginBottom = LvnTokens.Space3;
            _progress.Add(row);

            _status = new Label { name = "boot-status", pickingMode = PickingMode.Ignore, enableRichText = false };
            _status.style.fontSize = LvnTokens.TextBase;
            _status.style.color = LvnDawn.InkDim;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.flexGrow = 1;
            _status.style.flexShrink = 1;
            _status.style.minWidth = 0;
            row.Add(_status);

            _pct = new Label("0%") { name = "boot-percent", pickingMode = PickingMode.Ignore };
            _pct.style.fontSize = LvnTokens.TextBase;
            _pct.style.color = LvnDawn.Brand;
            _pct.style.minWidth = 90f;
            _pct.style.marginLeft = LvnTokens.Space3;
            _pct.style.flexShrink = 0;
            _pct.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(_pct);

            var track = new VisualElement { name = "boot-track", pickingMode = PickingMode.Ignore };
            track.style.height = 4f;
            track.style.backgroundColor = LvnDawn.Track;
            _fill = new VisualElement { name = "boot-fill", pickingMode = PickingMode.Ignore };
            _fill.style.height = Length.Percent(100);
            _fill.style.backgroundColor = LvnDawn.Brand;
            track.Add(_fill);
            _progress.Add(track);

            var signature = new VisualElement { name = "boot-signature", pickingMode = PickingMode.Ignore };
            LvnChrome.BottomStrip(signature, 0f, 36f);
            ScreenUi.Row(signature);
            signature.style.justifyContent = Justify.Center;
            var word = new Label(Lvn.LvnEngine.Name) { pickingMode = PickingMode.Ignore };
            LvnFonts.Apply(word, Resources.Load<Font>(LvnFonts.EngineDisplayPath));
            word.style.fontSize = LvnTokens.TextSm;
            word.style.letterSpacing = 4f;
            word.style.color = LvnDawn.Brand;
            signature.Add(word);
            var version = new Label("v" + Lvn.LvnEngine.Version) { pickingMode = PickingMode.Ignore };
            version.style.fontSize = LvnTokens.TextSm;
            version.style.marginLeft = LvnTokens.Space3;
            version.style.color = LvnDawn.InkDim;
            signature.Add(version);
            safe.Add(signature);
        }

        private static void ResetPresentation()
        {
            _splashAt = -1f;
            _barBack = false;
            _brandShown = false;
            _creepFrom = _creepCeil = _creepStarted = 0f;
            if (_brandTitle != null) _brandTitle.text = "";
            if (_identity != null) _identity.style.opacity = 1f;
            if (_progress != null) _progress.style.visibility = Visibility.Visible;
            if (_pct != null) _pct.text = "0%";
            if (_fill != null) _fill.style.width = Length.Percent(0);
        }
    }
}
