using System.Globalization;

namespace Lvn.Content
{
    /// <summary>
    /// КАК ЯЗЫК ПИШЕТ ЧИСЛА — разделители разрядов и дроби.
    ///
    /// <para>Роль отделилась от <see cref="LvnWords"/> по границе вопроса: там
    /// «каким словом это назвать», здесь «как выглядит число». Слово переводят,
    /// разделитель — нет; его выбирают вместе с языком, и выбор этот один на
    /// всю новеллу.</para>
    ///
    /// <para>Оба разделителя жили порознь и оба неправильно. Дробь центр
    /// загрузок дописывал руками: <c>.Replace('.', ',')</c> — русская запятая
    /// насильно, в любой новелле. Разряды брались из <c>ToString("N0")</c> без
    /// культуры, то есть из НАСТРОЕК УСТРОЙСТВА: одна и та же сумма выглядела
    /// по-разному на двух телефонах, а язык новеллы на это не влиял вовсе.
    /// Английская новелла на русском телефоне писала «1 200» вместо
    /// «1,200».</para>
    ///
    /// <para>Умолчания английские, как и у слов: «1,200.5». Русская новелла
    /// ставит <c>unit.group</c> = пробел и <c>unit.decimal</c> = запятая — и
    /// получает «1 200,5» на любом устройстве.</para>
    ///
    /// <para>Пробел взят ОБЫЧНЫЙ, а не неразрывный: в авторском шрифте
    /// неразрывного может не оказаться, и вместо разделителя игрок увидит
    /// пустой квадрат — цену, которую нельзя прочесть.</para>
    /// </summary>
    public static class LvnNumberFormat
    {
        private static NumberFormatInfo _cached;
        private static string _group, _decimal;

        /// <summary>Формат по действующим словам новеллы.</summary>
        public static NumberFormatInfo Current
        {
            get
            {
                string g = LvnWords.Of("unit.group", ",");
                string d = LvnWords.Of("unit.decimal", ".");
                // Пересобираем, только когда слова сменились: числа рисуются
                // часто, а клонировать формат на каждую пилюлю незачем.
                if (_cached != null && g == _group && d == _decimal) return _cached;
                var f = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
                f.NumberGroupSeparator = g;
                f.NumberDecimalSeparator = d;
                _group = g;
                _decimal = d;
                _cached = f;
                return f;
            }
        }

        /// <summary>Целое с разрядами: «1,200» / «1 200».</summary>
        public static string Groups(long value) => value.ToString("N0", Current);

        /// <summary>Дробное с заданным числом знаков: «1.5» / «1,5».</summary>
        public static string Decimals(float value, string pattern = "0.#")
            => value.ToString(pattern, Current);
    }
}
