using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ВИТРИНА ОТВЕЧАЕТ ОДИНАКОВО, КАКОЙ БЫ ОНА НИ БЫЛА.
    ///
    /// <para>Витрин две — карусель и хаб, — а вопрос к ним один: какую новеллу
    /// выбрал игрок. Пока у каждой был свой способ ответить, оболочка знала их
    /// устройство: подписывалась на событие карусели, снимала подписку, разбирала
    /// защёлку, переводила НОМЕР карточки в новеллу. И тут же разошлись мелочи:
    /// отмена у одной кидала исключение, у другого возвращала null.</para>
    ///
    /// <para>Дороже всего стоила ссылка <c>?title=…</c>: запрос защёлкивался в
    /// карусели, а ждал ответа хаб. В режиме хаба (то есть в живом продукте)
    /// ссылка не делала НИЧЕГО и рапортовала об успехе.</para>
    ///
    /// <para>Поэтому проверки ниже написаны на интерфейс: каждая гоняется по
    /// обеим витринам, и «работает у карусели, не работает у хаба» больше не
    /// проходит.</para>
    /// </summary>
    public sealed class BrowseTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
                => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
                => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private static List<LvnTitle> Новеллы() => new List<LvnTitle>
        {
            new LvnTitle { id = "a", name = "Первая" },
            new LvnTitle { id = "b", name = "Вторая" },
        };

        private static ILvnBrowse Карусель()
            => new TitleCarousel(Новеллы(), null, new NoAssets());

        private static ILvnBrowse Хаб()
        {
            var h = new BrowseHub(null, new NoAssets());
            h.SetData(null, Новеллы());
            return h;
        }

        private static IEnumerable<TestCaseData> Витрины()
        {
            yield return new TestCaseData((System.Func<ILvnBrowse>)Карусель).SetName("карусель");
            yield return new TestCaseData((System.Func<ILvnBrowse>)Хаб).SetName("хаб");
        }

        // Ссылка «?title=b», нажатая ДО того, как витрина взяла управление
        // (заставка ещё висит): запрос ждёт в защёлке и срабатывает первым же
        // вопросом. Это и есть тот случай, который в режиме хаба молча пропадал.
        [TestCaseSource(nameof(Витрины))]
        public async Task ЗапросДоПоказаЗащёлкиваетсяИОткрываетНовеллу(System.Func<ILvnBrowse> собрать)
        {
            var витрина = собрать();

            Assert.IsTrue(витрина.RequestTitle("b"), "витрина не приняла запрос на существующую новеллу");
            var выбор = await витрина.PickTitleAsync(CancellationToken.None);

            Assert.NotNull(выбор, "защёлкнутый запрос пропал — ссылка «работает», но не открывает ничего");
            Assert.AreEqual("b", выбор.id, "открылась не та новелла, которую просила ссылка");
        }

        // «Такой новеллы нет» — это ответ false, а не тихое согласие. На нём
        // держится предупреждение в логе: без него ошибка в ссылке выглядит как
        // успех, и об опечатке никто не узнает.
        [TestCaseSource(nameof(Витрины))]
        public void ЗапросНесуществующейНовеллыОтвечаетОтказом(System.Func<ILvnBrowse> собрать)
        {
            var витрина = собрать();

            Assert.IsFalse(витрина.RequestTitle("нет-такой"), "витрина согласилась открыть несуществующую новеллу");
            Assert.IsFalse(витрина.RequestTitle(null), "пустой id принят за новеллу");
        }

        // Отмена витка (выход из приложения, пересборка) — это «никого не
        // выбрали», а не поломка. Одно правило на обе витрины: раньше карусель
        // кидала исключение, а хаб возвращал null.
        [TestCaseSource(nameof(Витрины))]
        public async Task ОтменаВозвращаетПустойВыборАНеИсключение(System.Func<ILvnBrowse> собрать)
        {
            var витрина = собрать();
            var отмена = new CancellationTokenSource();

            var ждём = витрина.PickTitleAsync(отмена.Token);
            отмена.Cancel();
            var выбор = await ждём;

            Assert.IsNull(выбор, "отменённая витрина ответила выбором");
        }

        // Поверхность витрины — то, что оболочка показывает и прячет. Без неё
        // цикл снова начал бы звать экраны по именам.
        [TestCaseSource(nameof(Витрины))]
        public void УВитриныЕстьПоверхность(System.Func<ILvnBrowse> собрать)
        {
            Assert.NotNull(собрать().View, "витрина без поверхности — оболочке нечего показывать");
        }

        // Свежий манифест доезжает до витрины через общий шов: до него оболочка
        // называла обе по именам и обновляла каждую по-своему.
        [TestCaseSource(nameof(Витрины))]
        public async Task СвежийМанифестМенятСоставВитрины(System.Func<ILvnBrowse> собрать)
        {
            var витрина = собрать();

            витрина.SetContent(new LvnManifest
            {
                titles = new List<LvnTitle> { new LvnTitle { id = "c", name = "Третья" } },
            });

            Assert.IsFalse(витрина.RequestTitle("a"), "витрина всё ещё знает новеллу, которой в манифесте нет");
            Assert.IsTrue(витрина.RequestTitle("c"), "новеллу из свежего манифеста витрина не увидела");
            var выбор = await витрина.PickTitleAsync(CancellationToken.None);
            Assert.AreEqual("c", выбор?.id, "открылась не та новелла");
        }

        // Вход по ссылке идёт ТЕМИ ЖЕ воротами, что и «Играть» на карточке: у
        // хаба на них висит списание входа в новеллу. Ссылка мимо кассы — дыра
        // в экономике, которую легко не заметить: снаружи всё «работает».
        [Test]
        public async Task ЗапросВХабПроходитЧерезВоротаОплаты()
        {
            var хаб = new BrowseHub(null, new NoAssets());
            хаб.SetData(null, Новеллы());
            int спросили = 0;
            хаб.OnPlay = t => { спросили++; return Task.FromResult(false); }; // не потянул

            Assert.IsTrue(хаб.RequestTitle("b"));
            var отмена = new CancellationTokenSource();
            var ждём = хаб.PickTitleAsync(отмена.Token);
            отмена.Cancel();
            var выбор = await ждём;

            Assert.AreEqual(1, спросили, "ссылка открыла новеллу мимо ворот входа");
            Assert.IsNull(выбор, "ворота отказали, а витрина всё равно ответила выбором");
        }
    }
}
