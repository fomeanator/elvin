using System.Collections.Generic;
using Lvn.UI;
using Spine.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.Spine
{
    /// <summary>
    /// The optional spine-unity hookup: compiled ONLY when the
    /// com.esotericsoftware.spine.spine-unity package is present (see the
    /// asmdef's version define). Wires <see cref="LvnSpineBridge"/>.
    ///
    /// A Spine actor is a SELF-CONTAINED CONTAINER: one RectTransform holding an
    /// optional background image (behind) and the skeleton (in front). The whole
    /// container fades, scales and drags as a unit, so a bg that belongs to the
    /// art rides with the skeleton and stays perfectly aligned.
    ///
    /// Fit (found by live-testing real skeletons):
    ///  • spine-unity's Layout Scale Mode fights us (FitInParent stretches onto
    ///    the slot and overwrites anchors), so we fit MANUALLY on the container.
    ///  • We scale by the skeleton's AUTHOR CANVAS (skeleton x/y/width/height in
    ///    the json — the frame the artist composed in; the bg is exported at
    ///    exactly that crop), falling back to posed bounds (Skeleton.GetBounds)
    ///    only when the export carries no canvas. Posed bounds alone are
    ///    inflated by shadows/vignettes and drift per scene — they shifted and
    ///    shrank whole compositions.
    ///  • Default "width": frame WIDTH → screen width (height follows aspect).
    ///  • CRITICAL: SkeletonGraphic.MeshScale is 1 for a frame or two after
    ///    build, then settles (~100). Fitting at 1 blows up ~100×, so the fit is
    ///    DEFERRED to LateUpdate (<see cref="LvnSpineFit"/>) and only applied
    ///    once MeshScale settles. The container starts at localScale 0 (hidden).
    /// SkeletonData/atlas/material are parsed ONCE per texture and cached — only
    /// the first show pays the parse. A short CanvasGroup fade-IN
    /// (<see cref="LvnSpineFader"/>) softens the reveal; hiding is instant.
    /// </summary>
    internal static class LvnSpineBootstrap
    {
        private struct Res { public SkeletonDataAsset Data; public SpineAtlasAsset Atlas; public Material Mat; }
        private static readonly Dictionary<string, Res> _cache = new Dictionary<string, Res>();
        private static Shader _shader;

        // Flush the parsed-skeleton cache and destroy the runtime assets it
        // owns. Wired to LvnSpineBridge.ClearCache; the stage calls it when it
        // unloads its textures — cached entries reference those textures, and
        // a stale entry after an unload renders black/pink.
        private static void ClearCache()
        {
            foreach (var res in _cache.Values)
                DestroyRes(res);
            _cache.Clear();
        }

        private static void DestroyRes(Res res)
        {
            if (res.Data != null) Object.Destroy(res.Data);
            if (res.Atlas != null) Object.Destroy(res.Atlas);
            if (res.Mat != null) Object.Destroy(res.Mat);
        }

        // Units-per-pixel the skeleton data is loaded at (see Resource). The
        // json's canvas fields (skeleton x/y/width/height) are read RAW by
        // SkeletonJson — unlike bone/attachment coords they are NOT multiplied
        // by the loader scale — so frame math must apply this by hand.
        private const float DataScale = 0.01f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Hook()
        {
            LvnSpineBridge.Create = (parent, skeletonJson, atlasText, textures, scale, bgTexture) =>
            {
                var res = Resource(skeletonJson, atlasText, textures);
                if (res.Data == null) return null;

                // The container: the self-contained spine unit (bg + skeleton).
                var container = new GameObject("spine", typeof(RectTransform), typeof(CanvasGroup));
                var crt = container.GetComponent<RectTransform>();
                crt.SetParent(parent, false);
                crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
                crt.pivot = new Vector2(0.5f, 0.5f);
                crt.anchoredPosition = Vector2.zero;
                crt.localScale = Vector3.zero;              // hidden until the deferred fit lands
                // NOT 0: alpha 0 would make the Canvas cull the children — no
                // draw call, no MeshScale settle, no shader/texture warmup. The
                // fader's warm pulse (LvnSpineFader) draws a few frames at this
                // imperceptible alpha, then hides for real.
                container.GetComponent<CanvasGroup>().alpha = LvnSpineFader.WarmAlpha;

                // bg child FIRST so it renders behind the skeleton.
                RawImage bg = null;
                if (bgTexture != null)
                {
                    var bgo = new GameObject("bg", typeof(RectTransform), typeof(RawImage));
                    var brt = bgo.GetComponent<RectTransform>();
                    brt.SetParent(crt, false);
                    brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
                    brt.pivot = new Vector2(0.5f, 0.5f);
                    bg = bgo.GetComponent<RawImage>();
                    bg.texture = bgTexture;
                    bg.raycastTarget = false;
                }

                // skeleton child (in front of bg).
                var graphic = SkeletonGraphic.NewSkeletonGraphicGameObject(res.Data, crt, res.Mat);
                // A multi-page atlas NEEDS one CanvasRenderer per page material:
                // with a single renderer the whole mesh draws with page 1's
                // texture, so every attachment packed on page 2 samples the
                // wrong image and appears as misplaced rectangles (dragon's
                // chair back floating over the character, live-observed).
                graphic.allowMultipleCanvasRenderers = textures != null && textures.Length > 1;
                graphic.Initialize(false);
                var data = res.Data.GetSkeletonData(false);
                if (data != null && data.Animations.Count > 0)
                    graphic.AnimationState.SetAnimation(0, data.Animations.Items[0].Name, true);
                graphic.Update(0f);
                var srt = graphic.rectTransform;
                graphic.layoutScaleMode = SkeletonGraphic.LayoutMode.None; // manual — FitInParent would overwrite us
                srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.5f);
                srt.pivot = new Vector2(0.5f, 0.5f);
                srt.anchoredPosition = Vector2.zero;
                srt.localScale = Vector3.one;

                container.AddComponent<LvnSpineFader>();
                var fit = container.AddComponent<LvnSpineFit>();
                fit.Setup(graphic, bg);
                fit.Request(scale, "width"); // fits in LateUpdate once MeshScale settles
                return container;
            };

            LvnSpineBridge.Play = (go, name, loop) =>
            {
                var g = go != null ? go.GetComponentInChildren<SkeletonGraphic>() : null;
                if (g != null && g.AnimationState != null) g.AnimationState.SetAnimation(0, name, loop);
            };

            // Real-time size: request a re-fit. The actual fit runs in LateUpdate.
            LvnSpineBridge.Refit = (go, scale, mode) =>
            {
                var fit = go != null ? go.GetComponent<LvnSpineFit>() : null;
                if (fit != null) fit.Request(scale, mode);
            };

            // Show/hide with a short default fade instead of a hard SetActive.
            LvnSpineBridge.SetVisible = (go, visible) =>
            {
                if (go == null) return;
                var f = go.GetComponent<LvnSpineFader>();
                if (f != null) f.Show(visible);
                else go.SetActive(visible);
            };

            LvnSpineBridge.Prepare = PrepareAsync;
            LvnSpineBridge.ClearCache = ClearCache;

            Debug.Log("[lvn] spine-unity bridge hooked");
        }

        // Fit the CONTAINER to the SCREEN by the skeleton's AUTHOR CANVAS — the
        // export frame (skeleton x/y/width/height in the json) the artist
        // composed the scene in. This is the ground truth for placement: posed
        // bounds (the old reference) are inflated by shadows/vignettes and sit
        // asymmetrically around the composition, which shifted whole scenes
        // sideways and shrank any scene whose attachments overflow the frame
        // (masquerade: posed width 3070px vs canvas 2310px). The bg is exported
        // at exactly the canvas crop (verified: bg aspect == canvas aspect
        // across every set), so sizing the bg to the canvas stitches it 1:1.
        // Posed bounds remain the fallback for exports with no canvas info.
        // Default "width" maps frame width to screen width; "height"/"cover"/
        // "contain" mirror spine-unity's LayoutScaleMode. Returns false (retry)
        // until MeshScale settles (>1).
        internal static bool TryFit(RectTransform crt, SkeletonGraphic g, RawImage bg, float scale, string mode)
        {
            if (crt == null || g == null || g.Skeleton == null) return false;
            var canvas = g.canvas;
            if (canvas == null) return false;

            float meshScale = g.MeshScale;
            if (meshScale <= 1f) return false; // MeshScale not settled — fitting now would blow up ~100×

            float bx, by, bw, bh;
            var sd = g.Skeleton.Data;
            if (sd != null && sd.Width > 1f && sd.Height > 1f)
            {
                bx = sd.X * DataScale;
                by = sd.Y * DataScale;
                bw = sd.Width * DataScale;
                bh = sd.Height * DataScale;
            }
            else
            {
                float[] buf = null;
                g.Skeleton.GetBounds(out bx, out by, out bw, out bh, ref buf);
            }
            if (bw <= 0f || bh <= 0f) return false;

            var cRT = canvas.transform as RectTransform;
            float cw = cRT.rect.width, ch = cRT.rect.height;
            if (cw <= 0f || ch <= 0f) return false;

            float rw = cw / (bw * meshScale); // width-to-width
            float rh = ch / (bh * meshScale); // height-to-height
            float ratio;
            switch (mode)
            {
                case "height":  ratio = rh; break;
                case "cover":   ratio = Mathf.Max(rw, rh); break;
                case "contain": ratio = Mathf.Min(rw, rh); break;
                default:        ratio = rw; break; // "width" — the default
            }
            crt.localScale = Vector3.one * ratio * (scale <= 0f ? 1f : scale);

            // bg overlays the frame in container-local units (the mesh is drawn
            // there at frame × meshScale), so it tracks the skeleton 1:1 — and
            // since the bg image is exported at the canvas crop, no distortion.
            if (bg != null)
            {
                var brt = bg.rectTransform;
                brt.localScale = Vector3.one;
                brt.sizeDelta = new Vector2(bw * meshScale, bh * meshScale);
                brt.anchoredPosition = new Vector2((bx + bw * 0.5f) * meshScale, (by + bh * 0.5f) * meshScale);
            }

            // Centre the frame in the canvas.
            var c = new Vector3[4];
            cRT.GetWorldCorners(c);
            var center = new Vector3((c[0].x + c[2].x) * 0.5f, (c[0].y + c[2].y) * 0.5f, crt.position.z);
            var localCenter = new Vector3((bx + bw * 0.5f) * meshScale, (by + bh * 0.5f) * meshScale, 0f);
            crt.position += center - crt.TransformPoint(localCenter);
            return true;
        }

        // Atlas pages match textures BY NAME — name each page texture after
        // its atlas page line (sans extension), in order. The first page's
        // name is also the resource cache key. Shared by Resource/PrepareAsync.
        private static string PageKey(string atlasText, Texture2D[] textures)
        {
            var pageNames = new List<string>();
            foreach (var line in atlasText.Split('\n'))
            {
                var t = line.Trim();
                if (t.EndsWith(".png") || t.EndsWith(".PNG"))
                    pageNames.Add(System.IO.Path.GetFileNameWithoutExtension(t));
            }
            for (int i = 0; i < textures.Length; i++)
                if (textures[i] != null && i < pageNames.Count) textures[i].name = pageNames[i];
            return pageNames.Count > 0 ? pageNames[0]
                : (textures[0] != null ? textures[0].name : null);
        }

        // Parse + cache SkeletonData/atlas/material once per texture (the entity
        // key) so re-shows and re-entries don't re-parse — the lag killer.
        private static Res Resource(string skeletonJson, string atlasText, Texture2D[] textures)
        {
            if (textures == null || textures.Length == 0 || string.IsNullOrEmpty(atlasText)) return default;

            string key = PageKey(atlasText, textures);
            if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out var hit) && hit.Data != null)
                return hit;

            if (_shader == null) _shader = Shader.Find("Spine/SkeletonGraphic");
            var mat = new Material(_shader);
            var atlas = SpineAtlasAsset.CreateRuntimeInstance(new TextAsset(atlasText), textures, mat, true);
            // data at DataScale units/px; SkeletonGraphic multiplies by canvas PPU.
            var data = SkeletonDataAsset.CreateRuntimeInstance(new TextAsset(skeletonJson ?? ""), atlas, true, DataScale);
            if (data != null) data.GetSkeletonData(false); // parse NOW so later instances are instant

            var res = new Res { Data = data, Atlas = atlas, Mat = mat };
            if (!string.IsNullOrEmpty(key))
            {
                // Replacing a dead entry (its Data was destroyed by an unload):
                // free the leftovers before dropping the reference.
                if (_cache.TryGetValue(key, out var old)) DestroyRes(old);
                _cache[key] = res;
            }
            return res;
        }

        // The LvnSpineBridge.Prepare hookup: parse the (multi-MB) skeleton JSON
        // on the THREAD POOL and prime _cache, so the following Create() pays
        // only the mesh build. spine-csharp's SkeletonJson is pure C# — thread-
        // safe once the atlas (whose regions it references) is built, which
        // happens here on the main thread first. The parsed SkeletonData is
        // injected into a runtime SkeletonDataAsset via the internal
        // InitializeWithData seam (reflection); if ANY step fails we simply
        // don't cache — Create() then parses synchronously exactly as before.
        private static async System.Threading.Tasks.Task PrepareAsync(
            string skeletonJson, string atlasText, Texture2D[] textures)
        {
            if (textures == null || textures.Length == 0 || string.IsNullOrEmpty(atlasText)) return;
            string key = PageKey(atlasText, textures);
            if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out var hit) && hit.Data != null)
                return;

            var inject = typeof(SkeletonDataAsset).GetMethod("InitializeWithData",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (inject == null) return;

            if (_shader == null) _shader = Shader.Find("Spine/SkeletonGraphic");
            var mat = new Material(_shader);
            var atlasAsset = SpineAtlasAsset.CreateRuntimeInstance(new TextAsset(atlasText), textures, mat, true);
            var atlas = atlasAsset != null ? atlasAsset.GetAtlas() : null;
            if (atlas == null) return;

            string jsonText = skeletonJson ?? "";
            global::Spine.SkeletonData parsed = null;
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var loader = new global::Spine.SkeletonJson(
                        new global::Spine.AtlasAttachmentLoader(atlas)) { Scale = DataScale };
                    parsed = loader.ReadSkeletonData(new System.IO.StringReader(jsonText));
                }
                catch { parsed = null; }
            });
            if (parsed == null) return;

            // A concurrent sync build may have won the race while we parsed —
            // keep the winner, drop our duplicates.
            if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out hit) && hit.Data != null)
            {
                Object.Destroy(atlasAsset);
                Object.Destroy(mat);
                return;
            }

            var data = SkeletonDataAsset.CreateRuntimeInstance(
                new TextAsset(jsonText), atlasAsset, false, DataScale);
            try { inject.Invoke(data, new object[] { parsed }); }
            catch
            {
                Object.Destroy(data);
                Object.Destroy(atlasAsset);
                Object.Destroy(mat);
                return;
            }
            if (!string.IsNullOrEmpty(key))
            {
                if (_cache.TryGetValue(key, out var old)) DestroyRes(old);
                _cache[key] = new Res { Data = data, Atlas = atlasAsset, Mat = mat };
            }
        }
    }
}
