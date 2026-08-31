using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЧТО С ЭТОЙ ГЛАВОЙ — один ответ на все три списка глав в приложении.
    ///
    /// <para>Ответ жил внутри карточки новеллы одним длинным выражением, а два
    /// соседних списка — окно перезапуска и выбор главы в ленте — спрашивали
    /// лишь его половину («открыта ли») и собирали входы Привратника у себя.
    /// Правило было общим, а спрашивали его по-разному, и половины уже успели
    /// разойтись: свой расчёт «номер не больше достигнутого» рисовал ЗАМОК на
    /// первой главе непочатой новеллы — рядом с играбельной кнопкой «Играть», —
    /// а список глав объявлял непочатую новеллу пройденной, когда её главы
    /// нумерованы с нуля.</para>
    ///
    /// <para>Здесь закреплены ПРАВИЛА, а не выражение: «пройдена» — строго
    /// раньше достигнутой (достигнутую ещё играют), «открыта» — вопрос
    /// Привратника, «пройдена вся новелла» — вопрос Прогресса, а не свой
    /// пересчёт номеров.</para>
    /// </summary>
    public sealed class ChapterMarkTests
    {
        private const string Id = "t_chapter_marks";

        private static LvnTitle Title(params int[] numbers)
        {
            var chapters = new List<LvnChapter>();
            foreach (var n in numbers) chapters.Add(new LvnChapter { id = "ch" + n, number = n });
            return new LvnTitle { id = Id, seasons = new List<LvnSeason> { new LvnSeason { chapters = chapters } } };
        }

        private static LvnChapter Глава(LvnTitle t, int number) => t.ChapterById("ch" + number);

        private static IReadOnlyList<LvnChapterMark> Метки(LvnTitle t)
            => LvnChapterMarks.ForAll(t, t.ChaptersOf());

        [SetUp]
        [TearDown]
        public void Clean() => LvnProgress.ResetTitle(Id);

        // ── непочатая новелла ───────────────────────────────────────────────

        // ЖИВОЙ ДЕФЕКТ, ради которого дом и заведён. Первая глава открыта
        // ВСЕГДА — иначе новеллу нельзя начать вовсе, а игрок видит замок на
        // единственном входе и кнопку «Играть» рядом с ним: экран противоречит
        // сам себе, и верят обычно замку.
        [Test]
        public void НепочатаяНовеллаОткрываетПервуюГлавуИЗапираетОстальные()
        {
            var метки = Метки(Title(1, 2, 3));

            Assert.AreEqual(LvnChapterMark.Open, метки[0],
                "на первой главе непочатой новеллы висит замок — войти в новеллу неоткуда");
            Assert.AreEqual(LvnChapterMark.Locked, метки[1]);
            Assert.AreEqual(LvnChapterMark.Locked, метки[2],
                "непройденные главы открыты наперёд — список перестал что-либо значить");
        }

        // Номер первой главы задаёт АВТОР, а не единица: вводная новелла и вся
        // воронка приложения начинаются с нуля. Считай мы первой единицу — вход
        // в воронку оказался бы заперт.
        [Test]
        public void ПервойСчитаетсяГлаваАвтораАНеЕдиница()
        {
            var метки = Метки(Title(0, 1, 2));

            Assert.AreEqual(LvnChapterMark.Open, метки[0], "пилот с номером 0 — законное начало новеллы");
            Assert.AreEqual(LvnChapterMark.Locked, метки[1]);
        }

        // Порядок в файле — случайность формата, номер — замысел автора.
        // Открыта та, у которой наименьший НОМЕР, где бы она ни была записана.
        [Test]
        public void ПервойСчитаетсяНаименьшийНомерАНеВерхняяСтрочка()
        {
            // Список берём В ПОРЯДКЕ ЗАПИСИ, как его отдаёт манифест: экраны
            // рисуют строки тем же порядком, каким он к ним пришёл.
            var t = Title(3, 1, 2);
            var записаны = t.seasons[0].chapters;

            var метки = LvnChapterMarks.ForAll(t, записаны);

            for (int i = 0; i < записаны.Count; i++)
                Assert.AreEqual(записаны[i].number == 1 ? LvnChapterMark.Open : LvnChapterMark.Locked, метки[i],
                    "открытой оказалась не первая глава, а первая записанная");
        }

        // ── игрок в середине ────────────────────────────────────────────────

        [Test]
        public void НачатаяГлаваТекущаяПрошлаяПройденаСледующаяЗаперта()
        {
            var t = Title(1, 2, 3);
            LvnProgress.StartChapter(t, Глава(t, 2));

            var метки = Метки(t);

            Assert.AreEqual(LvnChapterMark.Done, метки[0], "сыгранная глава перестала быть пройденной");
            Assert.AreEqual(LvnChapterMark.Current, метки[1],
                "глава, в которой игрок стоит, — точка возврата, и отмечена она отдельно от пройденных");
            Assert.AreEqual(LvnChapterMark.Locked, метки[2], "до непочатой главы дали дойти в обход истории");
        }

        // ПАРТНЁРСКИЙ СЛУЧАЙ. Прошёл гл.2, вернулся и перезапустил её —
        // достигнутой осталась гл.3, но СЫГРАНА она не была. Отметь её
        // галочкой, и список врёт игроку о том, что он читал: пройденной
        // числится глава, которую он не открывал.
        [Test]
        public void ПерезапускНеДелаетДостигнутуюГлавуПройденной()
        {
            var t = Title(1, 2, 3);
            LvnProgress.StartChapter(t, Глава(t, 2));
            LvnProgress.FinishChapter(t, Глава(t, 3));   // гл.3 достигнута
            LvnProgress.StartChapter(t, Глава(t, 2));    // и тут же переигран второй акт

            var метки = Метки(t);

            Assert.AreEqual(LvnChapterMark.Current, метки[1], "игрок стоит во второй главе");
            Assert.AreEqual(LvnChapterMark.Open, метки[2],
                "третья глава помечена пройденной, хотя её ни разу не читали: " +
                "«пройдена» — строго РАНЬШЕ достигнутой, пока новелла не дочитана");
        }

        // Достигнутая глава сыграна лишь тогда, когда новелла кончилась. Ушёл
        // из середины через меню — точки нет, но и галочки на той главе быть не
        // может: игрок в неё вошёл, а не прошёл.
        [Test]
        public void ДостигнутаяНоНеДочитаннаяГлаваОстаётсяОткрытойАНеПройденной()
        {
            var t = Title(1, 2, 3);
            LvnProgress.StartChapter(t, Глава(t, 2));
            LvnProgress.FinishChapter(t, null);          // вышел, не дойдя до третьей

            var метки = Метки(t);

            Assert.AreEqual(LvnChapterMark.Done, метки[0]);
            Assert.AreEqual(LvnChapterMark.Open, метки[1],
                "новелла не дочитана, а глава на границе достигнутого уже в галочках");
            Assert.AreEqual(LvnChapterMark.Locked, метки[2]);
        }

        // ── дочитанная новелла ──────────────────────────────────────────────

        // Финал снимает точку продолжения — значит, ни одна глава не «текущая»,
        // и все они честно пройдены, включая последнюю: её галочка и есть то,
        // ради чего игрок читал.
        [Test]
        public void ДочитаннаяНовеллаВсяВГалочкахИБезТочки()
        {
            var t = Title(1, 2, 3);
            LvnProgress.StartChapter(t, Глава(t, 3));
            LvnProgress.FinishChapter(t, null);

            var метки = Метки(t);

            CollectionAssert.AreEqual(
                new[] { LvnChapterMark.Done, LvnChapterMark.Done, LvnChapterMark.Done }, метки,
                "новелла дочитана, а список это отрицает");
            CollectionAssert.DoesNotContain(метки, LvnChapterMark.Current,
                "точки продолжения в дочитанной новелле нет — повтор начинается с начала");
        }

        // НОЛЬ ТУТ КЛЮЧЕВОЙ. Своё правило «дошёл до N ≥ последняя N» на новелле
        // из одного пилота с номером 0 отвечало «пройдено» на ЧИСТОМ
        // устройстве: галочки стояли до первого запуска. Спрашивать надо
        // Прогресс, который отличает «ничего не начинали» от «дошёл до нуля».
        [Test]
        public void НовеллаСНулевойГлавойПройденаТолькоПослеФинала()
        {
            var t = Title(0);

            Assert.AreEqual(LvnChapterMark.Open, Метки(t)[0],
                "непочатый пилот с номером 0 отмечен пройденным — игрок видит галочку до первого запуска");

            LvnProgress.StartChapter(t, Глава(t, 0));
            Assert.AreEqual(LvnChapterMark.Current, Метки(t)[0]);

            LvnProgress.FinishChapter(t, null);
            Assert.AreEqual(LvnChapterMark.Done, Метки(t)[0],
                "пилот дочитан, а галочки нет — для игрока новелла не кончается никогда");
        }

        [Test]
        public void ДочитаннаяНовеллаСНулевойПервойГлавойВсяВГалочках()
        {
            var t = Title(0, 1, 2);
            LvnProgress.StartChapter(t, Глава(t, 2));
            LvnProgress.FinishChapter(t, null);

            CollectionAssert.AreEqual(
                new[] { LvnChapterMark.Done, LvnChapterMark.Done, LvnChapterMark.Done }, Метки(t));
        }

        // ── битые входы ─────────────────────────────────────────────────────

        // Манифест приходит из сети и из переимпорта: недописанная глава и
        // пустой список — обычное дело. Список глав — не то место, где игра
        // имеет право упасть: уронись он, экран новеллы остался бы вовсе без
        // глав, то есть одна битая запись отняла бы всё прохождение.
        [Test]
        public void БитыйВходНеРоняетСписокАЗапирает()
        {
            var t = Title(1, 2);

            Assert.AreEqual(0, LvnChapterMarks.ForAll(t, new List<LvnChapter>()).Count,
                "пустому списку соответствует пустой ответ, а не выдуманная строка");
            Assert.AreEqual(0, LvnChapterMarks.ForAll(t, null).Count, "списка нет — и меток нет");

            var сДырой = LvnChapterMarks.ForAll(t, new List<LvnChapter> { null, Глава(t, 1) });
            Assert.AreEqual(LvnChapterMark.Locked, сДырой[0],
                "недописанная глава пустила игрока внутрь себя");
            Assert.AreEqual(LvnChapterMark.Open, сДырой[1],
                "дыра в списке унесла с собой соседнюю живую главу");
        }

        // Новеллы нет — спрашивать прогресс не у кого, и ЗАПЕРТО здесь
        // единственный честный ответ: пускать в главу, о которой ничего не
        // известно, значит вести игрока в пустой экран.
        [Test]
        public void БезНовеллыВсеГлавыЗаперты()
        {
            var главы = new List<LvnChapter>
            {
                new LvnChapter { id = "ch1", number = 1 },
                new LvnChapter { id = "ch2", number = 2 },
            };

            var метки = LvnChapterMarks.ForAll(null, главы);

            Assert.AreEqual(2, метки.Count);
            CollectionAssert.AreEqual(new[] { LvnChapterMark.Locked, LvnChapterMark.Locked }, метки);
            Assert.AreEqual(LvnChapterMark.Locked, LvnChapterMarks.Of(null, главы[0]));
            Assert.AreEqual(LvnChapterMark.Locked, LvnChapterMarks.Of(Title(1), null));
        }

        // ── чем метка отвечает экранам ──────────────────────────────────────

        // Двум спискам из трёх нужен один вопрос — «можно ли войти». Ответ на
        // него ОДИН для всего, кроме запертого: пройденную переигрывают, в
        // текущую возвращаются, открытую начинают.
        [Test]
        public void ИгратьМожноВоВсёКромеЗапертого()
        {
            Assert.IsTrue(LvnChapterMarks.Playable(LvnChapterMark.Done), "пройденную главу нельзя переиграть");
            Assert.IsTrue(LvnChapterMarks.Playable(LvnChapterMark.Current), "в текущую главу нельзя вернуться");
            Assert.IsTrue(LvnChapterMarks.Playable(LvnChapterMark.Open), "открытую главу нельзя начать");
            Assert.IsFalse(LvnChapterMarks.Playable(LvnChapterMark.Locked),
                "запертая глава играбельна — история читается с конца");
        }

        // Одна глава и список из неё одной обязаны отвечать одинаково: иначе
        // экран, спросивший про главу поштучно, получит другой ответ, чем
        // список рядом, — то самое расхождение, ради ухода от которого дом и
        // заводился.
        [Test]
        public void ОднаГлаваОтвечаетТемЖеЧтоИСписокИзНеё()
        {
            var t = Title(1, 2, 3);
            LvnProgress.StartChapter(t, Глава(t, 2));

            var главы = t.ChaptersOf();
            var список = LvnChapterMarks.ForAll(t, главы);
            for (int i = 0; i < главы.Count; i++)
                Assert.AreEqual(список[i], LvnChapterMarks.Of(t, главы[i]),
                    "поштучный вопрос и список разошлись на главе " + главы[i].number);
        }
    }
}
