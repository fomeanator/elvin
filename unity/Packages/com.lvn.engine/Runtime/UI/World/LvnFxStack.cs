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
    ///   fx space=0.9 space_x=0.5 space_y=0.42 space_radius=0.28
    ///   fx glitch=0.8                        // добавить третий, первые живут
    ///   fx off                               // чистый кадр
    ///
    /// Поля: базовый грейдинг/оптика; атмосфера (fog/rain/snow/embers);
    /// боевые статусы (blood/poison/shockwave/speedlines); стилизация
    /// (dream/sepia/posterize/letterbox/sketch/halftone); вода и воздух
    /// (heat/ripple/dust); чернильный переход (ink). Всё выключено — компонент отключает
    /// себя и кадр не платит за хук.
    /// </summary>
    public sealed class LvnFxStack : MonoBehaviour
    {
        private Material _mat;
        private bool _shaderMissing;

        private float _vignette, _grain, _chromatic, _scanlines, _pixelate,
                      _glitch, _bloom, _rays, _distort, _frost, _blink, _invert,
                      _fog, _rain, _snow, _embers, _blood, _poison, _shockwave,
                      _speedlines, _dream, _sepia, _posterize, _letterbox, _space,
                      _sketch, _halftone, _heat, _ripple, _dust, _ink;
        private float _saturation = 1f, _contrast = 1f;
        // Цели tween'а (op-поле dur): без dur цели применяются мгновенно.
        private float _tVignette, _tGrain, _tChromatic, _tScanlines, _tPixelate,
                      _tGlitch, _tBloom, _tRays, _tDistort, _tFrost, _tBlink, _tInvert,
                      _tFog, _tRain, _tSnow, _tEmbers, _tBlood, _tPoison, _tShockwave,
                      _tSpeedlines, _tDream, _tSepia, _tPosterize, _tLetterbox, _tSpace,
                      // стилизация и атмосфера второй волны
                      _tSketch, _tHalftone, _tHeat, _tRipple, _tDust, _tInk;
        private float _tSaturation = 1f, _tContrast = 1f;
        private float _speed; // 1/dur; 0 = мгновенно
        private Vector2 _rayCenter = new Vector2(0.5f, 0.3f);
        private Vector2 _fxCenter = new Vector2(0.5f, 0.5f);
        private Vector2 _spaceCenter = new Vector2(0.5f, 0.45f);
        private float _spaceRadius = 0.28f;
        private Color _tint = Color.white;
        private Color _fogColor = new Color(0.68f, 0.76f, 0.82f, 1f);
        private Color _emberColor = new Color(1f, 0.28f, 0.035f, 1f);
        private Color _bloodColor = new Color(0.42f, 0.005f, 0.01f, 1f);
        // Чернила по умолчанию не чёрные, а очень тёмные сине-серые: чистый
        // чёрный на мобильном OLED читается как выключенный экран.
        private Color _inkColor = new Color(0.043f, 0.035f, 0.055f, 1f);
        private Color _poisonColor = new Color(0.18f, 0.55f, 0.08f, 1f);
        private Color _spaceColor = new Color(0.48f, 0.18f, 1f, 1f);

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
                _tVignette = _tGrain = _tChromatic = _tScanlines = _tPixelate =
                    _tGlitch = _tBloom = _tRays = _tDistort = _tFrost = _tBlink =
                    _tInvert = _tFog = _tRain = _tSnow = _tEmbers = _tBlood =
                    _tPoison = _tShockwave = _tSpeedlines = _tDream = _tSepia =
                    _tPosterize = _tLetterbox = _tSpace =
                    _tSketch = _tHalftone = _tHeat = _tRipple = _tDust = _tInk = 0f;
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
            _tFog        = F(cmd, "fog", _tFog);
            _tRain       = F(cmd, "rain", _tRain);
            _tSnow       = F(cmd, "snow", _tSnow);
            _tEmbers     = F(cmd, "embers", _tEmbers);
            _tBlood      = F(cmd, "blood", _tBlood);
            _tPoison     = F(cmd, "poison", _tPoison);
            _tShockwave  = F(cmd, "shockwave", _tShockwave);
            _tSpeedlines = F(cmd, "speedlines", _tSpeedlines);
            _tDream      = F(cmd, "dream", _tDream);
            _tSepia      = F(cmd, "sepia", _tSepia);
            _tPosterize  = F(cmd, "posterize", _tPosterize);
            _tLetterbox  = F(cmd, "letterbox", _tLetterbox);
            _tSpace      = F(cmd, "space", _tSpace);
            _tSketch     = F(cmd, "sketch", _tSketch);
            _tHalftone   = F(cmd, "halftone", _tHalftone);
            _tHeat       = F(cmd, "heat", _tHeat);
            _tRipple     = F(cmd, "ripple", _tRipple);
            _tDust       = F(cmd, "dust", _tDust);
            _tInk        = F(cmd, "ink", _tInk);
            _tSaturation = F(cmd, "saturation", _tSaturation);
            _tContrast   = F(cmd, "contrast", _tContrast);
            _rayCenter  = new Vector2(F(cmd, "rays_x", _rayCenter.x), F(cmd, "rays_y", _rayCenter.y));
            _fxCenter = new Vector2(F(cmd, "shock_x", _fxCenter.x), F(cmd, "shock_y", _fxCenter.y));
            _spaceCenter = new Vector2(F(cmd, "space_x", _spaceCenter.x),
                                       F(cmd, "space_y", _spaceCenter.y));
            _spaceRadius = Mathf.Clamp(F(cmd, "space_radius", _spaceRadius), 0.05f, 0.7f);
            var tint = (string)cmd["tint"];
            if (!string.IsNullOrEmpty(tint) && ColorUtility.TryParseHtmlString(tint, out var c)) _tint = c;
            ParseColor(cmd, "fog_color", ref _fogColor);
            ParseColor(cmd, "embers_color", ref _emberColor);
            ParseColor(cmd, "blood_color", ref _bloodColor);
            ParseColor(cmd, "ink_color", ref _inkColor);
            ParseColor(cmd, "poison_color", ref _poisonColor);
            ParseColor(cmd, "space_color", ref _spaceColor);

            if (_speed <= 0f) SnapToTargets();
            enabled = true;
        }

        private void SnapToTargets()
        {
            _vignette = _tVignette; _grain = _tGrain; _chromatic = _tChromatic;
            _scanlines = _tScanlines; _pixelate = _tPixelate; _glitch = _tGlitch;
            _bloom = _tBloom; _rays = _tRays; _distort = _tDistort;
            _frost = _tFrost; _blink = _tBlink; _invert = _tInvert;
            _fog = _tFog; _rain = _tRain; _snow = _tSnow; _embers = _tEmbers;
            _blood = _tBlood; _poison = _tPoison; _shockwave = _tShockwave;
            _speedlines = _tSpeedlines; _dream = _tDream; _sepia = _tSepia;
            _posterize = _tPosterize; _letterbox = _tLetterbox;
            _space = _tSpace;
            _sketch = _tSketch; _halftone = _tHalftone; _heat = _tHeat;
            _ripple = _tRipple; _dust = _tDust; _ink = _tInk;
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
            _fog = Mathf.MoveTowards(_fog, _tFog, k);
            _rain = Mathf.MoveTowards(_rain, _tRain, k);
            _snow = Mathf.MoveTowards(_snow, _tSnow, k);
            _embers = Mathf.MoveTowards(_embers, _tEmbers, k);
            _blood = Mathf.MoveTowards(_blood, _tBlood, k);
            _poison = Mathf.MoveTowards(_poison, _tPoison, k);
            _shockwave = Mathf.MoveTowards(_shockwave, _tShockwave, k);
            _speedlines = Mathf.MoveTowards(_speedlines, _tSpeedlines, k);
            _dream = Mathf.MoveTowards(_dream, _tDream, k);
            _sepia = Mathf.MoveTowards(_sepia, _tSepia, k);
            _posterize = Mathf.MoveTowards(_posterize, _tPosterize, k);
            _letterbox = Mathf.MoveTowards(_letterbox, _tLetterbox, k);
            _space = Mathf.MoveTowards(_space, _tSpace, k);
            _sketch = Mathf.MoveTowards(_sketch, _tSketch, k);
            _halftone = Mathf.MoveTowards(_halftone, _tHalftone, k);
            _heat = Mathf.MoveTowards(_heat, _tHeat, k);
            _ripple = Mathf.MoveTowards(_ripple, _tRipple, k);
            _dust = Mathf.MoveTowards(_dust, _tDust, k);
            _ink = Mathf.MoveTowards(_ink, _tInk, k);
            _saturation = Mathf.MoveTowards(_saturation, _tSaturation, k);
            _contrast = Mathf.MoveTowards(_contrast, _tContrast, k);
        }

        private static float F(JObject cmd, string key, float cur)
            => cmd[key] != null ? (float)cmd[key] : cur;

        // Разбор цвета — из общего дома (UiColor). Своя копия в каждом слое и
        // была тем, из-за чего одно понятие расходилось по движку.
        private static void ParseColor(JObject cmd, string key, ref Color current)
            => current = UiColor.FromCmd(cmd, key, current);

        private bool Active =>
            _vignette > 0f || _grain > 0f || _chromatic > 0f || _scanlines > 0f ||
            _pixelate > 0f || _glitch > 0f || _bloom > 0f || _rays > 0f || _distort > 0f ||
            _frost > 0f || _blink > 0f || _invert > 0f || _fog > 0f || _rain > 0f ||
            _snow > 0f || _embers > 0f || _blood > 0f || _poison > 0f ||
            _shockwave > 0f || _speedlines > 0f || _dream > 0f || _sepia > 0f ||
            _posterize > 0f || _letterbox > 0f ||
            _space > 0f ||
            _sketch > 0f || _halftone > 0f || _heat > 0f ||
            _ripple > 0f || _dust > 0f || _ink > 0f ||
            _tVignette > 0f || _tGrain > 0f || _tChromatic > 0f || _tScanlines > 0f ||
            _tPixelate > 0f || _tGlitch > 0f || _tBloom > 0f || _tRays > 0f || _tDistort > 0f ||
            _tFrost > 0f || _tBlink > 0f || _tInvert > 0f || _tFog > 0f || _tRain > 0f ||
            _tSnow > 0f || _tEmbers > 0f || _tBlood > 0f || _tPoison > 0f ||
            _tShockwave > 0f || _tSpeedlines > 0f || _tDream > 0f || _tSepia > 0f ||
            _tPosterize > 0f || _tLetterbox > 0f ||
            _tSpace > 0f ||
            _tSketch > 0f || _tHalftone > 0f || _tHeat > 0f ||
            _tRipple > 0f || _tDust > 0f || _tInk > 0f ||
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
            _mat.SetFloat("_Fog", _fog);
            _mat.SetFloat("_Rain", _rain);
            _mat.SetFloat("_Snow", _snow);
            _mat.SetFloat("_Embers", _embers);
            _mat.SetFloat("_Blood", _blood);
            _mat.SetFloat("_Poison", _poison);
            _mat.SetFloat("_Shockwave", _shockwave);
            _mat.SetFloat("_Speedlines", _speedlines);
            _mat.SetFloat("_Dream", _dream);
            _mat.SetFloat("_Sepia", _sepia);
            _mat.SetFloat("_Posterize", _posterize);
            _mat.SetFloat("_Sketch", _sketch);
            _mat.SetFloat("_Halftone", _halftone);
            _mat.SetFloat("_Heat", _heat);
            _mat.SetFloat("_Ripple", _ripple);
            _mat.SetFloat("_Dust", _dust);
            _mat.SetFloat("_Ink", _ink);
            _mat.SetFloat("_Letterbox", _letterbox);
            _mat.SetFloat("_Space", _space);
            _mat.SetFloat("_SpaceRadius", _spaceRadius);
            _mat.SetFloat("_Saturation", _saturation);
            _mat.SetFloat("_Contrast", _contrast);
            _mat.SetColor("_Tint", _tint);
            _mat.SetColor("_FogColor", _fogColor);
            _mat.SetColor("_EmberColor", _emberColor);
            _mat.SetColor("_BloodColor", _bloodColor);
            _mat.SetColor("_InkColor", _inkColor);
            _mat.SetColor("_PoisonColor", _poisonColor);
            _mat.SetColor("_SpaceColor", _spaceColor);
            _mat.SetVector("_RayCenter", new Vector4(_rayCenter.x, 1f - _rayCenter.y, 0, 0)); // авторская y вниз → uv вверх
            _mat.SetVector("_FxCenter", new Vector4(_fxCenter.x, 1f - _fxCenter.y, 0, 0));
            _mat.SetVector("_SpaceCenter",
                new Vector4(_spaceCenter.x, 1f - _spaceCenter.y, 0, 0));
            Graphics.Blit(src, dst, _mat, 0);

            if (bloomRt != null) RenderTexture.ReleaseTemporary(bloomRt);
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }
    }
}
