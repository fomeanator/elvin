using NUnit.Framework;
using Lvn;

namespace Lvn.Tests
{
    /// <summary>
    /// БАЗОВЫЙ АДРЕС — <see cref="LvnUrl.Base"/>.
    ///
    /// <para>Правило в одну строку, и потому оно стояло девятью написаниями в
    /// четырёх кварталах. Забывший его получает <c>host//v1/…</c>: одни серверы
    /// отвечают, другие дают 404, и разницу видно только на чужом хосте — то
    /// есть не у того, кто писал.</para>
    /// </summary>
    public class UrlBaseTests
    {
        [Test]
        public void Хвостовая_косая_снимается()
        {
            Assert.AreEqual("https://x.example", LvnUrl.Base("https://x.example/"));
            Assert.AreEqual("https://x.example", LvnUrl.Base("https://x.example"));
        }

        [Test]
        public void Пустое_и_ничто_дают_пустую_строку()
        {
            Assert.AreEqual("", LvnUrl.Base(null), "склейка с null дала бы «null/v1/…»");
            Assert.AreEqual("", LvnUrl.Base(""));
        }

        [Test]
        public void Несколько_косых_снимаются_все()
        {
            Assert.AreEqual("https://x.example", LvnUrl.Base("https://x.example///"),
                "иначе одна из копий правила чинила бы только одну косую");
        }

        [Test]
        public void Склейка_с_путём_даёт_одну_косую()
        {
            Assert.AreEqual("https://x.example/v1/me", LvnUrl.Base("https://x.example/") + "/v1/me",
                "ради этого всё и затевалось: двойная косая — 404 на части серверов");
        }
    }
}
