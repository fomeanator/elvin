using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Sprites;
using UnityEngine.UI;

namespace Lvn.UI.World
{
    /// <summary>
    /// Draws the current Image stack as one UI graphic while an actor is entering
    /// or leaving.  The live layers stay untouched and are restored at full opacity.
    ///
    /// A CanvasGroup does not isolate its children: body and clothes are blended
    /// independently, so half-transparent clothes reveal the body below.  This
    /// proxy composites the authored layers inside one fragment and only then lets
    /// the ordinary CanvasGroup alpha fade that one result. Ordinary entrances and
    /// exits stay plain; a wardrobe rebuild may briefly animate one shader progress
    /// value on this proxy to reveal the fully assembled new look underneath.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LvnActorComposite : MonoBehaviour
    {
        internal const int MaxLayers = 8;

        private RectTransform _proxyTransform;
        private RawImage _proxy;
        private Material _material;
        private RectTransform _rig;
        private bool _active;
        private bool _warnedTooMany;
        private int _crossfadeGeneration;

        private static readonly int LayerCountId = Shader.PropertyToID("_LayerCount");
        private static readonly int WardrobeModeId = Shader.PropertyToID("_WardrobeMode");
        private static readonly int WardrobeProgressId = Shader.PropertyToID("_WardrobeProgress");
        private static readonly int WardrobeFromTopId = Shader.PropertyToID("_WardrobeFromTop");
        private static readonly int[] TextureIds = BuildIds("_Layer");
        private static readonly int[] MapAIds = BuildIds("_MapA");
        private static readonly int[] MapBIds = BuildIds("_MapB");
        private static readonly int[] UvIds = BuildIds("_Uv");
        private static readonly int[] TintIds = BuildIds("_Tint");

        private static int[] BuildIds(string prefix)
        {
            var ids = new int[MaxLayers];
            for (var i = 0; i < ids.Length; i++) ids[i] = Shader.PropertyToID(prefix + i);
            return ids;
        }

        /// <summary>Replace a multi-Image rig with one already-composited proxy.</summary>
        internal bool Begin(RectTransform transition, RectTransform rig, bool includeSingleLayer = false)
        {
            End();
            if (transition == null || rig == null) return false;

            var layers = new List<Image>(MaxLayers);
            foreach (var image in rig.GetComponentsInChildren<Image>(true))
            {
                if (image == null || image.sprite == null || !image.enabled || !image.gameObject.activeSelf) continue;
                if (image.transform.parent != rig) continue; // authored layers are direct rig children
                if (image.name.StartsWith("__lvn-composite-")) continue; // explicit SFX helper, not authored art
                layers.Add(image);
            }

            // One Image already fades as one composite.  Avoid any special path.
            if (layers.Count == 0 || (layers.Count == 1 && !includeSingleLayer)) return false;
            if (layers.Count > MaxLayers)
            {
                if (!_warnedTooMany)
                {
                    _warnedTooMany = true;
                    Debug.LogWarning($"[lvn-fade] '{name}' has {layers.Count} visible layers; " +
                                     $"transition composite supports {MaxLayers}. Using live layers.");
                }
                return false;
            }

            if (!EnsureProxy(transition)) return false;
            _rig = rig;

            var rigGroup = rig.GetComponent<CanvasGroup>();
            var firstColor = layers[0].color;
            _proxy.color = new Color(firstColor.r, firstColor.g, firstColor.b,
                rigGroup != null ? rigGroup.alpha : 1f);

            var written = 0;
            for (var i = 0; i < layers.Count; i++)
                if (WriteLayer(written, layers[i])) written++;

            if (written == 0 || (written == 1 && !includeSingleLayer))
            {
                ClearMaterial();
                return false;
            }

            _material.SetFloat(LayerCountId, written);
            _material.SetFloat(WardrobeModeId, 0f);
            _material.SetFloat(WardrobeProgressId, 0f);
            _material.SetFloat(WardrobeFromTopId, 0f);
            for (var i = written; i < MaxLayers; i++)
            {
                _material.SetTexture(TextureIds[i], Texture2D.blackTexture);
                _material.SetVector(TintIds[i], Color.clear);
            }
            _proxy.SetMaterialDirty();
            _proxy.gameObject.SetActive(true);
            rig.gameObject.SetActive(false);
            _active = true;
            // СТОРОЖ: прокси — ВРЕМЕННАЯ поверхность (самый долгий переход
            // ~0.3с). Гонка перебивающих применений могла оставить его жить
            // вечно с уже уничтоженными текстурами — гигантский белый
            // прямоугольник вместо актёра (живые скрины 27.08). Через 3с
            // прокси обязан отдать сцену живому ригу, чем бы ни кончился
            // его кроссфейд.
            // Свой счётчик: _crossfadeGeneration поднимает каждый штатный фейд,
            // и сторож на нём стал бы холостым; тут важен последний Begin.
            int watch = ++_watchdogGen;
            Lvn.LvnAsync.Fire(WatchdogAsync(watch), "CompositeWatchdog");
            return true;
        }

        private int _watchdogGen;

        private async System.Threading.Tasks.Task WatchdogAsync(int gen)
        {
            await System.Threading.Tasks.Task.Delay(3000);
            // Новый Begin завёл своего сторожа — этот отходит.
            if (_active && gen == _watchdogGen) End();
        }

        /// <summary>Reveal a newly configured look against the previous opaque
        /// composite. Clothes remove the old proxy from above; hair keeps it below
        /// and reveals only new hair layers on top. Body and clothes never become
        /// independently translucent, so underwear cannot show through.</summary>
        internal void CrossfadeToLive(float seconds, bool wardrobeFlow = false,
                                      bool wardrobeFromTop = false)
        {
            if (!_active || _rig == null || _proxy == null)
            {
                End();
                return;
            }
            // Волосы раньше шли особой «шторкой» (старый облик подложкой,
            // новые волосы проявлялись шейдером сверху). Прерванная на середине
            // шторка оставляла волосы с прогрессом 0 — «героиня лысеет при
            // смене цвета». Смена волос теперь тот же чистый кроссфейд, что и
            // наряд: старый облик целиком растворяется над новым.

            int gen = ++_crossfadeGeneration;
            // The proxy was created after the rig and remains above it. The new
            // look is fully opaque underneath; fading the old composite reveals
            // it as one image instead of alpha-blending every clothing layer.
            _rig.gameObject.SetActive(true);
            _proxyTransform.SetAsLastSibling();
            _material.SetFloat(WardrobeModeId, wardrobeFlow ? 1f : 0f);
            _material.SetFloat(WardrobeProgressId, 0f);
            _material.SetFloat(WardrobeFromTopId, wardrobeFromTop ? 1f : 0f);
            Lvn.LvnAsync.Fire(CrossfadeToLiveAsync(gen, Mathf.Max(0f, seconds), wardrobeFlow),
                "WardrobeActorCrossfade");
        }

        private async Task CrossfadeToLiveAsync(int gen, float seconds, bool wardrobeFlow)
        {
            float startAlpha = _proxy != null ? _proxy.color.a : 1f;
            if (seconds <= 0.001f)
            {
                FinishCrossfade(gen);
                return;
            }
            float started = Time.unscaledTime;
            while (gen == _crossfadeGeneration && _active && _proxy != null)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - started) / seconds);
                float k = t * t * (3f - 2f * t);
                if (wardrobeFlow)
                    _material.SetFloat(WardrobeProgressId, k);
                else
                {
                    var c = _proxy.color;
                    c.a = Mathf.Lerp(startAlpha, 0f, k);
                    _proxy.color = c;
                }
                if (t >= 1f) break;
                await Task.Yield();
            }
            FinishCrossfade(gen);
        }

        private void FinishCrossfade(int gen)
        {
            if (gen != _crossfadeGeneration) return;
            if (_rig != null) _rig.gameObject.SetActive(true);
            if (_proxy != null) _proxy.gameObject.SetActive(false);
            if (_material != null)
            {
                _material.SetFloat(WardrobeModeId, 0f);
                _material.SetFloat(WardrobeProgressId, 0f);
                _material.SetFloat(WardrobeFromTopId, 0f);
            }
            _rig = null;
            _active = false;
        }

        /// <summary>Restore the authored live layers at a fully opaque hand-off.</summary>
        internal void End()
        {
            _crossfadeGeneration++;
            if (_rig != null) _rig.gameObject.SetActive(true);
            if (_proxy != null) _proxy.gameObject.SetActive(false);
            if (_material != null)
            {
                _material.SetFloat(WardrobeModeId, 0f);
                _material.SetFloat(WardrobeProgressId, 0f);
                _material.SetFloat(WardrobeFromTopId, 0f);
            }
            _rig = null;
            _active = false;
        }


        internal bool Active => _active;

        private bool EnsureProxy(RectTransform transition)
        {
            if (_material == null)
            {
                var shader = Resources.Load<Shader>("LvnActorComposite");
                if (shader == null || !shader.isSupported)
                {
                    Debug.LogWarning("[lvn-fade] LvnActorComposite shader is missing/unsupported; using live layers");
                    return false;
                }
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (_proxy != null)
            {
                if (_proxyTransform.parent != transition) _proxyTransform.SetParent(transition, false);
                return true;
            }

            var go = new GameObject("__lvn-actor-transition-composite",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            _proxyTransform = (RectTransform)go.transform;
            _proxyTransform.SetParent(transition, false);
            WorldPlacement.Stretch(_proxyTransform);
            _proxy = go.GetComponent<RawImage>();
            _proxy.raycastTarget = false;
            _proxy.texture = Texture2D.whiteTexture; // keeps the RawImage drawable; the material samples authored layers
            _proxy.material = _material;
            go.SetActive(false);
            return true;
        }

        private bool WriteLayer(int index, Image image)
        {
            if (index < 0 || index >= MaxLayers || image == null || image.sprite == null) return false;
            var rt = image.rectTransform;
            var draw = DrawingRect(image);
            if (draw.width <= 0.0001f || draw.height <= 0.0001f) return false;

            var p0 = ProxyUv(rt.TransformPoint(new Vector3(draw.xMin, draw.yMin)));
            var p1 = ProxyUv(rt.TransformPoint(new Vector3(draw.xMax, draw.yMin)));
            var p3 = ProxyUv(rt.TransformPoint(new Vector3(draw.xMin, draw.yMax)));
            var x = p1 - p0;
            var y = p3 - p0;
            var det = x.x * y.y - x.y * y.x;
            if (Mathf.Abs(det) < 0.000001f) return false;

            // Inverse affine map: proxy UV -> this layer's local 0..1 UV.
            var inv00 = y.y / det;
            var inv01 = -y.x / det;
            var inv10 = -x.y / det;
            var inv11 = x.x / det;
            _material.SetVector(MapAIds[index], new Vector4(inv00, inv01,
                -(inv00 * p0.x + inv01 * p0.y), 0f));
            _material.SetVector(MapBIds[index], new Vector4(inv10, inv11,
                -(inv10 * p0.x + inv11 * p0.y), 0f));

            var uv = DataUtility.GetOuterUV(image.sprite);
            _material.SetVector(UvIds[index], uv);
            _material.SetTexture(TextureIds[index], image.sprite.texture);
            // Focus tint is applied once by the proxy Graphic.  Only the authored
            // per-layer alpha belongs inside the composite.
            _material.SetVector(TintIds[index], new Vector4(1f, 1f, 1f, image.color.a));
            return true;
        }

        private Vector2 ProxyUv(Vector3 world)
        {
            var local = _proxyTransform.InverseTransformPoint(world);
            var r = _proxyTransform.rect;
            return new Vector2((local.x - r.xMin) / Mathf.Max(r.width, 0.0001f),
                               (local.y - r.yMin) / Mathf.Max(r.height, 0.0001f));
        }

        private static Rect DrawingRect(Image image)
        {
            var r = image.rectTransform.rect;
            if (!image.preserveAspect || image.sprite == null || r.width <= 0f || r.height <= 0f) return r;
            var sr = image.sprite.rect;
            if (sr.width <= 0f || sr.height <= 0f) return r;

            var spriteAspect = sr.width / sr.height;
            var rectAspect = r.width / r.height;
            if (spriteAspect > rectAspect)
            {
                var h = r.width / spriteAspect;
                r.y += (r.height - h) * 0.5f;
                r.height = h;
            }
            else
            {
                var w = r.height * spriteAspect;
                r.x += (r.width - w) * 0.5f;
                r.width = w;
            }
            return r;
        }

        private void ClearMaterial()
        {
            if (_material != null) _material.SetFloat(LayerCountId, 0f);
        }

        private void OnDisable()
        {
            // A hard scene reset can deactivate the actor without an ordinary
            // transition completion.  Never leave the live rig disabled.
            End();
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
