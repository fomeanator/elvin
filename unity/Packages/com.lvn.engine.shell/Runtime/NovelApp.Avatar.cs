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
            _shell?.TopBar?.SetAvatar(url, _assets);
        }
    }
}
