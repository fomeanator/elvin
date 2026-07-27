using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Мультиэффект кадра — op <c>fx</c>. Живёт на камере канвас-сцены рядом с
    /// <see cref="LvnBlurEffect"/> (тот же единственный крюк built-in пайплайна,
    /// OnRenderImage). Каждый эффект — «галочка»-поле одного опа; отсутствующее
    /// в команде поле НЕ трогает текущее значение (липко, как placement у
    /// актёров), <c>fx off</c> сбрасывает всё:
    ///
    ///   fx vignette=0.35 grain=0.12          // включить два эффекта
    ///   fx bloom=0.6 rays=0.5 rays_x=0.3 rays_y=0.25
    ///   fx glitch=0.8                        // добавить третий, первые живут
    ///   fx off                               // чистый кадр
    ///
    /// Поля: vignette grain chromatic scanlines pixelate glitch saturation
    /// contrast bloom rays rays_x rays_y tint (#rrggbb). Всё выключено —
    /// компонент отключает себя и кадр не платит за хук.
    /// </summary>
    public sealed class LvnFxStack : MonoBehaviour
    {
        private Material _mat;
        private bool _shaderMissing;

        private float _vignette, _grain, _chromatic, _scanlines, _pixelate,
                      _glitch, _bloom, _rays, _distort, _frost, _blink, _invert;
        private float _saturation = 1f, _contrast = 1f;
        // Цели tween'а (op-поле dur): без dur цели применяются мгновенно.
        private float _tVignette, _tGrain, _tChromatic, _tScanlines, _tPixelate,
                      _tGlitch, _tBloom, _tRays, _tDistort, _tFrost, _tBlink, _tInvert;
        private float _tSaturation = 1f, _tContrast = 1f;
        private float _speed; // 1/dur; 0 = мгновенно
        private Vector2 _rayCenter = new Vector2(0.5f, 0.3f);
        private Color _tint = Color.white;

        public static LvnFxStack Ensure(Camera cam) =>
            cam.GetComponent<LvnFxStack>() ?? cam.gameObject.AddComponent<LvnFxStack>();

        /// <summary>Применить op-команду (см. класс-комментарий).</summary>
        public void Apply(JObject cmd)
        {
            // `fx off` — литерал off попадает в поле "off" ИЛИ в первое слово;
            // компилятор кладёт голое слово как {"off": true}-подобный ключ не
            // гарантированно, поэтому признаём оба написания: off=1 и reset=1.
            float dur = cmd["dur"] != null ? (float)cmd["dur"] : 0f;
            _speed = dur > 0f ? 1f / dur : 0f;

            if (cmd["off"] != null || cmd["reset"] != null)
            {
                _tVignette = _tGrain = _tChromatic = _tScanlines = _tPixelate = _tGlitch = _tBloom = _tRays = _tDistort = _tFrost = _tBlink = _tInvert = 0f;
                _tSaturation = 1f; _tContrast = 1f; _tint = Color.white;
                if (_speed <= 0f) SnapToTargets();
                enabled = true;
                return;
            }

            _tVignette   = F(cmd, "vignette", _tVignette);
            _tGrain      = F(cmd, "grain", _tGrain);
            _tChromatic  = F(cmd, "chromatic", _tChromatic);
            _tScanlines  = F(cmd, "scanlines", _tScanlines);
            _tPixelate   = F(cmd, "pixelate", _tPixelate);
            _tGlitch     = F(cmd, "glitch", _tGlitch);
            _tBloom      = F(cmd, "bloom", _tBloom);
            _tRays       = F(cmd, "rays", _tRays);
            _tDistort    = F(cmd, "distort", _tDistort);
            _tFrost      = F(cmd, "frost", _tFrost);
            _tBlink      = F(cmd, "blink", _tBlink);
            _tInvert     = F(cmd, "invert", _tInvert);
            _tSaturation = F(cmd, "saturation", _tSaturation);
            _tContrast   = F(cmd, "contrast", _tContrast);
            _rayCenter  = new Vector2(F(cmd, "rays_x", _rayCenter.x), F(cmd, "rays_y", _rayCenter.y));
            var tint = (string)cmd["tint"];
            if (!string.IsNullOrEmpty(tint) && ColorUtility.TryParseHtmlString(tint, out var c)) _tint = c;

            if (_speed <= 0f) SnapToTargets();
            enabled = true;
        }

        private void SnapToTargets()
        {
            _vignette = _tVignette; _grain = _tGrain; _chromatic = _tChromatic;
            _scanlines = _tScanlines; _pixelate = _tPixelate; _glitch = _tGlitch;
            _bloom = _tBloom; _rays = _tRays; _distort = _tDistort;
            _frost = _tFrost; _blink = _tBlink; _invert = _tInvert;
            _saturation = _tSaturation; _contrast = _tContrast;
        }

        private void Advance()
        {
            if (_speed <= 0f) { SnapToTargets(); return; }
            float k = Time.unscaledDeltaTime * _speed;
            _vignette = Mathf.MoveTowards(_vignette, _tVignette, k);
            _grain = Mathf.MoveTowards(_grain, _tGrain, k);
            _chromatic = Mathf.MoveTowards(_chromatic, _tChromatic, k);
            _scanlines = Mathf.MoveTowards(_scanlines, _tScanlines, k);
            _pixelate = Mathf.MoveTowards(_pixelate, _tPixelate, k * 20f);
            _glitch = Mathf.MoveTowards(_glitch, _tGlitch, k);
            _bloom = Mathf.MoveTowards(_bloom, _tBloom, k);
            _rays = Mathf.MoveTowards(_rays, _tRays, k);
            _distort = Mathf.MoveTowards(_distort, _tDistort, k);
            _frost = Mathf.MoveTowards(_frost, _tFrost, k);
            _blink = Mathf.MoveTowards(_blink, _tBlink, k);
            _invert = Mathf.MoveTowards(_invert, _tInvert, k);
            _saturation = Mathf.MoveTowards(_saturation, _tSaturation, k);
            _contrast = Mathf.MoveTowards(_contrast, _tContrast, k);
        }

        private static float F(JObject cmd, string key, float cur)
            => cmd[key] != null ? (float)cmd[key] : cur;

        private bool Active =>
            _vignette > 0f || _grain > 0f || _chromatic > 0f || _scanlines > 0f ||
            _pixelate > 0f || _glitch > 0f || _bloom > 0f || _rays > 0f || _distort > 0f ||
            _frost > 0f || _blink > 0f || _invert > 0f ||
            _tVignette > 0f || _tGrain > 0f || _tChromatic > 0f || _tScanlines > 0f ||
            _tPixelate > 0f || _tGlitch > 0f || _tBloom > 0f || _tRays > 0f || _tDistort > 0f ||
            _tFrost > 0f || _tBlink > 0f || _tInvert > 0f ||
            !Mathf.Approximately(_saturation, 1f) || !Mathf.Approximately(_contrast, 1f) ||
            !Mathf.Approximately(_tSaturation, 1f) || !Mathf.Approximately(_tContrast, 1f) ||
            _tint != Color.white;

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (_mat == null && !_shaderMissing)
            {
                var shader = Resources.Load<Shader>("LvnFx");
                if (shader == null || !shader.isSupported) _shaderMissing = true;
                else _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            Advance();
            if (_shaderMissing || !Active)
            {
                if (_shaderMissing) Debug.LogWarning("[LvnFx] шейдер LvnFx не найден/не поддержан — эффекты выключены");
                Graphics.Blit(src, dst);
                if (!Active) enabled = false; // всё выключено — не платим за хук
                return;
            }

            // Блум: порог в четверть разрешения → два гаусс-прохода.
            RenderTexture bloomRt = null;
            if (_bloom > 0f)
            {
                int w = Mathf.Max(1, src.width / 4), h = Mathf.Max(1, src.height / 4);
                bloomRt = RenderTexture.GetTemporary(w, h, 0, src.format);
                var tmp = RenderTexture.GetTemporary(w, h, 0, src.format);
                Graphics.Blit(src, bloomRt, _mat, 1);
                _mat.SetVector("_Dir", new Vector4(1, 0, 0, 0));
                Graphics.Blit(bloomRt, tmp, _mat, 2);
                _mat.SetVector("_Dir", new Vector4(0, 1, 0, 0));
                Graphics.Blit(tmp, bloomRt, _mat, 2);
                RenderTexture.ReleaseTemporary(tmp);
                _mat.SetTexture("_BloomTex", bloomRt);
            }

            _mat.SetFloat("_Vignette", _vignette);
            _mat.SetFloat("_Grain", _grain);
            _mat.SetFloat("_Chromatic", _chromatic);
            _mat.SetFloat("_Scanlines", _scanlines);
            _mat.SetFloat("_Pixelate", _pixelate);
            _mat.SetFloat("_Glitch", _glitch);
            _mat.SetFloat("_Bloom", _bloom);
            _mat.SetFloat("_Rays", _rays);
            _mat.SetFloat("_Distort", _distort);
            _mat.SetFloat("_Frost", _frost);
            _mat.SetFloat("_Blink", _blink);
            _mat.SetFloat("_Invert", _invert);
            _mat.SetFloat("_Saturation", _saturation);
            _mat.SetFloat("_Contrast", _contrast);
            _mat.SetColor("_Tint", _tint);
            _mat.SetVector("_RayCenter", new Vector4(_rayCenter.x, 1f - _rayCenter.y, 0, 0)); // авторская y вниз → uv вверх
            Graphics.Blit(src, dst, _mat, 0);

            if (bloomRt != null) RenderTexture.ReleaseTemporary(bloomRt);
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }
    }
}
