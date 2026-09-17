using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПАРК СОБРАННЫХ СЦЕН (TR-141). Снятый живой фон не сносится, а ждёт на
    /// паузе; самая давняя сцена сверх вместимости уходит. Ошибка здесь —
    /// либо пересборка на каждую вкладку (лаги), либо утечка текстур во весь
    /// экран.
    /// </summary>
    public class SpineParkTests
    {
        [Test]
        public void ПаркОтдаётСвоёИСноситСамоеДавнееСверхВместимости()
        {
            var park = new LvnSpineBackdrop.Park(2);
            var a = new LvnSpineBackdrop.Handle();
            var b = new LvnSpineBackdrop.Handle();
            var c = new LvnSpineBackdrop.Handle();
            park.Put("a", a); park.Put("b", b);
            Assert.IsTrue(a.Paused && b.Paused, "в парке сцена стоит на паузе");
            park.Put("c", c);
            Assert.IsTrue(a.Released, "третья сцена вытесняет самую давнюю");
            Assert.IsNull(park.Take("a"), "снесённую не отдаём");
            Assert.AreSame(b, park.Take("b"), "живую отдаём — и она уходит из парка");
            Assert.IsNull(park.Take("b"), "второй раз той же нет");
            park.Clear();
            Assert.IsTrue(c.Released, "очистка сносит всё, что осталось");
        }

        [Test]
        public void СнесённуюИПустуюВПаркНеБерут()
        {
            var park = new LvnSpineBackdrop.Park(2);
            var dead = new LvnSpineBackdrop.Handle();
            dead.Release();
            park.Put("x", dead);
            Assert.IsNull(park.Take("x"));
            park.Put("", new LvnSpineBackdrop.Handle());
            Assert.IsNull(park.Take(""));
        }
    }
}
