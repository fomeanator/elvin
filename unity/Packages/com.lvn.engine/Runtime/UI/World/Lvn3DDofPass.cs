using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Проход глубины резкости, живущий НА КАМЕРЕ набора.
    ///
    /// <para>Первая версия делала Blit вручную сразу после <c>Render()</c> — и
    /// работала ровно в половине случаев. В живом режиме камера включена и
    /// снимает сама, каждый кадр, а ручной проход при этом не вызывается
    /// вообще: расфокус был виден на неподвижном кадре и пропадал, стоило сцене
    /// ожить. Здесь этой развилки нет — <c>OnRenderImage</c> вызывается для
    /// любого рендера камеры, кто бы его ни инициировал.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [ImageEffectAllowedInSceneView]
    public sealed class Lvn3DDofPass : MonoBehaviour
    {
        private Material _mat;
        private float _focus = 6f, _range = 4f, _power;

        public static Lvn3DDofPass Ensure(Camera cam)
        {
            if (cam == null) return null;
            return cam.GetComponent<Lvn3DDofPass>() ?? cam.gameObject.AddComponent<Lvn3DDofPass>();
        }

        public void Set(float focus, float range, float power)
        {
            _focus = focus;
            _range = range;
            _power = power;
            // Глубина нужна только когда расфокус включён: генерировать карту
            // глубины «на всякий случай» — платить за неё каждый кадр впустую.
            var cam = GetComponent<Camera>();
            if (cam != null)
                cam.depthTextureMode = power > 0f ? DepthTextureMode.Depth : DepthTextureMode.None;
            enabled = power > 0f;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (_power <= 0.001f) { Graphics.Blit(src, dst); return; }
            if (_mat == null)
            {
                var sh = Resources.Load<Shader>("LvnDof");
                if (sh == null) { Graphics.Blit(src, dst); return; }
                _mat = new Material(sh) { name = "lvn-dof" };
            }
            _mat.SetFloat("_Focus", _focus);
            _mat.SetFloat("_Range", _range);
            _mat.SetFloat("_Strength", _power);
            Graphics.Blit(src, dst, _mat);
        }
    }
}
