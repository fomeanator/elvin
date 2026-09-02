using NUnit.Framework;
using Lvn.Content;

namespace Lvn.Tests
{
    /// <summary>
    /// АДРЕС С НЕПОДСТАВЛЕННОЙ ОСЬЮ НЕ УХОДИТ В СЕТЬ.
    ///
    /// <para>Адреса слоёв в каталоге — шаблоны (<c>hair_{hairstyle}_{hair}.png</c>),
    /// значения осей подставляют в момент показа. Правило «такой адрес не
    /// качаем» было записано в доме списков и применялось в ОДНОМ списке из
    /// семи. А живой случай 02.09 рождается уже ПОСЛЕ подстановки: гардероб
    /// перебирал причёски, подставлял одну ось и оставлял вторую —
    /// <c>hair_rose_{hair}.png</c>. Никакой список такое не отфильтрует.</para>
    ///
    /// <para>Поэтому правило спрашивают у двери загрузчика. Здесь проверяется
    /// само правило: частичная подстановка — тоже шаблон.</para>
    /// </summary>
    public class TemplateUrlTests
    {
        [TestCase("/content/sprites/hill/hair_{hairstyle}_{hair}.png")]
        [TestCase("/content/sprites/hill/hair_{hairstyle}_brunette.png")]  // подставлена вторая
        [TestCase("/content/sprites/hill/hair_rose_{hair}.png")]           // подставлена первая
        [TestCase("/content/bg/{room}.jpg")]
        public void ШаблонУзнаётсяДажеЧастичный(string url)
            => Assert.IsTrue(DownloadPolicy.IsTemplate(url),
                "адрес с оставшейся осью — гарантированный 404 и потраченное ожидание");

        [TestCase("/content/sprites/hill/hair_rose_brunette.png")]
        [TestCase("/content/sprites/hill/hair_rose_brunette@1440.png")]
        [TestCase("/content/ui/panel.png")]
        [TestCase("")]
        [TestCase(null)]
        public void ЖивойАдресШаблономНеСчитается(string url)
            => Assert.IsFalse(DownloadPolicy.IsTemplate(url));
    }
}
