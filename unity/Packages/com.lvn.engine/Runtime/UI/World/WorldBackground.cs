using UnityEngine;
using UnityEngine.UI;

namespace Lvn.UI.World
{
    /// <summary>
    /// The full-screen background on a uGUI Canvas — the Canvas mirror of
    /// <see cref="BackgroundLayer"/>. A stretched <see cref="RawImage"/> shows the
    /// sprite's texture cropped to cover (uv rect computed from the texture vs.
    /// the slot aspect), or a solid colour when there is no art.
    /// </summary>
    public sealed class WorldBackground
    {
        private readonly RawImage _image;
        private readonly RectTransform _rt;
        private Texture _tex;

        public WorldBackground(Transform parent)
        {
            var go = new GameObject("bg", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            _rt = (RectTransform)go.transform;
            _rt.SetParent(parent, false);
            Stretch(_rt);
            _image = go.GetComponent<RawImage>();
            _image.raycastTarget = false;
            _image.color = Color.black;
            _image.texture = null;
        }

        public RectTransform Transform => _rt;

        public void SetSprite(Sprite sprite)
        {
            if (sprite == null) return;
            _tex = sprite.texture;
            _image.texture = _tex;
            _image.color = Color.white;
            UpdateCover();
        }

        public void SetColor(Color color)
        {
            _tex = null;
            _image.texture = null;
            _image.color = color;
            _image.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        /// <summary>Recompute the cover-crop uv rect for the current slot size —
        /// call when the canvas resizes. Cheap and safe to call every layout.</summary>
        public void UpdateCover()
        {
            if (_tex == null) return;
            var size = _rt.rect.size;
            if (size.x <= 0f || size.y <= 0f) { _image.uvRect = new Rect(0f, 0f, 1f, 1f); return; }
            float texAspect = (float)_tex.width / Mathf.Max(1, _tex.height);
            float slotAspect = size.x / size.y;
            float u = 1f, v = 1f;
            if (texAspect > slotAspect) u = slotAspect / texAspect; // crop sides
            else v = texAspect / slotAspect;                        // crop top/bottom
            _image.uvRect = new Rect((1f - u) * 0.5f, (1f - v) * 0.5f, u, v);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }
    }
}
