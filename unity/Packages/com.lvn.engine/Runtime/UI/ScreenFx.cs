using System.Threading;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>ОКНО в дом движения: гашение, которого можно дождаться.
    ///
    /// <para>Работа переехала к <see cref="LvnMotion"/> — там живёт время
    /// движения, и там же гашение наконец спрашивает темп. Имя осталось: его
    /// знают девять экранов, и переписывать их ради переезда дороже, чем
    /// оставить дверь.</para></summary>
    public static class ScreenFx
    {
        public static Task FadeAsync(VisualElement el, float from, float to, float seconds, CancellationToken ct)
            => LvnMotion.FadeAsync(el, from, to, seconds, ct);
    }
}
