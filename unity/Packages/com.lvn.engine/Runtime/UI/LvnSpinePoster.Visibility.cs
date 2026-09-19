using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    public static partial class LvnSpinePoster
    {
        // A retained UITK screen stays attached when hidden. Detach alone cannot
        // stop its offscreen camera or SkeletonGraphic.Update. Shared posters
        // use the visible consumer's container, not their intentionally hidden host.
        internal static bool IsVisible(VisualElement target)
        {
            if (target?.panel == null || target.resolvedStyle.visibility != Visibility.Visible)
                return false;
            var visible = target.worldBound;
            if (!(visible.width > 0f && visible.height > 0f)) return false;
            for (var node = target; node != null; node = node.parent)
            {
                var style = node.resolvedStyle;
                if (node.style.display == DisplayStyle.None || style.display == DisplayStyle.None
                    || style.opacity <= 0f) return false;
                // ScrollView's overflow comes from Unity's USS, while custom
                // engine clips use inline overflow. IResolvedStyle does not expose
                // overflow, so use the actual ScrollView viewport for USS clips.
                if (node is ScrollView scroll && !Intersect(ref visible, scroll.contentViewport.worldBound))
                    return false;
                if (node != target && node.style.overflow == Overflow.Hidden)
                    if (!Intersect(ref visible, node.worldBound)) return false;
            }
            return Intersect(ref visible, target.panel.visualTree.worldBound);
        }

        private static bool Intersect(ref Rect area, Rect clip)
        {
            float x0 = Mathf.Max(area.xMin, clip.xMin), y0 = Mathf.Max(area.yMin, clip.yMin);
            float x1 = Mathf.Min(area.xMax, clip.xMax), y1 = Mathf.Min(area.yMax, clip.yMax);
            if (!(x1 > x0 && y1 > y0)) return false;
            area = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        // The ticker is a sibling of the canvas: it must remain alive to notice
        // a return to the screen while the canvas and its animations are paused.
        internal sealed class Ticker : MonoBehaviour
        {
            public Camera Camera;
            public GameObject CanvasRoot;
            public VisualElement VisibilityTarget;
            /// <summary>Рисовать всегда — у живого фона сцены нет элемента-хозяина.</summary>
            public bool AlwaysVisible;
            private float _last = -1f;
            private LvnPerf.Scope _renderScope;

            private bool SyncVisibility()
            {
                using var perf = LvnPerf.Measure(LvnPerf.Part.PosterVisibility);
                bool visible = AlwaysVisible || IsVisible(VisibilityTarget);
                if (CanvasRoot != null && CanvasRoot.activeSelf != visible)
                {
                    CanvasRoot.SetActive(visible);
                    _last = -1f; // Draw immediately on return; retain RT while hidden.
                }
                return visible;
            }

            private void Update() => SyncVisibility();

            private void LateUpdate()
            {
                if (Camera == null) { Destroy(this); return; }
                // Recheck after UI callbacks: a screen can be hidden during Update.
                if (!SyncVisibility()) { Camera.enabled = false; return; }
                float now = Time.unscaledTime;
                Camera.enabled = ShouldRender(_last, now);
                if (!Camera.enabled) return;
                _last = now;
                // Let Unity submit this camera in its normal render phase,
                // after all animation/Canvas LateUpdates. Camera.Render here
                // forced a separate submission from each poster on the main
                // thread (observed ~1 s waits on Android). Keep the same 30 Hz
                // budget; scheduling does not add renders on skipped frames.
            }

            private void OnPreCull() => _renderScope = LvnPerf.Measure(LvnPerf.Part.PosterRender);
            private void OnPostRender() { _renderScope.Dispose(); _renderScope = default; }
            private void OnDisable()
            {
                if (Camera != null) Camera.enabled = false;
                _last = -1f;
            }
        }
    }
}
