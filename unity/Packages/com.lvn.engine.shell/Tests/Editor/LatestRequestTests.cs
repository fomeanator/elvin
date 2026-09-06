using System.Collections.Generic;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    public sealed class LatestRequestTests
    {
        [Test]
        public void LateRequestCannotOverwritePublishedLatest()
        {
            var requests = new LatestRequest();
            var first = requests.Begin();
            var second = requests.Begin();
            var published = new List<string>();

            Assert.IsTrue(requests.TryPublish(second, () => published.Add("B")));
            // Медленная загрузка вернулась после новой: видимый выбор
            // должен остаться прежним, а не зависеть от скорости сети.
            Assert.IsFalse(requests.TryPublish(first, () => published.Add("A")));
            CollectionAssert.AreEqual(new[] { "B" }, published);
        }

        [Test]
        public void LatestRequestPublishes()
        {
            var requests = new LatestRequest();
            var request = requests.Begin();
            var published = new List<string>();

            Assert.IsTrue(requests.TryPublish(request, () => published.Add("A")));
            CollectionAssert.AreEqual(new[] { "A" }, published);
        }

        [Test]
        public void CompletedPublicationAllowsNewRequest()
        {
            var requests = new LatestRequest();
            var first = requests.Begin();
            var published = new List<string>();
            Assert.IsTrue(requests.TryPublish(first, () => published.Add("A")));

            var second = requests.Begin();
            Assert.Greater(second, first);
            Assert.IsFalse(requests.TryPublish(first, () => published.Add("A")));
            Assert.IsTrue(requests.TryPublish(second, () => published.Add("B")));
            CollectionAssert.AreEqual(new[] { "A", "B" }, published);
        }

        [Test]
        public void ConsecutiveRequestsAllowOnlySecondToPublish()
        {
            var requests = new LatestRequest();
            var first = requests.Begin();
            var second = requests.Begin();
            var published = new List<string>();

            // Даже пока новая загрузка ждёт, прежняя уже не вправе
            // показывать результат: игрок успел выбрать другое.
            Assert.IsFalse(requests.TryPublish(first, () => published.Add("A")));
            CollectionAssert.IsEmpty(published);
            Assert.IsTrue(requests.TryPublish(second, () => published.Add("B")));
            CollectionAssert.AreEqual(new[] { "B" }, published);
        }
    }
}
