using System.Collections.Generic;
using System.Text;

namespace Lvn
{
    /// <summary>
    /// ПУТЬ СЛОЯ ИЗ ШАБЛОНА И ОСЕЙ — один дом на движок.
    ///
    /// <para>Шаблон вида <c>art/{who}_{emotion}.png</c> заполняется осями
    /// персонажа; ось, которой не нашлось ни в команде, ни в умолчаниях
    /// сущности, ОТМЕНЯЕТ слой целиком — это и есть «ничего не надето».</para>
    ///
    /// <para>Правило это жило в двух дословных копиях (SpriteComposer и
    /// SpriteCatalog). Разойдись они на одну строку — и один и тот же персонаж
    /// собирался бы по-разному в сцене и в каталоге, причём молча.</para>
    /// </summary>
    public static class LayerTemplate
    {
        /// <summary>Заполнить шаблон. Возвращает null, если хоть одна ось не
        /// разрешилась — слой в этом случае не рисуется вовсе.</summary>
        public static string Fill(string template,
                                  IReadOnlyDictionary<string, string> axes,
                                  IReadOnlyDictionary<string, string> defaults)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template;

            var sb = new StringBuilder(template.Length);
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c != '{') { sb.Append(c); i++; continue; }

                int end = template.IndexOf('}', i + 1);
                // Незакрытая скобка — не повод потерять остаток пути: дописываем
                // как есть, пусть загрузчик честно скажет «файл не найден».
                if (end < 0) { sb.Append(template, i, template.Length - i); break; }

                var key = template.Substring(i + 1, end - i - 1);
                string val = null;
                axes?.TryGetValue(key, out val);
                if (string.IsNullOrEmpty(val)) defaults?.TryGetValue(key, out val);
                if (string.IsNullOrEmpty(val)) return null;   // ось не разрешилась → слоя нет
                sb.Append(val);
                i = end + 1;
            }
            return sb.ToString();
        }
    }
}
