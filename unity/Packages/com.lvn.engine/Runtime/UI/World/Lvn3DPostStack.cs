using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Пост-обработка кадра НАБОРА: единственное место, где кадр сцены проходит
    /// сквозь фильтры, и единственное, где задан их порядок.
    ///
    /// <para>Почему один компонент, а не несколько. В Built-in каждый
    /// <c>OnRenderImage</c> — отдельный компонент на камере, и вызываются они в
    /// порядке добавления. Порядок расфокуса и тональной кривой при этом решает
    /// не замысел, а то, кто раньше подвернулся коду; переставить их местами
    /// можно случайно, а увидеть — только на кадре. Собранные в один проход, они
    /// идут так, как задумано, и это видно в коде.</para>
    ///
    /// <para>Почему именно здесь, а не в общем пост-проходе экрана. Поверх
    /// набора рисуются 2D-персонажи — готовый рисунок художника в готовом цвете.
    /// Тональная кривая существует, чтобы сжать СВЕТ, которого в рисунке нет:
    /// пропустить через неё спрайт значит переписать его цвет без спроса.
    /// Поэтому кривая живёт здесь, на камере набора, и заканчивается ДО того,
    /// как персонаж встанет в кадр.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [ImageEffectAllowedInSceneView]
    public sealed class Lvn3DPostStack : MonoBehaviour
    {
        /// <summary>Как сжимать света.</summary>
        public enum Tone
        {
            /// <summary>Никак: кадр уходит как есть. Прежнее поведение движка, и
            /// оно остаётся умолчанием — уже написанные новеллы не должны
            /// поменять вид оттого, что движок обновился.</summary>
            Off = 0,
            /// <summary>Своя кривая: мягкое плечо с сохранением оттенка.</summary>
            Neutral = 1,
            /// <summary>Khronos PBR Neutral — отраслевой образец для сверки.</summary>
            Khronos = 2,
        }

        private Material _dofMat, _toneMat;
        private float _focus = 6f, _range = 4f, _dof;
        private Tone _tone = Tone.Off;
        private float _exposureEV, _knee = 0.65f, _white = 1.6f;
        private float _saturation = 1f, _contrast = 1f, _dither = 1f;

        public static Lvn3DPostStack Ensure(Camera cam)
        {
            if (cam == null) return null;
            return cam.GetComponent<Lvn3DPostStack>() ?? cam.gameObject.AddComponent<Lvn3DPostStack>();
        }

        /// <summary>Глубина резкости: плоскость фокуса и её глубина — в метрах.</summary>
        public void SetDof(float focus, float range, float power)
        {
            _focus = focus;
            _range = range;
            _dof = power;
            // Карта глубины нужна только расфокусу. Просить её «на всякий
            // случай» — платить лишним проходом по сцене каждый кадр.
            var cam = GetComponent<Camera>();
            if (cam != null)
                cam.depthTextureMode = power > 0f ? DepthTextureMode.Depth : DepthTextureMode.None;
            Refresh();
        }

        /// <summary>Тональная компрессия и правка цвета.</summary>
        public void SetTone(Tone tone, float exposureEV, float saturation, float contrast, float dither)
        {
            _tone = tone;
            _exposureEV = exposureEV;
            _saturation = saturation;
            _contrast = contrast;
            _dither = dither;
            Refresh();
        }

        /// <summary>Форма плеча: где начинается сжатие и куда оно стремится.</summary>
        public void SetCurve(float knee, float white)
        {
            _knee = Mathf.Clamp(knee, 0.1f, 1f);
            _white = Mathf.Max(1f, white);
            Refresh();
        }

        /// <summary>Свечение: сила ореола, порог в единицах СВЕТА (выше единицы
        /// — только по-настоящему яркое) и мягкость этого порога.</summary>
        public void SetBloom(float power, float threshold, float knee)
        {
            _bloom = Mathf.Max(0f, power);
            _bloomThreshold = Mathf.Max(0.05f, threshold);
            _bloomKnee = Mathf.Max(0f, knee);
            Refresh();
        }

        /// <summary>Нужен ли камере расширенный диапазон. Не только кривая:
        /// свечение с порогом выше белого без HDR не увидит вообще ничего —
        /// всё, что ярче единицы, к тому моменту уже срезано.</summary>
        public bool NeedsHdr => _tone != Tone.Off || _bloom > 0.001f;

        private float _bloom, _bloomThreshold = 1.2f, _bloomKnee = 0.5f;

        /// <summary>Нужен ли вообще проход. Пока всё выключено, компонент спит и
        /// кадр идёт в буфер напрямую — без лишнего копирования.</summary>
        private void Refresh()
        {
            bool need = _dof > 0.001f || _tone != Tone.Off || _bloom > 0.001f
                        || Mathf.Abs(_saturation - 1f) > 0.001f
                        || Mathf.Abs(_contrast - 1f) > 0.001f
                        || Mathf.Abs(_exposureEV) > 0.001f
                        || _dither > 0.001f;
            enabled = need;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            RenderTexture mid = null;
            var input = src;

            // 1. РАСФОКУС — по свету, а не по картинке. Размывать после сжатия
            // светов значит размывать уже погашенный блик: в жизни расфокус
            // размазывает саму энергию, отчего яркая точка становится большим
            // ярким кругом. Поэтому он идёт первым, пока яркость ещё настоящая.
            if (_dof > 0.001f && EnsureMat(ref _dofMat, "LvnDof", "lvn-dof"))
            {
                _dofMat.SetFloat("_Focus", _focus);
                _dofMat.SetFloat("_Range", _range);
                _dofMat.SetFloat("_Strength", _dof);
                // Промежуточный буфер — БЕЗ мультисэмплинга. Кадр сюда уже
                // разрешён (сглаживание своё дело сделало), а держать его
                // многосэмпловым значит заставлять следующий проход разрешать
                // его повторно — и на стыке участков это видно швом.
                var dd = src.descriptor;
                dd.msaaSamples = 1;
                dd.depthBufferBits = 0;
                mid = RenderTexture.GetTemporary(dd);
                Graphics.Blit(src, mid, _dofMat);
                input = mid;
            }

            // 2. СВЕЧЕНИЕ — тоже по свету, до сжатия. Считается в половинном
            // разрешении: ореол по определению размыт, и разглядеть в нём
            // полный размер кадра невозможно, а платить вчетверо пришлось бы.
            RenderTexture bloomA = null, bloomB = null;
            if (_bloom > 0.001f && EnsureMat(ref _toneMat, "LvnTone", "lvn-tone"))
            {
                var d = input.descriptor;
                d.width = Mathf.Max(1, d.width / 2);
                d.height = Mathf.Max(1, d.height / 2);
                d.depthBufferBits = 0;
                d.msaaSamples = 1;      // размытому ореолу сглаживание ни к чему
                bloomA = RenderTexture.GetTemporary(d);
                bloomB = RenderTexture.GetTemporary(d);

                _toneMat.SetFloat("_BloomThreshold", _bloomThreshold);
                _toneMat.SetFloat("_BloomKnee", _bloomKnee);
                _toneMat.SetFloat("_ExposureEV", _exposureEV);
                Graphics.Blit(input, bloomA, _toneMat, 1);              // отбор яркого

                _toneMat.SetVector("_BlurDir", new Vector4(1f, 0f, 0f, 0f));
                Graphics.Blit(bloomA, bloomB, _toneMat, 2);             // размытие по X
                _toneMat.SetVector("_BlurDir", new Vector4(0f, 1f, 0f, 0f));
                Graphics.Blit(bloomB, bloomA, _toneMat, 2);             // и по Y

                _toneMat.SetTexture("_BloomTex", bloomA);
            }

            // 3. ТОН И ЦВЕТ — последним: дальше кадр уже показывают.
            if (NeedsTone() && EnsureMat(ref _toneMat, "LvnTone", "lvn-tone"))
            {
                _toneMat.SetFloat("_Bloom", _bloom);
                _toneMat.SetFloat("_ExposureEV", _exposureEV);
                _toneMat.SetFloat("_Mode", (float)_tone);
                _toneMat.SetFloat("_Knee", _knee);
                _toneMat.SetFloat("_White", _white);
                _toneMat.SetFloat("_Saturation", _saturation);
                _toneMat.SetFloat("_Contrast", _contrast);
                _toneMat.SetFloat("_Dither", _dither);
                Graphics.Blit(input, dst, _toneMat, 0);
            }
            else
            {
                Graphics.Blit(input, dst);
            }

            if (mid != null) RenderTexture.ReleaseTemporary(mid);
            if (bloomA != null) RenderTexture.ReleaseTemporary(bloomA);
            if (bloomB != null) RenderTexture.ReleaseTemporary(bloomB);
        }

        private bool NeedsTone() =>
            _tone != Tone.Off || _bloom > 0.001f
            || Mathf.Abs(_exposureEV) > 0.001f
            || Mathf.Abs(_saturation - 1f) > 0.001f
            || Mathf.Abs(_contrast - 1f) > 0.001f
            || _dither > 0.001f;

        private static bool EnsureMat(ref Material mat, string shaderName, string matName)
        {
            if (mat != null) return true;
            var sh = Resources.Load<Shader>(shaderName);
            if (sh == null) return false;
            mat = new Material(sh) { name = matName };
            return true;
        }

        private void OnDestroy()
        {
            if (_dofMat != null) Destroy(_dofMat);
            if (_toneMat != null) Destroy(_toneMat);
        }
    }
}
