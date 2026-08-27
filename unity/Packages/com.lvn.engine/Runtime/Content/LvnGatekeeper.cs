using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn.Content
{
    /// <summary>
    /// ПРИВРАТНИК — что игроку сейчас открыто.
    ///
    /// <para>Вопрос один, а отвечали на него порознь. Правило «какая глава
    /// доступна» — «первая всегда, дальше по дошедшему» — стояло ДОСЛОВНО
    /// ДВАЖДЫ: в списке глав карусели и в перезапуске с карточки новеллы.
    /// Правило «доступна ли новелла» (авторское выражение <c>unlock</c> над
    /// кросс-новелльными статами) жило третьим местом, внутри хаба, вместе с
    /// кэшем этих статов.</para>
    ///
    /// <para>Копии живут ровно до первой правки. Стоит владельцу решить, что
    /// пройденная новелла открывает эпилог или что глава открывается покупкой,
    /// — и правки поедут в одно место из трёх, а разойдётся это на живом
    /// экране: карусель пускает, карточка не пускает.</para>
    ///
    /// <para>Ответственность: сказать «открыто/закрыто» и ничего больше. Что
    /// показать вместо закрытого, как объяснить отказ и куда вести после
    /// покупки — дело экранов; списание — дело Кассира.</para>
    /// </summary>
    public static class LvnGatekeeper
    {
        /// <summary>
        /// ГЛАВА ОТКРЫТА? Первая — всегда (иначе новеллу нельзя начать),
        /// дальше — до той, которой игрок уже достигал.
        ///
        /// <para><paramref name="reached"/> — номер самой дальней ПОЧАТОЙ главы
        /// (см. <c>LvnProgress</c>), <paramref name="firstNumber"/> — номер
        /// первой главы новеллы: он не обязан быть единицей, номера задаёт
        /// автор.</para>
        /// </summary>
        public static bool ChapterOpen(int number, int reached, int firstNumber)
            => number <= reached || number == firstNumber;

        /// <summary>Та же проверка для главы целиком — чтобы вызывающему не
        /// пришлось доставать номер самому.</summary>
        public static bool ChapterOpen(LvnChapter chapter, int reached, int firstNumber)
            => chapter != null && ChapterOpen(chapter.number, reached, firstNumber);

        /// <summary>
        /// НОВЕЛЛА ЗАКРЫТА? Автор пишет условие в <c>title.unlock</c> — выражение
        /// над кросс-новелльными статами игрока (<c>global.*</c>). Нет условия —
        /// новелла открыта.
        ///
        /// <para>Сломанное выражение НЕ ЗАКРЫВАЕТ игру: опечатка автора не должна
        /// превращаться в стену для игрока, поэтому при ошибке разбора новелла
        /// считается открытой.</para>
        /// </summary>
        public static bool TitleLocked(LvnTitle title, JObject globalVars)
        {
            if (title == null || string.IsNullOrEmpty(title.unlock)) return false;
            try
            {
                var vars = new Dictionary<string, JToken> { ["global"] = globalVars ?? new JObject() };
                return !Lvn.LvnExpression.EvaluateBool(title.unlock, vars);
            }
            catch { return false; }
        }
    }
}
