using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// СТЕКЛО: размытая копия кадра, которую интерфейс может показать как
    /// обычную картинку.
    ///
    /// <para>У элемента UI Toolkit нет материала — произвольный шейдер к нему не
    /// прикрутить, и это регулярно читают как «диалог надо переносить на
    /// канвас». Не надо: у элемента есть ФОН, а фоном может быть
    /// <see cref="RenderTexture"/>. Значит любой эффект, который умеет рисовать
    /// в текстуру, доступен интерфейсу — просто не через материал, а через
    /// фон.</para>
    ///
    /// <para>Здесь этим эффектом работает размытие кадра. Компонент живёт на той
    /// же камере, что <see cref="LvnBlurEffect"/> и <see cref="LvnFxStack"/>, и
    /// добавляется ПОСЛЕ них — значит видит кадр уже с эффектами: если мир в
    /// дыму, стекло тоже мутнеет, само собой. Кадр он не меняет:
    /// <c>Blit(src, dst)</c> проходит насквозь, а размытая копия уходит вбок,
    /// в <see cref="Backdrop"/>.</para>
    ///
    /// <para>Хром UITK рисуется ПОСЛЕ камеры, поэтому в стекло попадает только
    /// мир — окно не видит в подложке само себя и не уходит в бесконечное
    /// отражение.</para>
    ///
    /// <para>Платит кадр только пока стекло кому-то нужно:
    /// <see cref="Retain"/>/<see cref="Forget"/> считают пользователей, и на
    /// нуле компонент выключает сам себя.</para>
    /// </summary>
    public sealed class LvnGlass : MonoBehaviour
    {
        /// <summary>Единственное стекло сцены — камера мира тоже одна.
        /// Интерфейсу неоткуда узнать про камеру, а спросить подложку он должен
        /// уметь из любого места.</summary>
        public static LvnGlass Current { get; private set; }

        /// <summary>Размытый кадр. Null, пока никто не попросил стекло или пока
        /// не отрисован первый кадр.</summary>
        public RenderTexture Backdrop => _rt;

        /// <summary>Во сколько раз подложка мельче экрана. Стекло размыто, детали
        /// в нём не видны — треть разрешения неотличима от полной, а стоит
        /// девятую часть.</summary>
        public const int Downscale = 3;

        private RenderTexture _rt;
        private Material _mat;
        private bool _shaderMissing;
        private int _users;

        /// <summary>Стекло на камере <paramref name="cam"/> (создаётся при первом
        /// обращении).</summary>
        public static LvnGlass Ensure(Camera cam)
        {
            if (cam == null) return null;
            var g = cam.GetComponent<LvnGlass>() ?? cam.gameObject.AddComponent<LvnGlass>();
            Current = g;
            return g;
        }

        private void OnEnable() { Current = this; }

        /// <summary>«Мне нужна подложка» — включает обновление кадра.</summary>
        public void Retain()
        {
            _users++;
            enabled = true;
        }

        /// <summary>«Больше не нужна». На нуле обновление прекращается.</summary>
        public void Forget()
        {
            if (_users > 0) _users--;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            // ПОРЯДОК ЗДЕСЬ — НЕ ВКУСОВЩИНА. Кадр в dst отдаётся ПОСЛЕДНИМ
            // действием: Unity считает целевую текстуру записанной по последней
            // операции, и если после Blit(src, dst) порисовать в свои текстуры,
            // в лог сыплется «OnRenderImage() possibly didn't write anything to
            // the destination texture». Предупреждение безобидное ровно до того
            // дня, когда за ним потеряется настоящее.
            if (_users <= 0)
            {
                ReleaseTarget();
                enabled = false;
                Graphics.Blit(src, dst);
                return;
            }

            if (_mat == null && !_shaderMissing)
            {
                var shader = Resources.Load<Shader>("LvnBlur");
                if (shader == null || !shader.isSupported) _shaderMissing = true;
                else _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_shaderMissing) { Graphics.Blit(src, dst); return; }

            int w = Mathf.Max(8, src.width / Downscale);
            int h = Mathf.Max(8, src.height / Downscale);
            EnsureTarget(w, h, src.format);

            var a = RenderTexture.GetTemporary(w, h, 0, src.format);
            var b = RenderTexture.GetTemporary(w, h, 0, src.format);
            Graphics.Blit(src, a);

            // Два раздельных прохода (Г→В) поверх уже уменьшенной копии дают
            // мягкость матового стекла без «ступенек» на контрастных краях.
            _mat.SetFloat("_Radius", 2.0f);
            for (int i = 0; i < 2; i++)
            {
                _mat.SetVector("_Dir", new Vector4(1f, 0f, 0f, 0f));
                Graphics.Blit(a, b, _mat, 0);
                _mat.SetVector("_Dir", new Vector4(0f, 1f, 0f, 0f));
                Graphics.Blit(b, a, _mat, 0);
            }

            // ПЕРЕВОРОТ ПО ВЕРТИКАЛИ — не украшение, а разница систем координат:
            // у кадра камеры начало внизу, у интерфейса — вверху. Без него
            // стекло показывает мир вверх ногами, причём ровно настолько
            // правдоподобно (размыто же), что это замечают не сразу.
            if (SystemInfo.graphicsUVStartsAtTop)
                Graphics.Blit(a, _rt);
            else
                Graphics.Blit(a, _rt, new Vector2(1f, -1f), new Vector2(0f, 1f));

            RenderTexture.ReleaseTemporary(a);
            RenderTexture.ReleaseTemporary(b);

            Graphics.Blit(src, dst); // и только теперь — кадр на экран
        }

        private void EnsureTarget(int w, int h, RenderTextureFormat fmt)
        {
            if (_rt != null && _rt.width == w && _rt.height == h) return;
            ReleaseTarget();
            _rt = new RenderTexture(w, h, 0, fmt)
            {
                name = "LvnGlassBackdrop",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            _rt.Create();
        }

        private void ReleaseTarget()
        {
            if (_rt == null) return;
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }

        private void OnDestroy()
        {
            ReleaseTarget();
            if (_mat != null) Destroy(_mat);
            if (Current == this) Current = null;
        }
    }
}
