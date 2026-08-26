using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.UI.World
{
    /// <summary>
    /// The full-screen background on a uGUI Canvas — the Canvas mirror of
    /// фон канвас-сцены. A stretched <see cref="RawImage"/> shows the
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

        // Снимок ПРЕЖНЕГО кадра, растворяющийся над новым: смена фона резким
        // свопом читалась как склейка («фоны меняются просто резко» — партнёр).
        private RawImage _cross;
        private CanvasGroup _crossGroup;

        public void SetSprite(Sprite sprite) => SetSprite(sprite, 0f);

        public void SetSprite(Sprite sprite, float crossfadeSeconds)
        {
            if (sprite == null) return;
            bool hadArt = _image.texture != null && _tilePx <= 0f;
            bool differs = _image.texture != sprite.texture;
            if (crossfadeSeconds > 0.01f && hadArt && differs)
                BeginCrossfade(crossfadeSeconds);
            _tile = null; _tilePx = 0f;
            _tex = sprite.texture;
            _image.texture = _tex;
            _image.color = Color.white;
            _panGen++; _panX = 0.5f; // новый фон = центр, прежний пан отменён
            UpdateCover();
        }

        // ── пан по фону ──────────────────────────────────────────────────────
        // Кадр «едет» по широкой картинке: 0 = левый край, 0.5 = центр,
        // 1 = правый. Работает на горизонтальном слаке cover-кроя: пейзажный
        // фон 16:9 на портретном экране даёт ~68% хода — сцена начинается в
        // левой части кадра и за десятки секунд доезжает до правой.
        private float _panX = 0.5f;
        private int _panGen;

        public void SetPan(float x01)
        {
            _panGen++;
            _panX = Mathf.Clamp01(x01);
            UpdateCover();
        }

        public void PanTo(float x01, float seconds)
        {
            x01 = Mathf.Clamp01(x01);
            int gen = ++_panGen;
            if (seconds <= 0.01f) { _panX = x01; UpdateCover(); return; }
            Lvn.LvnAsync.Fire(PanAsync(gen, _panX, x01, seconds), "BgPan");
        }

        private async Task PanAsync(int gen, float from, float to, float seconds)
        {
            float started = Time.unscaledTime;
            while (gen == _panGen && _image != null)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - started) / seconds);
                _panX = Mathf.Lerp(from, to, t * t * (3f - 2f * t));
                UpdateCover();
                if (t >= 1f) break;
                await Task.Yield();
            }
        }

        private void BeginCrossfade(float seconds)
        {
            if (_cross == null)
            {
                var go = new GameObject("bg-cross", typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(RawImage), typeof(CanvasGroup));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_rt.parent, false);
                // Ровно над фоном и ПОД актёрами: старый кадр — часть фона.
                rt.SetSiblingIndex(_rt.GetSiblingIndex() + 1);
                Stretch(rt);
                _cross = go.GetComponent<RawImage>();
                _cross.raycastTarget = false;
                _crossGroup = go.GetComponent<CanvasGroup>();
            }
            _cross.texture = _image.texture;
            _cross.uvRect = _image.uvRect;
            _cross.color = _image.color;
            _cross.gameObject.SetActive(true);
            _crossGroup.alpha = 1f;
            var go2 = _cross.gameObject;
            LvnFade.Play(_crossGroup, 1f, 0f, seconds,
                () => { if (go2 != null) go2.SetActive(false); });
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

        /// <summary>Show a LIVE texture as the background — the frame a 3D set is
        /// being filmed into (<see cref="Lvn3DBackdrop"/>). Unlike a sprite it is
        /// already rendered at screen size, so it fills the slot as-is: cropping
        /// it would throw away the camera's framing, which is the whole point of
        /// a 3D set. Passing null hands the background back to flat art.</summary>
        public void SetLiveTexture(Texture tex)
        {
            _tile = null; _tilePx = 0f;
            _tex = null; // skip cover-crop: the frame is already the right shape
            _image.texture = tex;
            _image.color = tex != null ? Color.white : Color.black;
            _image.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        /// <summary>ДИАГНОСТИКА «белого прямоугольника»: RawImage рисует свой
        /// color сплошняком, когда текстуры нет, а после первой же постановки
        /// фона color=white. Значит выгруженная (или так и не приехавшая)
        /// текстура превращает полотно в белое пятно во весь кадр. Возвращает
        /// true ровно в этот момент — вызывающий пишет это в лог.</summary>
        /// <summary>На полотне ЕСТЬ картинка — единственный честный признак
        /// «фон стоит». Флаг у сцены может врать: команда, применённая до
        /// рождения рендерера, ничего не рисует.</summary>
        public bool HasArt => _image != null && _image.texture != null;

        public bool IsBlankWhite =>
            _image != null && _image.texture == null && _image.color.maxColorComponent > 0.5f;

        /// <summary>Что сейчас на полотне — для лога.</summary>
        public string DebugState =>
            _image == null ? "нет" :
            $"tex={(_image.texture != null ? _image.texture.width + "x" + _image.texture.height : "НЕТ")} " +
            $"color={ColorUtility.ToHtmlStringRGBA(_image.color)} tile={(_tilePx > 0f ? "да" : "нет")}";

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
            _image.uvRect = new Rect((1f - u) * _panX, (1f - v) * 0.5f, u, v);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }
    }
}
