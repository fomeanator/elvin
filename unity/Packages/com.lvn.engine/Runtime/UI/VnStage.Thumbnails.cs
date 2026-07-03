using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    public sealed partial class VnStage
    {
        // ── save thumbnails ──────────────────────────────────────────────────
        // The Ren'Py convention: the frame on screen when the menu OPENS (before
        // the scrim paints over it) is the thumbnail of any save made from it.

        private Texture2D _pendingThumb;
        private const int ThumbWidth = 320;

        /// <summary>Capture the current clean frame as the pending save
        /// thumbnail, then continue (the menu defers its scrim by one frame).
        /// Headless/batch runs skip the capture — there are no frames.</summary>
        internal void CaptureMenuThumb(Action onDone)
        {
            if (Application.isBatchMode) { onDone?.Invoke(); return; }
            StartCoroutine(CaptureThumbCo(onDone));
        }

        private System.Collections.IEnumerator CaptureThumbCo(Action onDone)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot != null)
                {
                    if (_pendingThumb != null) Destroy(_pendingThumb);
                    _pendingThumb = ScaleToWidth(shot, ThumbWidth);
                    if (!ReferenceEquals(shot, _pendingThumb)) Destroy(shot);
                }
            }
            catch (Exception e) { LvnPlayer.Log?.Invoke("thumb capture failed: " + e.Message); }
            onDone?.Invoke();
        }

        // GPU-resample to the thumbnail width (readable — it gets PNG-encoded).
        private static Texture2D ScaleToWidth(Texture2D tex, int width)
        {
            if (tex.width <= width) return tex;
            int h = Mathf.Max(1, Mathf.RoundToInt((float)tex.height * width / tex.width));
            var rt = RenderTexture.GetTemporary(width, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var small = new Texture2D(width, h, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0, 0, width, h), 0, 0);
            small.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return small;
        }

        /// <summary>Restore a persistent slot taken in the CURRENT chapter; returns
        /// false for another chapter's slot (see <see cref="LoadFromSlotAsync"/> for
        /// the cross-chapter path).</summary>
    }
}
