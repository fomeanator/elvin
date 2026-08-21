using System.Text;

namespace Lvn
{
    /// <summary>
    /// СРАВНЕНИЕ ИМЁН ПО СМЫСЛУ, а не по буквам.
    ///
    /// <para>«Ноэль де Флёр», «noel_de_fleur» и «Noel De Fleur» — для автора
    /// одно и то же имя, для словаря — три разных ключа. Приведение к общему
    /// виду жило двумя копиями (сцена и трёхмерный мир); разойдись они — и
    /// персонаж, найденный на плоской сцене, не находился бы в наборе.</para>
    /// </summary>
    public static class LvnKey
    {
        /// <summary>Только буквы и цифры, в нижнем регистре. Пробелы, дефисы и
        /// подчёркивания выбрасываются: разделитель — вопрос вкуса автора, а не
        /// часть имени.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }
    }
}
