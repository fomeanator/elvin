using System;

namespace Lvn
{
    /// <summary>
    /// ШТАМП СБОРКИ — одна строка в логе, отвечающая на вопрос «какой код
    /// сейчас запущен». Редактор с file:-пакетами перекомпилирует движок
    /// только по фокусу окна, и «правим, а не видно» здесь — регулярная
    /// ловушка: полдня ушло на охоту за багом, который был давно починен,
    /// но не скомпилирован. Время записи DLL — это буквально момент
    /// последней компиляции каждой сборки.
    /// </summary>
    public static class LvnBuildStamp
    {
        /// <summary>Строка вида «[lvn-build] Core=23.08 22:31 | UI=…» для
        /// сборок, к которым принадлежат переданные типы-якоря. Хост передаёт
        /// по одному типу из каждой интересной сборки.</summary>
        public static string Line(params Type[] anchors)
        {
            var sb = new System.Text.StringBuilder("[lvn-build] движок:");
            foreach (var t in anchors)
            {
                if (t == null) continue;
                var asm = t.Assembly;
                var name = asm.GetName().Name;
                // «Lvn.Engine.UI» → «UI»; сам «Lvn.Engine» → «Core».
                var shortName = name == "Lvn.Engine" ? "Core"
                    : name.StartsWith("Lvn.Engine.", StringComparison.Ordinal)
                        ? name.Substring("Lvn.Engine.".Length)
                        : name;
                string when;
                try
                {
                    var path = asm.Location;
                    when = string.IsNullOrEmpty(path)
                        ? "?"
                        : System.IO.File.GetLastWriteTime(path).ToString("dd.MM HH:mm:ss");
                }
                catch { when = "?"; }
                sb.Append(' ').Append(shortName).Append('=').Append(when).Append(" |");
            }
            if (sb[sb.Length - 1] == '|') sb.Length -= 2;
            return sb.ToString();
        }
    }
}
