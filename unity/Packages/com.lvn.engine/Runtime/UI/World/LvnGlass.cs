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

        /// <summary>Сколько раз в секунду пересчитывается размытая подложка.
        /// Меньше кадровой частоты СОЗНАТЕЛЬНО: копия уменьшена втрое и сильно
        /// размыта, на глаз её запаздывание на десятые доли кадра не читается,
        /// а считается она недёшево — уменьшение, четыре прохода размытия и
        /// переворот. Окно диалога висит почти всю игру, так что этот расход
        /// постоянный: на 60 кадрах в секунду это было семь проходов на каждый
        /// кадр, теперь столько же на четыре.</summary>
        public const float RefreshHz = 15f;

        /// <summary>Пора ли пересчитывать подложку. Вынесено отдельной чистой
        /// функцией, потому что это правило расхода, а не деталь отрисовки:
        /// его надо видеть и проверять, а не искать в середине кадра.</summary>
        public static bool ShouldRefresh(float lastRefresh, float now, bool hasCopy)
            => !hasCopy || lastRefresh < 0f || now - lastRefresh >= 1f / RefreshHz;

        private float _lastRefresh = -1f;

        private RenderTexture _rt;
        private LvnShaderSlot _slot;   // материал эффекта: одна попытка, один ответ
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

            // Материал берёт ЯЧЕЙКА: одна попытка за жизнь, жалобу про
            // пропавший шейдер дом уже произнёс. Прежде здесь стояли две
            // памяти и обряд из четырёх строк — слово в слово в трёх эффектах.
            var _mat = _slot.Of("LvnBlur");
            if (_slot.Missing) { Graphics.Blit(src, dst); return; }

            int w = Mathf.Max(8, src.width / Downscale);
            int h = Mathf.Max(8, src.height / Downscale);
            bool sizeChanged = _rt == null || _rt.width != w || _rt.height != h;
            if (!sizeChanged && !ShouldRefresh(_lastRefresh, LvnClock.Now(), _rt != null))
            {
                Graphics.Blit(src, dst);   // подложка ещё свежая — кадр идёт насквозь
                return;
            }
            _lastRefresh = LvnClock.Now();
            EnsureTarget(w, h, src.format);

            var a = RenderTexture.GetTemporary(w, h, 0, src.format);
            var b = RenderTexture.GetTemporary(w, h, 0, src.format);
            Graphics.Blit(src, a);

            // Два раздельных прохода (Г→В) поверх уже уменьшенной копии дают
            // мягкость матового стекла без «ступенек» на контрастных краях.
            LvnBlurPass.Run(_mat, a, b, radius: 2.0f, iterations: 2);   // результат — в a

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
            _slot.Release();   // материал завёл слот — он же и уберёт
            if (Current == this) Current = null;
        }
    }
}
