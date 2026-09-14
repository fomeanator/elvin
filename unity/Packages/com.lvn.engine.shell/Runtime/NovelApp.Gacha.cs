using System.Threading.Tasks;
using Lvn.UI;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КРУТКИ (TR-47) — открыть рулетку и вернуть игрока на витрину.
    ///
    /// <para>Экран живёт на время визита: состояние он спрашивает у сервера
    /// сам. Сервис обновляет кошелёк и инвентарь после каждого прокрута,
    /// ещё до анимации. Повторный тап по входу не открывает вторую рулетку.</para>
    /// </summary>
    public partial class NovelApp
    {
        private bool _gachaOpen;

        private async Task OpenGachaAsync()
        {
            var root = _shell?.Document?.rootVisualElement;
            if (_gachaOpen || root == null || _assets == null) return;
            _gachaOpen = true;
            var screen = new GachaScreen(_assets);
            screen.SetContent(_manifest);
            root.Add(screen);
            try { await screen.RunAsync(); }
            finally
            {
                screen.RemoveFromHierarchy();
                _gachaOpen = false;
            }
        }
    }
}
