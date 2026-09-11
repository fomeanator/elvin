using System.Threading.Tasks;
using Lvn.UI;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КРУТКИ (TR-47) — открыть рулетку и вернуть игрока на витрину.
    ///
    /// <para>Экран живёт на время визита: состояние он спрашивает у сервера
    /// сам, а начисление уже произошло там же — витрине остаётся обновить
    /// кошелёк, чтобы выигранное появилось в шапке сразу.</para>
    /// </summary>
    public partial class NovelApp
    {
        private async Task OpenGachaAsync()
        {
            var root = _shell?.Document?.rootVisualElement;
            if (root == null || _assets == null) return;
            var screen = new GachaScreen(_assets);
            screen.SetContent(_manifest);
            root.Add(screen);
            try { await screen.RunAsync(); }
            finally
            {
                screen.RemoveFromHierarchy();
                await Lvn.Services.LvnWallet.NudgeAsync();   // выигранное видно в шапке
            }
        }
    }
}
