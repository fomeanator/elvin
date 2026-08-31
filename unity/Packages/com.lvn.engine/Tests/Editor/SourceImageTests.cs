using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ИСХОДНАЯ КАРТИНКА — та, из которой контент делает другие
    /// (<see cref="DownloadPolicy.SplitSourceImage"/>).
    ///
    /// <para>Разложить адрес на «имя без расширения» и «расширение», а заодно
    /// ответить, картинка ли это вообще: у чужого расширения — звука, сценария,
    /// бандла, уже готового .ktx2 — вариантов и перекодировок не бывает.
    /// Проверка стояла ЧЕТЫРЕЖДЫ дословно (вариант качества, уменьшенный показ,
    /// ASTC, KTX2), а список расширений в ней один: забыть одну копию значило
    /// получить перекодировку, которая молча ничего не делает для половины
    /// арта.</para>
    ///
    /// <para>Главное правило дома — про РЕГИСТР, и ломается оно тихо. Сервер
    /// отдаёт файлы ровно так, как они лежат: «Hero.PNG» и «hero.png» для
    /// Linux — разные файлы. Мак разработчика разницы не видит, прод отвечает
    /// 404, и арт пропадает только у игроков.</para>
    /// </summary>
    public class SourceImageTests
    {
        private string _suffix;

        [SetUp]
        public void SetUp() { _suffix = DownloadPolicy.PreferredSuffix; DownloadPolicy.PreferredSuffix = "@2k"; }

        [TearDown]
        public void TearDown() => DownloadPolicy.PreferredSuffix = _suffix;

        // Расширение узнаётся без оглядки на регистр — «Hero.PNG» такая же
        // картинка, как «hero.png», — но ВОЗВРАЩАЕТСЯ оно родным. Приведи его к
        // нижнему, и собранный из кусков адрес станет именем, которого на
        // сервере нет: на Маке всё работает, у игрока пустое место вместо арта.
        [Test]
        public void РасширениеВозвращаетсяВРодномРегистре()
        {
            Assert.IsTrue(DownloadPolicy.SplitSourceImage("/content/art/Hero.PNG", out var stem, out var ext),
                "«Hero.PNG» не признан картинкой — опознание расширения не должно зависеть от регистра");
            Assert.AreEqual(".PNG", ext, "расширение приведено к нижнему регистру — такого файла на сервере нет");
            Assert.AreEqual("/content/art/Hero", stem, "имя без расширения потеряло свой регистр");

            Assert.AreEqual("/content/art/Hero@1k.PNG", DownloadPolicy.WithVariant("/content/art/Hero.PNG", "@1k"),
                "вариант собран с чужим регистром — сервер такого имени не знает");
            Assert.AreEqual("/art/Hero@2k.JPG", DownloadPolicy.DownscaleVariant("/art/Hero.JPG"),
                "уменьшенный показ собран с чужим регистром");

            Assert.IsTrue(DownloadPolicy.SplitSourceImage("/art/a.Jpeg", out _, out var jpeg));
            Assert.AreEqual(".Jpeg", jpeg, "смешанный регистр расширения тоже родной");
        }

        // Что НЕ картинка-исходник: звук, сценарий, бандл и уже закодированный
        // .ktx2 — из них вариантов не делают. .webp сюда же, и намеренно:
        // варианты кодирует lvnconv, а он ходит только по png/jpg/jpeg, так что
        // «@2k.webp» на сервере не существует. Объяви его исходником — и показ
        // пойдёт за адресом, которого нет.
        [Test]
        public void ЧужоеРасширениеИсходникомНеСчитается()
        {
            foreach (var url in new[] { "/audio/theme.ogg", "/art/hero.ktx2", "/packs/city.bundle",
                                        "/scripts/ch1.lvn", "/art/hero.webp", "/art/hero.svg" })
            {
                Assert.IsFalse(DownloadPolicy.SplitSourceImage(url, out var stem, out var ext), url);
                Assert.IsNull(stem, url + ": имя выдано у того, что картинкой не является");
                Assert.IsNull(ext, url + ": расширение выдано у того, что картинкой не является");
            }

            Assert.AreEqual("/art/hero.webp", DownloadPolicy.Effective("sprite", "/art/hero.webp"),
                "у чего нет вариантов, то качается как есть — а не по несуществующему адресу");
        }

        // Пустой адрес и адрес без точки приходят по-настоящему: ассет без
        // расширения (папка-раздатчик), незаполненное поле каталога, обложка,
        // которую автор не задал. Ответ «нет» обязан приходить с ПУСТЫМИ
        // именем и расширением: звонящий их складывает, и остаток от прошлого
        // разбора собрался бы в чужой адрес.
        [Test]
        public void БезТочкиИПустойАдресНеИсходники()
        {
            foreach (var url in new[] { "/content/art/hero", "", null })
            {
                Assert.IsFalse(DownloadPolicy.SplitSourceImage(url, out var stem, out var ext), url ?? "null");
                Assert.IsNull(stem, "имя выдано там, где расширения нет вовсе");
                Assert.IsNull(ext, "расширение выдано там, где его нет");
            }

            Assert.IsNull(DownloadPolicy.WithVariant("/content/art/hero", "@1k"),
                "варианту не на что навешиваться, а адрес всё равно собрали");
        }

        // Точка встречается в имени ПАПКИ — «/bg.v2/», «/art.old/» — и это не
        // расширение файла. Ищи мы первую точку вместо последней, «/bg.v2/room»
        // объявился бы картинкой с расширением «.v2/room», и вариант собрался
        // бы посреди пути.
        [Test]
        public void ТочкаВИмениПапкиРасширениемНеСчитается()
        {
            Assert.IsFalse(DownloadPolicy.SplitSourceImage("/content/bg.v2/room", out _, out _),
                "точка в имени папки принята за расширение файла");

            Assert.IsTrue(DownloadPolicy.SplitSourceImage("/content/bg.v2/room.png", out var stem, out var ext));
            Assert.AreEqual("/content/bg.v2/room", stem, "разбор откусил кусок пути, а не расширение");
            Assert.AreEqual(".png", ext);
        }

        // Имена на проде — кириллические и процент-кодированные (таких файлов
        // там сотни). Разбор обязан только ДЕЛИТЬ строку, ничего в ней не
        // трогая: раскодируй он «%D0%94» обратно в букву — и вариант попросят
        // по другому адресу, чем скачали; урони пробел или букву — и получится
        // имя, которого нет.
        [Test]
        public void КириллицаИПроцентыВИмениОстаютсяКакБыли()
        {
            Assert.IsTrue(DownloadPolicy.SplitSourceImage("/content/bg/Дом Платона.png", out var stem, out var ext));
            Assert.AreEqual("/content/bg/Дом Платона", stem, "кириллическое имя пережило разбор не целым");
            Assert.AreEqual(".png", ext);
            Assert.AreEqual("/content/bg/Дом Платона@2k.png", DownloadPolicy.DownscaleVariant("/content/bg/Дом Платона.png"),
                "вариант кириллического арта собран не тем именем");

            Assert.IsTrue(DownloadPolicy.SplitSourceImage("/content/bg/%D0%94%D0%BE%D0%BC.png", out var enc, out _));
            Assert.AreEqual("/content/bg/%D0%94%D0%BE%D0%BC", enc,
                "закодированное имя раскодировали при разборе — попросят не тот файл, что скачали");
            Assert.AreEqual("/content/bg/%D0%94%D0%BE%D0%BC@1k.png",
                DownloadPolicy.WithVariant("/content/bg/%D0%94%D0%BE%D0%BC.png", "@1k"));
        }
    }
}
