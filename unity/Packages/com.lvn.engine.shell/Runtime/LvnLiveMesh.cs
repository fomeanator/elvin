using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>ЖИВАЯ ГЕОМЕТРИЯ — основа элементов, которые рисуют себя сами и
    /// живут во времени (сияние приза, крутилка): тик раз в кадр, пока элемент
    /// прикреплён к панели, перерисовка после каждого тика; снятие с панели
    /// останавливает тик. Наследник даёт шаг времени и рисунок.</summary>
    public abstract class LvnLiveMesh : VisualElement
    {
        private IVisualElementScheduledItem _tick;

        protected LvnLiveMesh()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<AttachToPanelEvent>(_ => Start());
            RegisterCallback<DetachFromPanelEvent>(_ => _tick?.Pause());
        }

        private void Start()
        {
            _tick?.Pause();
            _tick = schedule.Execute(() => { Tick(); MarkDirtyRepaint(); }).Every(16);
        }

        /// <summary>Шаг времени — раз в кадр.</summary>
        protected abstract void Tick();
        /// <summary>Рисунок текущего состояния.</summary>
        protected abstract void Draw(MeshGenerationContext mgc);
    }
}
