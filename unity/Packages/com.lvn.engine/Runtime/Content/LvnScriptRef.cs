using System;
using System.Text;
using UnityEngine;

namespace Lvn.Content
{
    /// <summary>
    /// ЭТО ТОТ ЖЕ СКРИПТ? — один ответ на весь движок.
    ///
    /// <para>Вопрос задают пятеро: слот сохранения («сейв из этой ли главы?»),
    /// автосейв карусели, поиск главы по адресу, переход между главами и
    /// восстановление снимка. Все пятеро отвечали ПРЯМЫМ равенством строк — то
    /// есть считали, что адрес всегда записан одинаково.</para>
    ///
    /// <para>Он не всегда записан одинаково. Кириллица в адресах живёт двумя
    /// записями — буквами и процентами (<c>Глава1.lvn</c> против
    /// <c>%D0%93%D0%BB%D0%B0%D0%B2%D0%B01.lvn</c>), и «й» с «ё» вдобавок двумя
    /// юникодными формами (см. <see cref="Lvn.LvnKey"/>: там та же болезнь
    /// стоила актёру имени). Снимок хранит адрес, записанный ПРОШЛОЙ версией
    /// контента, и сравнивается с сегодняшним манифестом — то есть ровно в том
    /// месте, где записи и расходятся.</para>
    ///
    /// <para>Цена промаха: автосохранение молча объявляется чужим. Игрок теряет
    /// прохождение не потому, что оно испорчено, а потому что адрес его главы
    /// записан другими буквами.</para>
    ///
    /// <para>Ответственность узкая: сказать, указывают ли две записи на один
    /// файл. НЕ решать, можно ли восстанавливать (это дело зовущего) и НЕ
    /// чинить адреса — сведение к одному виду живёт здесь только ради
    /// сравнения.</para>
    /// </summary>
    public static class LvnScriptRef
    {
        /// <summary>Указывают ли две записи на один и тот же скрипт. Пустые
        /// считаются несравнимыми: «адреса нет» — не «адреса совпали».</summary>
        public static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;   // горячий путь
            bool same = string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);
            if (same)
                LvnLog.Trace($"[lvn-save] адрес записан иначе, но это один скрипт: «{a}» ≡ «{b}»");
            return same;
        }

        /// <summary>
        /// Запись адреса, приведённая к одному виду ДЛЯ СРАВНЕНИЯ: проценты
        /// раскрыты, юникод собран, хвостовые пробелы сняты. Не годится для
        /// загрузки — сеть по-прежнему берёт адрес таким, каким его дал
        /// манифест.
        /// </summary>
        public static string Canonical(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            // Без строки запроса — правило разбора адреса живёт у LvnUrl, и
            // спросить его надо было сразу: «?v=3» ставят для сброса кэша, файл
            // от этого другим не становится. Манифест, обновивший версию,
            // объявлял бы сейв чужим — ровно тот дефект, ради которого эта роль
            // и появилась, только с другой стороны.
            var s = LvnUrl.Bare(url).Trim();
            // Раскрываем проценты сами: UnityWebRequest.UnEscapeURL заодно
            // превращает «+» в пробел (наследие форм), а в пути «+» — это плюс.
            s = Unescape(s);
            try { s = s.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { /* битая суррогатная пара — сравним как есть */ }
            return s;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('%') < 0) return s;
            var bytes = new System.Collections.Generic.List<byte>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length
                    && TryHex(s[i + 1], out int hi) && TryHex(s[i + 2], out int lo))
                {
                    bytes.Add((byte)((hi << 4) | lo));
                    i += 2;
                    continue;
                }
                // Не-ASCII символ отдаём его собственными байтами UTF-8, чтобы
                // смешанная запись («Глава%201») сошлась с чистой.
                bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
            try { return Encoding.UTF8.GetString(bytes.ToArray()); }
            catch { return s; }
        }

        private static bool TryHex(char c, out int v)
        {
            if (c >= '0' && c <= '9') { v = c - '0'; return true; }
            if (c >= 'a' && c <= 'f') { v = c - 'a' + 10; return true; }
            if (c >= 'A' && c <= 'F') { v = c - 'A' + 10; return true; }
            v = 0;
            return false;
        }
    }
}
