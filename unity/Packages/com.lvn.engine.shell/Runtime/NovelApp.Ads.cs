using System.Threading.Tasks;
using UnityEngine;
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
            // ПЕРЕЕЗД К ЩИТУ ВЕДЁТ ХОЗЯИН СЦЕНЫ (TR-64, пункт 4): экран просит
            // подъехать, камеру двигаем мы. Своим доступом к сцене экран завёл
            // бы вторую власть над камерой — ту самую, от которой героиня в
            // меню «ходила ходуном».
            screen.OnApproach = near => ApproachBillboard(near);
            root.Add(screen);
            try { await screen.RunAsync(placement); }
            finally
            {
                screen.RemoveFromHierarchy();
                ApproachBillboard(false);   // экран мог закрыться на полпути
            }
        }

        /// <summary>Камера у щита: подъехать к нему и вернуть общий план.
        /// Числа те же по смыслу, что у наезда гардероба, — лёгкий наезд и
        /// подъём точки интереса, чтобы героиня оказалась под щитом.</summary>
        private void ApproachBillboard(bool near)
        {
            if (Stage == null || InChapter) return;
            if (!near) { RestoreMenuComposition(0.45f); return; }
            float H = 1080f * Screen.height / Mathf.Max(1, Screen.width);
            Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
            { ["op"] = "camera", ["action"] = "zoom", ["factor"] = 1.12f, ["duration"] = 0.5 }, LvnSender.Menu);
            Stage.ApplyStage(new Newtonsoft.Json.Linq.JObject
            { ["op"] = "camera", ["action"] = "pan", ["y"] = -0.06f * H, ["duration"] = 0.5 }, LvnSender.Menu);
        }
    }
}
