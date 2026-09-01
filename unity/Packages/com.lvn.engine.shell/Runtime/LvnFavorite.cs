using Lvn.Content;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КОГО ПОКАЗЫВАЕМ ИЗБРАННОЙ — один ответ на всю оболочку.
    ///
    /// <para>Игрок выбирает любимую героиню в гардеробе, и её облик встаёт на
    /// витрине, в быстром меню и в створе. Вопрос простой ровно до второго
    /// условия: выбранная могла ИСЧЕЗНУТЬ из новеллы — обновился контент,
    /// сменился титул, — и тогда её имя есть, а рисовать нечем.</para>
    ///
    /// <para>Отвечали на это ДВА места, и они уже разошлись. Быстрое меню
    /// проверяло облик и у выбранной, и у запасной из манифеста; вкладка
    /// гардероба — только у выбранной, а запасную брала как есть. Итог:
    /// новелла без облика у запасной героини даёт на одном экране пустоту, а
    /// на другом — честное «никого», и это разные экраны одного приложения.
    /// Правило взято строгое: имя без облика — не выбор.</para>
    /// </summary>
    public static class LvnFavorite
    {
        /// <summary>Избранная героиня, у которой ЕСТЬ облик; иначе запасная из
        /// манифеста, если облик есть у неё; иначе никто.</summary>
        public static string Entity(LvnManifest manifest)
        {
            var fav = LvnPrefs.MenuFavorite;
            if (HasArt(manifest, fav)) return fav;
            var def = manifest?.ui?.wardrobe?.entity;
            return HasArt(manifest, def) ? def : null;
        }

        /// <summary>Есть ли у имени облик в каталоге новеллы.</summary>
        public static bool HasArt(LvnManifest manifest, string entity) =>
            !string.IsNullOrEmpty(entity) && manifest?.sprites != null
            && manifest.sprites.ContainsKey(entity);
    }
}
