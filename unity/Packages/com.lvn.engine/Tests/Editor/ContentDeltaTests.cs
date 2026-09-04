using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// РАЗНИЦА ВМЕСТО ПЕРЕКАЧКИ.
    ///
    /// <para>Замер на живом проекте 04.09: карта версий 282 КБ, манифест
    /// 435 КБ. Правка одной реплики меняет хеш её скрипта, значит и общую
    /// версию, — и клиент забирал 717 КБ ради изменения в сотню байт. Живое
    /// обновление упиралось не в частоту опроса, а в цену ответа.</para>
    ///
    /// <para>Здесь проверяется клиентская половина: наложение разницы на карту
    /// версий и решение «идти ли за каталогом».</para>
    /// </summary>
    public class ContentDeltaTests
    {
        private static ContentLoader Loader()
            => new ContentLoader("http://127.0.0.1:9/",
                   System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                       "lvn-delta-" + System.Guid.NewGuid().ToString("N")));

        [Test]
        public void НаложениеСчитаетТолькоНастоящиеПравки()
        {
            using var loader = Loader();
            var n = loader.ApplyVersionDelta(
                new Dictionary<string, string> { ["scripts/ch1.lvn"] = "aaa" },
                new List<string> { "bg/gone.jpg" });
            Assert.AreEqual(2, n, "правка и удаление — две записи");
        }

        [Test]
        public void ПустаяРазницаНеРаботаЕслиНичегоНеПришло()
        {
            using var loader = Loader();
            Assert.AreEqual(0, loader.ApplyVersionDelta(null, null));
            Assert.AreEqual(0, loader.ApplyVersionDelta(
                new Dictionary<string, string>(), new List<string>()),
                "пустая разница подняла работу на пустом месте");
        }

        // КАТАЛОГ НЕ МЕНЯЛСЯ — ЗА НИМ НЕ ИДЁМ. Это вся экономия: 435 КБ
        // манифеста не качаются, если правили реплику, а не структуру.
        [Test]
        public void ПравкаРепликиНеТребуетКаталога()
        {
            var d = new ContentSync.Delta();
            d.Changed["scripts/ch1.lvn"] = "aaa";
            Assert.IsFalse(d.ManifestChanged,
                "за каталогом пошли из-за правки реплики — экономии не будет");
        }

        [Test]
        public void ПравкаКаталогаТребуетКаталога()
        {
            var d = new ContentSync.Delta();
            d.Changed[ContentSync.ManifestKey] = "bbb";
            Assert.IsTrue(d.ManifestChanged, "каталог сменился, а за ним не пошли");
        }

        // «ЗАБИРАЙ ВСЁ» ОБЯЗАНО ВЕСТИ ЗА КАТАЛОГОМ. Иначе клиент, проспавший
        // неделю, останется на вчерашней структуре, и это выглядело бы как
        // экономия, а было бы потерей контента.
        [Test]
        public void ЗабиратьВсёВедётЗаКаталогом()
        {
            var d = new ContentSync.Delta { Full = true };
            Assert.IsTrue(d.ManifestChanged,
                "на «забирай всё» не пошли за каталогом — клиент застрянет на вчерашней структуре");
        }
    }
}
