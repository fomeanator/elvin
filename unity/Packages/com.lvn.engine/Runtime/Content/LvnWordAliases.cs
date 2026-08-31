using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>
    /// ПРЕЖНЕЕ ИМЯ СЛОВА — чтобы перевод, написанный вчера, работал сегодня.
    ///
    /// <para>Ключи росли двумя пространствами. Меню сцены спрашивало ГОЛЫЕ
    /// имена (<c>close</c>, <c>gallery</c>, <c>history</c>, <c>language</c>,
    /// <c>window_opacity</c>) — так они и попали в манифесты авторов, в раздел
    /// <c>ui.menu.labels</c>. Экраны оболочки, написанные позже, спрашивают
    /// имена с приставкой (<c>common.close</c>, <c>nav.gallery</c>,
    /// <c>settings.box_opacity</c>), потому что их стало много и без приставки
    /// они бы сталкивались.</para>
    ///
    /// <para>Обе стороны правы, а игрок платит: в живом манифесте Time Romance
    /// тридцать одна подпись переведена под голыми именами, и те же самые вещи
    /// в оболочке показывались по-английски. «Закрыть» в меню главы и Close на
    /// экране оболочки — одна кнопка, названная дважды.</para>
    ///
    /// <para>Поэтому пары названы ЗДЕСЬ, а не разосланы по местам вызова:
    /// словарь спрашивает канон, и если его нет — прежнее имя. Правило «взять
    /// последнюю часть после точки» не годится: <c>saves.auto</c> — это
    /// автосохранение, а голое <c>auto</c> — кнопка автопрокрутки, и совпадение
    /// хвостов означало бы подмену слова.</para>
    /// </summary>
    public static class LvnWordAliases
    {
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                // Общие кнопки.
                ["common.close"] = "close",
                ["common.cancel"] = "cancel",

                // Разделы, у которых в сцене есть свой пункт меню.
                ["nav.gallery"] = "gallery",
                ["game.history"] = "history",
                ["game.exit"] = "exit",
                ["saves.auto"] = "autosave",
                ["menu.settings"] = "settings",

                // Настройки: набор один, а спрашивали его двумя именами.
                ["settings.language"] = "language",
                ["settings.font"] = "font",
                ["settings.font_author"] = "font_author",
                ["settings.text_size"] = "text_size",
                ["settings.ui_size"] = "ui_size",
                ["settings.text_speed"] = "text_speed",
                ["settings.auto_advance"] = "auto_advance",
                ["settings.auto_delay"] = "auto_delay",
                ["settings.box_opacity"] = "window_opacity",
                ["settings.skip_read"] = "skip_read_only",
                ["settings.reduce_motion"] = "reduce_motion",
                ["settings.music"] = "music",
                ["settings.sounds"] = "sound",
                ["settings.ambient"] = "ambient",
                ["settings.sfx"] = "sfx",
                ["settings.voice"] = "voice",
            };

        /// <summary>Прежнее имя ключа или null, если его не было.</summary>
        public static string Legacy(string key)
            => !string.IsNullOrEmpty(key) && Map.TryGetValue(key, out var old) ? old : null;

        /// <summary>Все пары — для стражей и диагностики.</summary>
        public static IReadOnlyDictionary<string, string> All => Map;
    }
}
