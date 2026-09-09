using System.Threading.Tasks;
using Lvn.UI;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// НАГРАДА ЗА РОЛИК — разговор вокруг рекламы.
    ///
    /// <para>Витрина только сообщает о нажатии; что показать игроку до ролика и
    /// после, решает хозяин. Экран живёт ровно на время разговора: он ничего не
    /// держит между показами, а состояние зарядов и так приходит с сервера.</para>
    /// </summary>
    public partial class NovelApp
    {
        private async Task OpenAdRewardAsync()
        {
            var placement = _manifest?.ui?.store?.ad_placement;
            var root = _shell?.Document?.rootVisualElement;
            if (string.IsNullOrEmpty(placement) || root == null || _assets == null) return;
            var screen = new AdRewardScreen(_assets);
            screen.SetContent(_manifest);
            root.Add(screen);
            try { await screen.RunAsync(placement); }
            finally { screen.RemoveFromHierarchy(); }
        }
    }
}
