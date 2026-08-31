using System.Collections.Generic;
using Lvn.Content;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЧЕМ ГЛАВА ОТМЕЧЕНА В СПИСКЕ.
    ///
    /// <para>Состояние жило числом 0..3, объяснённым комментарием в одном
    /// месте и разобранным <c>state == N</c> в шести других: прочитать
    /// «state == 2» без возврата к комментарию нельзя, а пятое состояние
    /// (скажем, «открыта, но стоит энергии») заставило бы перечитать все
    /// шесть.</para>
    /// </summary>
    public enum LvnChapterMark { Done, Current, Open, Locked }

    /// <summary>
    /// СОСТОЯНИЕ ГЛАВЫ В СПИСКЕ — один ответ на «что с этой главой» для всех
    /// трёх мест, где список глав показывают.
    ///
    /// <para>Раньше ответ жил внутри карточки новеллы одним длинным выражением,
    /// а два соседних списка — окно перезапуска и выбор главы в ленте —
    /// спрашивали лишь его половину («открыта ли») и собирали входы Швейцара
    /// у себя: достигнутую главу и номер первой. Правило было общим, а
    /// спрашивали его по-разному, и одна половина уже успела разойтись с
    /// другой: свой расчёт «номер не больше достигнутого» рисовал замок на
    /// первой главе непочатой новеллы — рядом с играбельной кнопкой.</para>
    ///
    /// <para>Здесь ответ один и целиком: он спрашивает Швейцара про
    /// доступность и Прогресс про пройденное, а показ — уже дело экрана.</para>
    /// </summary>
    public static class LvnChapterMarks
    {
        /// <summary>Разом на весь список: «докуда дошёл», «где стоит» и «номер
        /// первой» спрашиваются ОДИН раз, а не на каждой строке.</summary>
        public static IReadOnlyList<LvnChapterMark> ForAll(LvnTitle title, IReadOnlyList<LvnChapter> chapters)
        {
            var marks = new List<LvnChapterMark>();
            if (chapters == null) return marks;
            if (title == null)
            {
                for (int i = 0; i < chapters.Count; i++) marks.Add(LvnChapterMark.Locked);
                return marks;
            }
            int reached = LvnProgress.Reached(title);
            var current = LvnProgress.Current(title);
            // Завершённая новелла: тогда и глава на границе достигнутого честно
            // «пройдена». Спрашиваем Прогресс, а не считаем сами: своё правило
            // не знало, что НЕПОЧАТАЯ новелла не пройдена, и новелла с первой
            // главой под номером 0 показывала все главы галочками на чистом
            // устройстве.
            bool finished = LvnProgress.Finished(title);
            int firstNumber = LvnGatekeeper.FirstNumber(title);
            foreach (var ch in chapters)
            {
                if (ch == null) { marks.Add(LvnChapterMark.Locked); continue; }
                // ПРОЙДЕНА — строго раньше достигнутой: сама достигнутая ещё
                // не сыграна (партнёр прошёл гл.2, перезапустил её — и
                // «пройденной» рисовалась гл.3).
                marks.Add(
                    current != null && ch.id == current.id ? LvnChapterMark.Current
                    : ch.number < reached || (finished && ch.number <= reached) ? LvnChapterMark.Done
                    : LvnGatekeeper.ChapterOpen(ch.number, reached, firstNumber) ? LvnChapterMark.Open
                    : LvnChapterMark.Locked);
            }
            return marks;
        }

        /// <summary>Одна глава — когда список не нужен.</summary>
        public static LvnChapterMark Of(LvnTitle title, LvnChapter chapter)
        {
            var one = ForAll(title, new[] { chapter });
            return one.Count > 0 ? one[0] : LvnChapterMark.Locked;
        }

        /// <summary>Можно ли в неё войти. Единственный вопрос двух списков из
        /// трёх — и раньше каждый собирал ответ у себя.</summary>
        public static bool Playable(LvnChapterMark mark) => mark != LvnChapterMark.Locked;
    }
}
