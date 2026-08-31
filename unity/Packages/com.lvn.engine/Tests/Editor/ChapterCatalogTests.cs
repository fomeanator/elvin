using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ВОПРОСЫ К КАТАЛОГУ ГЛАВ — <see cref="LvnTitleExtensions"/>.
    ///
    /// <para>«Какая глава первая», «какая следующая», «на какой остановились» —
    /// это свойства ДАННЫХ, но каждый спрашивавший обходил сезоны сам: привратник,
    /// загрузчик, оболочка, метка прогресса и облачный свёрток — пять записей
    /// одних и тех же трёх правил. Расходились они молча и по-разному: загрузчик
    /// и оболочка возвращали следующую главу одинаково лишь пока номера идут
    /// подряд, а метка прогресса и свёрток искали остановку РАЗНЫМ порядком —
    /// одна сперва весь список по id, второй одним проходом, где совпадение по
    /// номеру могло встретиться раньше нужного id.</para>
    ///
    /// <para>Тесты закрепляют правила, а не реализацию: номер — авторский
    /// замысел, порядок в файле — случайность формата.</para>
    /// </summary>
    public class ChapterCatalogTests
    {
        /// <summary>Новелла из глав, ПЕРЕЧИСЛЕННЫХ в заданном порядке (не в
        /// порядке номеров) и разложенных по двум сезонам — так бывает у
        /// импортированного контента.</summary>
        private static LvnTitle Title(params int[] numbers)
        {
            var s1 = new List<LvnChapter>();
            var s2 = new List<LvnChapter>();
            for (int i = 0; i < numbers.Length; i++)
                (i % 2 == 0 ? s1 : s2).Add(new LvnChapter { id = "ch" + numbers[i], number = numbers[i] });
            return new LvnTitle
            {
                id = "t",
                seasons = new List<LvnSeason>
                {
                    new LvnSeason { chapters = s1 },
                    new LvnSeason { chapters = s2 },
                },
            };
        }

        // ── первая и последняя ───────────────────────────────────────────────

        [Test]
        public void ПерваяПоНомеруАНеПоПорядкуЗаписи()
        {
            // Автор записал третью главу первой — «начать сначала» обязано
            // повести в первую, а не в верхнюю строчку файла.
            var t = Title(3, 1, 2);
            Assert.AreEqual("ch1", t.FirstChapter()?.id);
            Assert.AreEqual("ch3", t.LastChapter()?.id);
        }

        [Test]
        public void ПилотСНулёмТожеПервый()
        {
            // Номер 0 — законное начало новеллы, а не «нет номера».
            Assert.AreEqual(0, Title(2, 0, 1).FirstChapter().number);
        }

        [Test]
        public void ПустаяНовеллаНеРоняет()
        {
            Assert.IsNull(new LvnTitle().FirstChapter());
            Assert.IsNull(new LvnTitle().LastChapter());
            Assert.IsNull(((LvnTitle)null).FirstChapter());
            Assert.IsNull(new LvnTitle { seasons = new List<LvnSeason> { null } }.LastChapter());
        }

        // ── следующая ────────────────────────────────────────────────────────

        [Test]
        public void СледующаяБерётсяПоНомеруАНеПоСоседству()
        {
            // Номера у импортированных новелл идут не подряд: после 2 может
            // стоять 7. «Следующая» — наименьший номер СТРОГО больше текущего.
            var t = Title(1, 2, 7, 20);
            Assert.AreEqual("ch7", t.ChapterAfter(t.ChapterById("ch2"))?.id);
            Assert.AreEqual("ch20", t.ChapterAfter(t.ChapterById("ch7"))?.id);
        }

        [Test]
        public void СледующаяПерешагиваетГраницуСезона()
        {
            // Сезон — способ ПОКАЗАТЬ главы, а не порядок их прохождения.
            // Считай «следующую» внутри сезона — и на последней главе первого
            // сезона новелла кончалась бы: игрока вернули бы в меню посреди
            // истории, а вторая половина контента осталась бы недостижимой.
            var t = Title(1, 2, 3, 4);   // нечётные в первом сезоне, чётные во втором
            Assert.AreEqual("ch2", t.ChapterAfter(t.ChapterById("ch1"))?.id,
                "переход из первого сезона во второй потерян");
            Assert.AreEqual("ch3", t.ChapterAfter(t.ChapterById("ch2"))?.id,
                "переход из второго сезона обратно в первый потерян");
        }

        [Test]
        public void СледующаяСчитаетсяПоНомеруАНеПоТомуЖеОбъекту()
        {
            // Главу, «на которой остановились», достают из СТАРОГО манифеста —
            // из метки прогресса, из облачного свёртка, из ссылки на
            // продолжение. После переимпорта это уже другой объект с тем же
            // номером. Сравнивай мы главы по ссылке, конец главы у вернувшегося
            // игрока вёл бы в меню вместо продолжения.
            var t = Title(1, 2, 3);
            var изПрошлогоМанифеста = new LvnChapter { id = "ch1", number = 1 };

            Assert.AreEqual("ch2", t.ChapterAfter(изПрошлогоМанифеста)?.id,
                "глава из прошлого манифеста не ведёт дальше — прохождение обрывается");
        }

        [Test]
        public void ПослеПоследнейНичего()
        {
            // Null здесь значит «возвращай игрока в меню», и спутать это с
            // «главу не нашли» нельзя.
            var t = Title(1, 2);
            Assert.IsNull(t.ChapterAfter(t.ChapterById("ch2")));
            Assert.IsNull(t.ChapterAfter(null));
        }

        [Test]
        public void ДвеГлавыСОднимНомеромНеЗацикливают()
        {
            // Битый контент: «следующая» не должна вернуть саму себя, иначе
            // глава заканчивается собой же — бесконечно.
            var t = Title(1, 1, 2);
            var cur = t.ChapterById("ch1");
            var next = t.ChapterAfter(cur);
            Assert.AreEqual(2, next?.number, "равный номер — не «следующий»");
        }

        // ── на чём остановились ──────────────────────────────────────────────

        [Test]
        public void ОстановкаИщетсяПоIdАНеПоНомеру()
        {
            // Id точен; номер — только выручалочка. Пока id жив, номер не
            // должен уводить в другую главу.
            var t = Title(1, 2, 3);
            Assert.AreEqual("ch3", t.ChapterByIdOrNumber("ch3", number: 1)?.id);
        }

        [Test]
        public void ПропавшийIdВыручаетНомер()
        {
            // Переимпорт переименовал главы: id из метки больше нет. Терять
            // из-за этого прохождение нельзя.
            var t = Title(1, 2, 3);
            Assert.AreEqual("ch2", t.ChapterByIdOrNumber("старое-имя", number: 2)?.id);
        }

        [Test]
        public void БезНомераНичегоНеУгадываем()
        {
            // Ноль — это «номера не записали», а не «глава номер ноль»: иначе
            // пустая метка увела бы игрока в пилот.
            var t = Title(0, 1, 2);
            Assert.IsNull(t.ChapterByIdOrNumber("нет-такой", number: 0),
                "нулевой номер — отсутствие сведений, а не пилот");
            Assert.IsNull(t.ChapterByIdOrNumber(null, number: 0));
            Assert.AreEqual("ch0", t.ChapterByIdOrNumber("ch0", number: 0)?.id,
                "по id пилот находиться обязан");
        }

        [Test]
        public void ГлаваПоIdНеПутаетсяВСезонах()
        {
            var t = Title(1, 2, 3, 4);
            Assert.AreEqual(4, t.ChapterById("ch4")?.number, "глава из второго сезона не найдена");
            Assert.IsNull(t.ChapterById("нет"));
            Assert.IsNull(t.ChapterById(""));
        }

        [Test]
        public void ПустойIdОтдаётРешениеНомеру()
        {
            // Метка прогресса старого формата хранила только номер, id в ней
            // пуст. Прими пустую строку за имя главы — и поиск честно не найдёт
            // ничего, а игрок, вернувшийся после обновления, начнёт новеллу
            // сначала. Пустой id — это «сведений нет», и тогда решает номер.
            var t = Title(1, 2, 3);
            Assert.AreEqual("ch2", t.ChapterByIdOrNumber("", number: 2)?.id, "пустой id съел исправный номер");
            Assert.AreEqual("ch3", t.ChapterByIdOrNumber(null, number: 3)?.id, "отсутствующий id съел исправный номер");
        }

        [Test]
        public void ДыраВКаталогеНеУноситОстальныеГлавы()
        {
            // Манифест приходит из сети и из переимпорта: пустой сезон и
            // недописанная глава в нём — обычное дело. Уронись каталог на
            // такой дыре, экран новеллы остался бы вовсе без глав — то есть
            // одна битая запись отняла бы у игрока всё прохождение.
            var t = new LvnTitle
            {
                id = "t",
                seasons = new List<LvnSeason>
                {
                    new LvnSeason { chapters = new List<LvnChapter> { null, new LvnChapter { id = "ch2", number = 2 } } },
                    new LvnSeason { chapters = null },
                    null,
                    new LvnSeason { chapters = new List<LvnChapter> { new LvnChapter { id = "ch1", number = 1 } } },
                },
            };

            Assert.AreEqual("ch1", t.FirstChapter()?.id, "дыра в каталоге унесла первую главу");
            Assert.AreEqual("ch2", t.LastChapter()?.id, "дыра в каталоге унесла последнюю главу");
            Assert.AreEqual("ch2", t.ChapterAfter(t.ChapterById("ch1"))?.id, "дыра в каталоге оборвала переход");
            Assert.AreEqual(2, t.ChaptersOf().Count, "пустая запись попала в перечень глав как настоящая");
        }
    }
}
