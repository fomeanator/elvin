using System.Collections.Generic;
using Lvn.Content;

namespace Lvn.UI
{
    /// <summary>
    /// КОГО ПУСКАТЬ В СТОЛБИК ГАРДЕРОБА — правило отбора персонажей.
    ///
    /// <para>Каталог на сервере ОДИН на все новеллы, а гардероб открывают
    /// внутри одной из них. Пока правило жило строкой «все, у кого есть шкаф»,
    /// игрок жал «гардероб» посреди главы и находил в столбике героиню из
    /// другой истории: розовые волосы, бельё и вкладки «Наряд / Фон» вместо
    /// «Украшения / Причёска / Платье» (живой прогон Ильи 09.09, TR-61).</para>
    ///
    /// <para>Порядок ответов: авторский список (<c>ui.wardrobe.characters</c>)
    /// — закон; нет его — герой гардероба и те, кто СТОИТ НА СЦЕНЕ, потому что
    /// чужому там взяться неоткуда; и только в каталоге одной новеллы годится
    /// прежнее «все подряд» — там чужих нет по определению.</para>
    ///
    /// <para>Одинаковых героев под разными именами столбик показывает ОДИН
    /// раз: у импортов герой заводится трижды (Mira / demo_main /
    /// Главный_герой), и все три — одна героиня. Признак «тот же» — набор
    /// сюжетных переменных шкафа: они и есть личность персонажа.</para>
    /// </summary>
    public static class LvnWardrobeRoster
    {
        /// <param name="sprites">каталог сущностей манифеста</param>
        /// <param name="authored">авторский список (<c>ui.wardrobe.characters</c>) или null</param>
        /// <param name="primary">герой гардероба — всегда первый</param>
        /// <param name="onStage">кто сейчас на сцене; null — сцены нет</param>
        /// <param name="titleCount">сколько новелл в каталоге: одна — каталог её собственный</param>
        public static List<string> Pick(IReadOnlyDictionary<string, LvnSpriteEntity> sprites,
                                        IReadOnlyList<string> authored, string primary,
                                        IReadOnlyList<string> onStage, int titleCount)
        {
            var picked = new List<string>();
            if (sprites == null) return picked;
            var signatures = new HashSet<string>();

            bool Take(string id)
            {
                if (string.IsNullOrEmpty(id) || !sprites.TryGetValue(id, out var d)
                    || d?.wardrobe == null || d.wardrobe.Count == 0) return false;
                var vars = new List<string>();
                foreach (var kv in d.wardrobe)
                    if (!string.IsNullOrEmpty(kv.Value?.storyVar)) vars.Add(kv.Value.storyVar);
                vars.Sort();
                var sig = vars.Count > 0 ? string.Join("|", vars) : "id:" + id;
                if (!signatures.Add(sig)) return false;   // тот же герой под другим именем
                picked.Add(id);
                return true;
            }

            if (authored != null && authored.Count > 0)
            {
                foreach (var id in authored) Take(id);
                return picked;
            }

            Take(primary);
            if (onStage != null) foreach (var id in onStage) Take(id);
            // Каталог одной новеллы: чужих в нём нет, и прежнее «все, у кого
            // есть шкаф» остаётся — иначе одиночная новелла потеряла бы всех,
            // кто в эту минуту не на сцене.
            if (picked.Count <= 1 && titleCount <= 1)
                foreach (var id in sprites.Keys) Take(id);
            return picked;
        }
    }
}
