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
        private Texture _tile;    // repeating backdrop (the void filler)
        private float _tilePx;    // on-screen size of one tile; >0 = tiling mode

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
            _tile = null; _tilePx = 0f;
            _tex = sprite.texture;
            _image.texture = _tex;
            _image.color = Color.white;
            UpdateCover();
        }

        public void SetColor(Color color)
        {
            _tile = null; _tilePx = 0f;
            _tex = null;
            _image.texture = null;
            _image.color = color;
            _image.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        /// <summary>Use a seamless texture as a REPEATING backdrop (the filler
        /// behind letterboxed scenes instead of flat black). <paramref name="tilePx"/>
        /// is one tile's on-screen width — smaller = finer grid. Overridden the
        /// moment a real bg sprite/colour is set.</summary>
        public void SetTile(Texture tex, float tilePx)
        {
            if (tex == null) return;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;
            _tex = null;
            _tile = tex;
            _tilePx = Mathf.Max(1f, tilePx);
            _image.texture = tex;
            _image.color = Color.white;
            UpdateCover();
        }

        /// <summary>Recompute the cover-crop uv rect for the current slot size —
        /// call when the canvas resizes. Cheap and safe to call every layout.</summary>
        public void UpdateCover()
        {
            var size = _rt.rect.size;
            if (_tilePx > 0f && _tile != null)
            {
                if (size.x <= 0f || size.y <= 0f) return;
                float tileH = _tilePx * _tile.height / Mathf.Max(1, _tile.width);
                _image.uvRect = new Rect(0f, 0f, size.x / _tilePx, size.y / Mathf.Max(1f, tileH));
                return;
            }
            if (_tex == null) return;
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
