using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// КАРТОЧКА НОВЕЛЛЫ СПРАШИВАЕТ «НАЧИНАЛИ ЛИ» — и по ответу решает две
    /// вещи, которые игрок видит первыми.
    ///
    /// <para>«Продолжить» в блоке сохранений и «Начать заново» в нижней панели
    /// показываются ровно тогда, когда есть что продолжать и что стирать. Это
    /// ДВА отдельных места на одном экране, и оба спрашивали одно и то же
    /// своими словами: «есть точка ИЛИ потолок больше нуля».</para>
    ///
    /// <para>Вторая половина лжёт на новелле, чьи главы нумерованы с нуля.
    /// Игрок прошёл вводную — а карточка предлагает ему «начать», прячет
    /// «Продолжить» и пишет «сохранений пока нет». Прогресс при этом цел:
    /// человек видит потерю, которой не было, и первым делом жмёт «начать
    /// заново», после чего потеря становится настоящей.</para>
    ///
    /// <para>Экран строится и перестраивается без панели и без ассетов
    /// (<see cref="TitleDetailScreen.Rebuild"/> публичен ровно для этого), а
    /// подписи берутся тем же <see cref="LvnWords"/>, что и в живой игре, —
    /// иначе проверка сломалась бы от загруженного каталога перевода.</para>
    /// </summary>
    public sealed class TitleDetailProgressTests
    {
        private const string Id = "t_detail_novel";
        private const string Ноль = "t_detail_pilot";

        private static string Продолжить => LvnWords.Of("hub.continue", "Continue");
        private static string НачатьЗаново => LvnWords.Of("title.restart", "Start over");
        private static string НетСейвов => LvnWords.Of("saves.empty", "No saves yet — start reading.");

        private static LvnTitle Title(string id, params (string id, int number)[] chapters)
        {
            var list = new List<LvnChapter>();
            foreach (var (cid, number) in chapters) list.Add(new LvnChapter { id = cid, number = number });
            return new LvnTitle { id = id, name = id, seasons = new List<LvnSeason> { new LvnSeason { chapters = list } } };
        }

        private static LvnTitle Три() => Title(Id, ("c1", 1), ("c2", 2), ("c3", 3));

        /// <summary>Вводная: ОДНА глава, и её номер — ноль.</summary>
        private static LvnTitle Пилот() => Title(Ноль, ("pilot", 0));

        [SetUp]
        [TearDown]
        public void Стереть()
        {
            LvnProgress.ResetTitle(Id);
            LvnProgress.ResetTitle(Ноль);
        }

        /// <summary>Построить карточку так, как её строит хост: посадить
        /// новеллу и перечитать содержимое.</summary>
        private static VisualElement Карточка(LvnTitle title)
        {
            var экран = new TitleDetailScreen(new TestAssets()) { Title = title };
            экран.Rebuild();
            return экран;
        }

        private static bool ЕстьНадпись(VisualElement корень, string text)
        {
            foreach (var el in Всё(корень))
                if (el is TextElement t && t.text == text) return true;
            return false;
        }

        private static IEnumerable<VisualElement> Всё(VisualElement el)
        {
            yield return el;
            foreach (var ch in el.Children())
                foreach (var d in Всё(ch)) yield return d;
        }

        // Непочатая новелла: продолжать нечего и стирать нечего. Покажи здесь
        // «Начать заново» — и игрок решит, что уже играл: кнопка на карточке
        // это утверждение о нём, а не украшение.
        [Test]
        public void НепочатаяНовеллаНеПредлагаетНиПродолжитьНиНачатьЗаново()
        {
            var карточка = Карточка(Три());

            Assert.IsFalse(ЕстьНадпись(карточка, Продолжить),
                "непочатая новелла предлагает продолжить — продолжать нечего");
            Assert.IsFalse(ЕстьНадпись(карточка, НачатьЗаново),
                "непочатая новелла предлагает начать заново — игрок решит, что уже играл");
            Assert.IsTrue(ЕстьНадпись(карточка, НетСейвов),
                "у непочатой новеллы пропало объяснение пустого блока сохранений");
        }

        // Початая — предлагает оба. Это опорная точка: без неё следующие две
        // проверки прошли бы и на экране, который не показывает ничего никогда.
        [Test]
        public void ПочатаяНовеллаПредлагаетИПродолжитьИНачатьЗаново()
        {
            var t = Три();
            LvnProgress.StartChapter(t, t.ChaptersOf()[1]);

            var карточка = Карточка(Три());

            Assert.IsTrue(ЕстьНадпись(карточка, Продолжить), "початая новелла не предлагает продолжить");
            Assert.IsTrue(ЕстьНадпись(карточка, НачатьЗаново), "початую новеллу нечем начать заново");
        }

        // ТА ЖЕ новелла с главами от нуля — вводная, через которую проходит
        // каждый новый игрок. Здесь «потолок больше нуля» и лгало: прогресс
        // цел, а карточка показывает его непочатым.
        [Test]
        public void НовеллаСНулевойГлавойТожеПредлагаетПродолжитьИНачатьЗаново()
        {
            var t = Пилот();
            LvnProgress.StartChapter(t, t.ChaptersOf()[0]);

            Assert.AreEqual(0, LvnProgress.Reached(t), "потолок нулевой главы — ноль, в этом вся ловушка");

            var карточка = Карточка(Пилот());

            Assert.IsTrue(ЕстьНадпись(карточка, Продолжить),
                "игрок стоит в нулевой главе, а карточка прячет «Продолжить» — прогресс выглядит потерянным");
            Assert.IsTrue(ЕстьНадпись(карточка, НачатьЗаново),
                "вводную нечем начать заново, хотя её уже играли");
            Assert.IsFalse(ЕстьНадпись(карточка, НетСейвов),
                "карточка пишет «сохранений нет» игроку, который в этой новелле сейчас находится");
        }

        // Дочитанная новелла с нулевой главой: точку снял финал, остался только
        // потолок — ноль. Обе половины признака поодиночке молчат, и лишь
        // вместе они говорят правду. «Начать заново» обязано остаться: это
        // единственный способ перечитать пройденное с начала.
        [Test]
        public void ПройденнаяНовеллаСНулевойГлавойНеТеряетНачатьЗаново()
        {
            var t = Пилот();
            LvnProgress.StartChapter(t, t.ChaptersOf()[0]);
            LvnProgress.FinishChapter(t, null);

            Assert.IsNull(LvnProgress.Current(t), "у дочитанной новеллы точки нет — иначе проверка ни о чём");

            var карточка = Карточка(Пилот());

            Assert.IsTrue(ЕстьНадпись(карточка, НачатьЗаново),
                "пройденную новеллу нечем перечитать с начала — «Начать заново» пропало");
        }

        // Карточку открывают и до того, как хост посадил на неё новеллу (первый
        // кадр перехода). Урони это сборку — игрок не получит экран вовсе.
        [Test]
        public void КарточкаБезНовеллыСтроитсяИНичегоНеПредлагает()
        {
            VisualElement карточка = null;
            Assert.DoesNotThrow(() => карточка = Карточка(null),
                "карточка без новеллы уронила сборку — экран не откроется вообще");
            Assert.IsFalse(ЕстьНадпись(карточка, НачатьЗаново),
                "карточка без новеллы предлагает стереть прогресс, которого не знает");
        }
    }
}
