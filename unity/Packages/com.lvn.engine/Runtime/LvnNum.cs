using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// ЧТО СЧИТАЕТСЯ ЧИСЛОМ В ЯЗЫКЕ — одно место на весь движок.
    ///
    /// <para>Автор пишет размеры и координаты тремя способами: числом (0.57),
    /// числом в кавычках ("0.57") и процентом (57%). Первые два разбирались
    /// давно, третий молча терялся: строка не парсилась, поле уходило в null, и
    /// объект вставал в положение по умолчанию. В тексте новеллы при этом
    /// стояли осмысленные координаты — в одной дуэли их 67 штук, и ни одна не
    /// действовала.</para>
    ///
    /// <para>Процент — это доля: 57% == 0.57. Тот же смысл, что в дереве
    /// <c>ui</c>, где он понимался с первого дня.</para>
    /// </summary>
    public static class LvnNum
    {
        /// <summary>Число, или null — если поля нет или оно испорчено. Никогда
        /// не бросает: одно кривое поле не должно ронять главу.</summary>
        public static float? Parse(JToken t)
        {
            if (t == null) return null;
            try { return (float)t; } catch { }   // число могло прийти строкой — пробуем ниже
            try
            {
                var text = ((string)t)?.Trim();
                if (string.IsNullOrEmpty(text)) return null;
                bool percent = text.EndsWith("%");
                if (percent) text = text.Substring(0, text.Length - 1).TrimEnd();
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    return percent ? f / 100f : f;
            }
            catch { }   // не число: одно кривое поле не должно ронять главу
            return null;
        }

        /// <summary>То же, но с запасным значением.</summary>
        public static float Parse(JToken t, float fallback) => Parse(t) ?? fallback;

        /// <summary>Строка — тот же разбор: числа приходят и текстом.</summary>
        public static float Parse(string s, float fallback) => Parse((JToken)s) ?? fallback;

        /// <summary>
        /// ЧИСЛО ИЗ ЗНАЧЕНИЯ СОСТОЯНИЯ — то, что видит арифметика над
        /// переменными: <c>inc</c>, пороги выбора, шкалы статов.
        ///
        /// <para>Отдельно от <see cref="Parse(JToken)"/>, потому что вопрос
        /// другой. Там читают ПОЛЕ КОМАНДЫ, где «57%» значит долю; здесь читают
        /// ЗНАЧЕНИЕ ПЕРЕМЕННОЙ, где процент — просто текст, зато <c>true</c>
        /// законно значит единицу (так же считает язык выражений).</para>
        ///
        /// <para>Правило было записано трижды и по-разному: плеер не разбирал
        /// число из строки вовсе (<c>inc</c> над строковым «10» давал 1, стирая
        /// значение), шкала статов разбирала, а язык выражений разбирал и умел
        /// bool. Число-строкой — не выдумка: так сохраняется ВВОД ИГРОКА
        /// (<c>VnStage.Input</c> кладёт в переменную строку), и спросить у
        /// игрока число — обычное дело.</para>
        ///
        /// <para>Согласовано с <c>LvnExpression.AsNum</c>, но не бросает:
        /// выражение вправе объявить «'абв' — не число» ошибкой автора, а
        /// счётчику посреди главы падать не за что.</para>
        /// </summary>
        public static double Value(JToken t, double fallback)
        {
            if (t == null || t.Type == JTokenType.Null) return fallback;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return t.Value<double>();
                case JTokenType.Boolean:
                    return t.Value<bool>() ? 1 : 0;
            }
            var text = t.Type == JTokenType.String ? t.Value<string>() : t.ToString();
            return double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;
        }
    }
}
