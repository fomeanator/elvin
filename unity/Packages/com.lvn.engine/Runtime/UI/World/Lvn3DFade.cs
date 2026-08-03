using UnityEngine;

namespace Lvn.UI.World
{
    /// <summary>
    /// Дальность прорисовки для посева: копии, до которых далеко, не рисуются.
    ///
    /// <para>Роща в сто деревьев дёшева, пока все они в кадре. Но сцена, по
    /// которой ходят (`bg3d walk=1`), показывает лес и вблизи, и с другого
    /// края поля — и там половина копий занимает по три пикселя, оставаясь
    /// полноценными вызовами отрисовки.</para>
    ///
    /// <para>Это не полноценные уровни детализации: упрощённых версий моделей у
    /// нас нет и заводить их ради задника незачем. Здесь ровно то, что даёт
    /// почти весь выигрыш даром — дальнее просто гаснет, а туман к этому
    /// расстоянию всё равно съедает силуэт.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Lvn3DFade : MonoBehaviour
    {
        private Lvn3DBackdrop _owner;
        private float _far = 40f;
        private Renderer[] _kids;
        private float _clock;

        public static void Attach(Transform grove, Lvn3DBackdrop owner, float far)
        {
            var f = grove.GetComponent<Lvn3DFade>() ?? grove.gameObject.AddComponent<Lvn3DFade>();
            f._owner = owner;
            f._far = Mathf.Max(2f, far);
            f._kids = grove.GetComponentsInChildren<Renderer>(true);
            f.enabled = true;
            f.Apply();
        }

        private void LateUpdate()
        {
            // Пересчитываем НЕ каждый кадр: камера в новелле движется медленно,
            // а перебор сотни копий каждый кадр съел бы выигрыш, ради которого
            // всё затевалось.
            _clock += Time.unscaledDeltaTime;
            if (_clock < 0.25f) return;
            _clock = 0f;
            Apply();
        }

        private void Apply()
        {
            if (_owner == null || _kids == null) { enabled = false; return; }
            var cam = _owner.SetCamera;
            if (cam == null) return;
            var eye = cam.transform.position;
            float far2 = _far * _far;
            foreach (var r in _kids)
            {
                if (r == null) continue;
                bool visible = (r.transform.position - eye).sqrMagnitude <= far2;
                if (r.enabled != visible) r.enabled = visible;
            }
        }
    }
}
