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
                rt.anchorMin = new Vector2(0.5f, 0f); // feet at the slot's bottom-centre
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = Vector2.zero;
                // The skeleton renders at its AUTHORED pixel size — fit it to the
                // actor's slot height instead, with `scale` as a multiplier on top
                // (1 = exactly the placed height).
                // Fit by what the renderer ACTUALLY draws: skeleton data height
                // times the graphic's own mesh scale — no unit guessing.
                var sd = data.GetSkeletonData(true);
                float mesh = graphic.MeshScale > 0f ? graphic.MeshScale : 1f;
                float renderedH = sd != null && sd.Height > 0f ? sd.Height * mesh : 0f;
                float slotH = parent.rect.height;
                if (slotH > 0f && renderedH > 0f)
                    rt.localScale = Vector3.one * (slotH / renderedH * (scale <= 0f ? 1f : scale));
                return graphic.gameObject;
            };
            LvnSpineBridge.Play = (go, name, loop) =>
            {
                var g = go != null ? go.GetComponent<SkeletonGraphic>() : null;
                if (g != null && g.AnimationState != null) g.AnimationState.SetAnimation(0, name, loop);
            };
            Debug.Log("[lvn] spine-unity bridge hooked");
        }
    }
}
