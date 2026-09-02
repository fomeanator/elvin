using NUnit.Framework;
using Lvn.Content;

namespace Lvn.Tests
{
    /// <summary>
    /// КОД ДЛЯ ВИДЕОКАРТЫ ИЩЕТСЯ У ЛЮБОЙ СТУПЕНИ КАЧЕСТВА.
    ///
    /// <para>Отображение «адрес картинки → адрес кода» было написано под ОДНУ
    /// ступень (<c>@2k</c>). Устройство, выбравшее другую (<c>@1440</c>),
    /// получало <c>null</c> и уходило на процессорную распаковку PNG — молча,
    /// до первой строки лога: адрес с уже проставленной ступенью
    /// <c>DownscaleVariant</c> не обрабатывает по устройству.</para>
    ///
    /// <para>Цена ошибки была не в скорости, а в том, что весь формат —
    /// сервер, клиент, пакет декодера — не работал НИ РАЗУ и никак этого не
    /// показывал. Проверка стоит здесь, чтобы «одна ступень» не вернулась
    /// незаметно.</para>
    /// </summary>
    public class Ktx2UrlTests
    {
        [TestCase("/content/sprites/hill/body_west@1440.png", "/content/sprites/hill/body_west@1440.ktx2")]
        [TestCase("/content/sprites/hill/body_west@2k.png",   "/content/sprites/hill/body_west@2k.ktx2")]
        [TestCase("/content/bg/room@1080.jpg",                "/content/bg/room@1080.ktx2")]
        public void СтупеньСохраняется(string url, string ждём)
            => Assert.AreEqual(ждём, ContentLoader.Ktx2UrlFor(url));

        [Test]
        public void БезСтупениАдресСтроитсяЧерезВариант()
        {
            var got = ContentLoader.Ktx2UrlFor("/content/bg/room.jpg");
            Assert.IsNotNull(got, "крупный арт без ступени обязан получить код через свой вариант");
            StringAssert.EndsWith(".ktx2", got);
            StringAssert.StartsWith("/content/bg/room@", got);
        }

        [TestCase("/content/ui/panel.png")]
        [TestCase("/content/pixel/tile.png")]
        [TestCase("/content/scripts/ch1.lvn")]
        [TestCase("")]
        [TestCase(null)]
        public void ЧужоеОстаётсяНаОбычномПути(string url)
            => Assert.IsNull(ContentLoader.Ktx2UrlFor(url),
                "мелкий арт, скины и не-картинки кодом для видеокарты не показываются");

        /// <summary>
        /// КРОШКА-ЗАГОТОВКА ЖИВЁТ РАСТРОМ — И ЭТО РЕШЕНИЕ, А НЕ НЕДОРАБОТКА.
        ///
        /// <para>@mini показывают, пока едет крупный арт: весь её смысл в том,
        /// чтобы появиться мгновенно. Кода ей не собирают нигде — ни прогрев,
        /// ни ленивый тракт, — а растрового пути у арта истории нет. Попроси
        /// показ код для крошки, и вместо мгновенной заготовки получится 7.5 с
        /// ожидания того, чего не будет, и пустое место после них.</para>
        ///
        /// <para>Правило пришло не отсюда: исключения наследовались от
        /// уменьшителя, через который проходил каждый адрес. Стоило пропустить
        /// уменьшитель для адреса, уже несущего ступень, — и они ушли вместе с
        /// ним, молча.</para>
        /// </summary>
        [TestCase("/content/sprites/hill/body_west@mini.png")]
        [TestCase("/content/bg/room@mini.jpg")]
        [TestCase("/content/ui/panel@2k.png")]
        [TestCase("/content/pixel/tile@1440.png")]
        public void РастровыйПоУмыслуКодаНеПросит(string url)
            => Assert.IsNull(ContentLoader.Ktx2UrlFor(url),
                "попросили код у того, кому его нарочно не собирают: "
                + "показ встанет на 7.5 с и не покажет ничего");

        /// <summary>Тот же список, спрошенный у дома напрямую: ступень сама по
        /// себе кода не отменяет — отменяет только вид арта.</summary>
        [TestCase("/content/sprites/hill/body@1440.png", true)]
        [TestCase("/content/bg/room.jpg", true)]
        [TestCase("/content/bg/room@mini.jpg", false)]
        [TestCase("/content/ui/panel.png", false)]
        [TestCase("/content/pixel/tile.png", false)]
        public void ДомЗнаетКомуПоложенКод(string url, bool положен)
            => Assert.AreEqual(положен, DownloadPolicy.CodedArt(url));
    }
}
