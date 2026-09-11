using System.Threading.Tasks;
using Lvn.UI;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЛИЦО ИГРОКА (TR-79) — открыть набор и применить выбранное.
    ///
    /// <para>Экран живёт ровно на время выбора: набор берётся из манифеста, а
    /// выбранное хранит сам дом аватарок. Применяется немедленно — шапка и
    /// профиль перечитывают адрес сразу, без «Сохранить».</para>
    /// </summary>
    public partial class NovelApp
    {
        private async Task OpenAvatarPickAsync()
        {
            var root = _shell?.Document?.rootVisualElement;
            if (root == null || _assets == null) return;
            var screen = new AvatarPickScreen(_assets);
            screen.SetContent(_manifest);
            screen.Changed = ApplyAvatar;
            root.Add(screen);
            try { await screen.ShowAsync(); }
            finally { screen.RemoveFromHierarchy(); }
        }

        /// <summary>Показать выбранное лицо там, где игрок его видит: в шапке
        /// витрины и на экране профиля.</summary>
        private void ApplyAvatar()
        {
            var url = LvnAvatars.Url(_manifest);
            if (_shell?.Profile != null)
            {
                _shell.Profile.AvatarUrl = url;
                _shell.Profile.Rebuild();
            }
            _shell?.TopBar?.SetAvatar(url, _assets, _manifest);
        }

        /// <summary>
        /// ОБЛИК ГЕРОЯ СМЕНИЛСЯ — ЛИЦО СЛЕДОМ (TR-68).
        ///
        /// <para>Портрет собирается из слоёв на лету, поэтому «пересобрать» —
        /// это просто одеть кружки заново. Пачку правок сводим в одну
        /// пересборку: подтверждая облик, гардероб надевает вещи по одной, и
        /// каждая объявляет смену.</para>
        /// </summary>
        private void SchedulePortrait()
        {
            if (LvnAvatars.Picked != LvnAvatars.SelfId) return;
            _portraitDue = true;
            if (_portraitWaiting) return;
            _portraitWaiting = true;
            LvnAsync.Fire(PortraitSoonAsync(), "HeroPortrait");
        }

        private bool _portraitDue, _portraitWaiting;

        private async Task PortraitSoonAsync()
        {
            try
            {
                while (_portraitDue)
                {
                    _portraitDue = false;
                    await Task.Delay(250);      // пачка правок успевает закончиться
                }
                ApplyAvatar();
            }
            finally { _portraitWaiting = false; }
        }
    }
}
