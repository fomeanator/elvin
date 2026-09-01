using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ОДИН РАЗ НА АДРЕС — <see cref="LvnOnce{T}"/>.
    ///
    /// <para>Проверяется не кэш (кэш очевиден), а ВТОРАЯ половина правила:
    /// два одновременных запроса одного адреса делают работу ОДИН раз.
    /// Проигравший незакрытую гонку перезаписывал запись и навсегда терял чужую
    /// текстуру — она оставалась в видеопамяти без единой ссылки. Утечка тихая,
    /// растёт с каждым совпадением и видна только как «под конец сессии всё
    /// тормозит».</para>
    /// </summary>
    public class OnceTests
    {
        private sealed class Box { public string Name; }

        [Test]
        public async Task Два_одновременных_запроса_делают_работу_один_раз()
        {
            var once = new LvnOnce<Box>();
            int работ = 0;
            var ворота = new TaskCompletionSource<bool>();

            async Task<Box> Медленно()
            {
                работ++;
                await ворота.Task;
                return new Box { Name = "готово" };
            }

            var первый = once.GetAsync("a", Медленно);
            var второй = once.GetAsync("a", Медленно);
            ворота.SetResult(true);
            var a = await первый;
            var b = await второй;

            Assert.AreEqual(1, работ, "работу сделали дважды — один результат потерян вместе с памятью");
            Assert.AreSame(a, b, "просящие получили РАЗНЫЕ предметы: один из них уже никто не освободит");
        }

        [Test]
        public async Task Готовое_отдаётся_без_работы()
        {
            var once = new LvnOnce<Box>();
            int работ = 0;
            Task<Box> Раз() { работ++; return Task.FromResult(new Box()); }

            await once.GetAsync("a", Раз);
            await once.GetAsync("a", Раз);

            Assert.AreEqual(1, работ, "готовое пересобрали заново");
            Assert.IsTrue(once.Has("a"));
        }

        [Test]
        public async Task Неудачу_не_запоминаем()
        {
            // Пустой ответ означает «сейчас не вышло», а не «этого нет».
            // Запомнив его, мы отняли бы у файла все будущие попытки — именно
            // так выглядит арт, который однажды не догрузился и больше не
            // появился.
            var once = new LvnOnce<Box>();
            int работ = 0;
            Task<Box> Сначала_нет() => Task.FromResult(++работ == 1 ? null : new Box());

            Assert.IsNull(await once.GetAsync("a", Сначала_нет));
            Assert.IsFalse(once.Has("a"), "неудача осела в готовом");
            Assert.IsNotNull(await once.GetAsync("a", Сначала_нет), "вторая попытка не состоялась");
        }

        [Test]
        public async Task Чужой_срыв_читается_как_промах_а_не_как_свой_сбой()
        {
            // Отмена принадлежит ТОМУ, кто начал: игрок вышел из его экрана, а
            // не из нашего. Для второго просящего это обычный промах.
            var once = new LvnOnce<Box>();
            var ворота = new TaskCompletionSource<Box>();
            var первый = once.GetAsync("a", () => ворота.Task);
            var второй = once.GetAsync("a", () => Task.FromResult(new Box()));

            ворота.SetException(new System.OperationCanceledException());
            try { await первый; } catch { /* свой срыв — свой */ }

            Assert.IsNull(await второй, "чужой срыв прилетел вторым исключением вместо промаха");
            Assert.IsFalse(once.Has("a"));
        }

        [Test]
        public async Task Пустой_адрес_ничего_не_делает()
        {
            var once = new LvnOnce<Box>();
            int работ = 0;
            Assert.IsNull(await once.GetAsync("", () => { работ++; return Task.FromResult(new Box()); }));
            Assert.IsNull(await once.GetAsync(null, () => { работ++; return Task.FromResult(new Box()); }));
            Assert.AreEqual(0, работ, "пустой адрес пошёл в работу");
        }

        [Test]
        public async Task Место_освобождается_после_срыва()
        {
            // Сорвавшаяся работа не должна оставить адрес «вечно в полёте»:
            // следующий просящий встал бы в очередь за задачей, которой нет.
            var once = new LvnOnce<Box>();
            try { await once.GetAsync("a", () => Task.FromException<Box>(new System.Exception("сорвалось"))); }
            catch { }
            var снова = await once.GetAsync("a", () => Task.FromResult(new Box { Name = "второй" }));
            Assert.AreEqual("второй", снова?.Name, "адрес остался в полёте после срыва");
        }
    }
}
