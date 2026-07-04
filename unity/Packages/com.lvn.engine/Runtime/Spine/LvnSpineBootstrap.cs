using Lvn.UI;
using Spine.Unity;
using UnityEngine;

namespace Lvn.Spine
{
    /// <summary>
    /// The optional spine-unity hookup: compiled ONLY when the
    /// com.esotericsoftware.spine.spine-unity package is present (see the
    /// asmdef's version define). Wires <see cref="LvnSpineBridge"/> so
    /// kind:"spine" catalog entities build a SkeletonGraphic from runtime
    /// data (json + atlas + texture streamed like any other content).
    /// </summary>
    internal static class LvnSpineBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Hook()
        {
            LvnSpineBridge.Create = (parent, skeletonJson, atlasText, texture, scale) =>
            {
                // Runtime atlas pages match textures BY NAME — name ours after the
                // atlas's first page line (sans extension).
                foreach (var line in atlasText.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    texture.name = System.IO.Path.GetFileNameWithoutExtension(t);
                    break;
                }
                var shader = Shader.Find("Spine/SkeletonGraphic");
                var material = new Material(shader);
                var atlas = SpineAtlasAsset.CreateRuntimeInstance(
                    new TextAsset(atlasText), new[] { texture }, material, true);
                // spine-unity convention: data loads at 0.01 units/px and
                // SkeletonGraphic multiplies by the canvas PPU (100) — net 1:1.
                // The catalog's `scale` stays a pure fit multiplier below.
                var data = SkeletonDataAsset.CreateRuntimeInstance(
                    new TextAsset(skeletonJson), atlas, true, 0.01f);
                var graphic = SkeletonGraphic.NewSkeletonGraphicGameObject(data, parent, material);
                graphic.Initialize(false);
                var rt = graphic.rectTransform;
                // Stretch to fill the actor's slot, then let spine-unity's OWN
                // layout scaling fit the skeleton into it — the supported path
                // (Layout Scale Mode), not a hand-rolled localScale guess. It
                // re-fits every frame, so changing the slot height resizes the
                // Spine in real time for free.
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                graphic.layoutScaleMode = SkeletonGraphic.LayoutMode.None;
                graphic.MatchRectTransformWithBounds();   // referenceSize = skeleton bounds
                rt.anchorMin = Vector2.zero;               // restore stretch (Match set it to bounds)
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                graphic.layoutScaleMode = SkeletonGraphic.LayoutMode.FitInParent;
                // The catalog `scale` is a multiplier ON TOP of the auto-fit.
                if (scale > 0f && scale != 1f)
                    rt.localScale = Vector3.one * scale;
                return graphic.gameObject;
            };
            LvnSpineBridge.Play = (go, name, loop) =>
            {
                var g = go != null ? go.GetComponent<SkeletonGraphic>() : null;
                if (g != null && g.AnimationState != null) g.AnimationState.SetAnimation(0, name, loop);
            };
            // Real-time size is now automatic (layoutScaleMode), but keep the
            // catalog-scale multiplier reapplied on demand.
            LvnSpineBridge.Refit = (go, scale) =>
            {
                var g = go != null ? go.GetComponent<SkeletonGraphic>() : null;
                if (g != null && scale > 0f)
                    g.rectTransform.localScale = Vector3.one * scale;
            };
            Debug.Log("[lvn] spine-unity bridge hooked");
        }

    }
}
