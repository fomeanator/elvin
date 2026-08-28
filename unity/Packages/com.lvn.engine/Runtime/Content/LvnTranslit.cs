using System.Text;

namespace Lvn.Content
{
    /// <summary>
    /// КИРИЛЛИЦА ЛАТИНИЦЕЙ — когда перевода нет, а язык уже английский.
    ///
    /// <para>Перевести можно то, что кто-то перевёл. Имена приходят из данных и
    /// от самого игрока: наряды каталога, персонажи, имя, которое он ввёл в
    /// прологе. Автор не переведёт их все, а игрок своё имя — тем более. Пока
    /// они оставались кириллицей, английский интерфейс выглядел наполовину
    /// сделанным: «Victoria met Роман» читается как ошибка, а не как выбор.</para>
    ///
    /// <para>Транслитерация — не перевод и не претендует им быть. Это способ
    /// прочитать чужое имя вслух: «Виктория» → «Viktoriya». Хуже перевода,
    /// сильно лучше квадратов и лучше кириллицы посреди английской фразы.</para>
    ///
    /// <para>Таблица — практическая (ГОСТ-подобная, как в загранпаспортах): её
    /// узнаёт носитель обоих языков. «Щ» → «shch», «ю» → «yu», мягкий знак
    /// пропадает — он не звучит, и апостроф вместо него только мешает
    /// читать.</para>
    /// </summary>
    public static class LvnTranslit
    {
        /// <summary>Есть ли в строке кириллица — то есть требуется ли ей
        /// транслитерация вообще.</summary>
        public static bool HasCyrillic(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s)
                if (c >= 'Ѐ' && c <= 'ӿ') return true;
            return false;
        }

        /// <summary>Латиницей. Не кириллические символы (цифры, знаки, латиница)
        /// остаются как есть: строка может быть смешанной.</summary>
        public static string ToLatin(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                var repl = Map(char.ToLowerInvariant(c));
                if (repl == null) { sb.Append(c); continue; }
                if (repl.Length == 0) continue;                 // ь, ъ — не звучат
                if (char.IsUpper(c))
                {
                    // «Щука» → «Shchuka», а не «SHchuka»: заглавной становится
                    // только первая буква замены.
                    sb.Append(char.ToUpperInvariant(repl[0]));
                    if (repl.Length > 1) sb.Append(repl, 1, repl.Length - 1);
                }
                else sb.Append(repl);
            }
            return sb.ToString();
        }

        private static string Map(char c)
        {
            switch (c)
            {
                case 'а': return "a";  case 'б': return "b";  case 'в': return "v";
                case 'г': return "g";  case 'д': return "d";  case 'е': return "e";
                case 'ё': return "e";  case 'ж': return "zh"; case 'з': return "z";
                case 'и': return "i";  case 'й': return "y";  case 'к': return "k";
                case 'л': return "l";  case 'м': return "m";  case 'н': return "n";
                case 'о': return "o";  case 'п': return "p";  case 'р': return "r";
                case 'с': return "s";  case 'т': return "t";  case 'у': return "u";
                case 'ф': return "f";  case 'х': return "kh"; case 'ц': return "ts";
                case 'ч': return "ch"; case 'ш': return "sh"; case 'щ': return "shch";
                case 'ъ': return "";   case 'ы': return "y";  case 'ь': return "";
                case 'э': return "e";  case 'ю': return "yu"; case 'я': return "ya";
                // Украинские и белорусские буквы: контент бывает не только русским.
                case 'і': return "i";  case 'ї': return "yi"; case 'є': return "ye";
                case 'ґ': return "g";  case 'ў': return "u";
                default: return null;  // не кириллица — оставляем символ как есть
            }
        }
    }
}
