using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// КАКОЙ АДРЕС СКАЧАЕТСЯ НА САМОМ ДЕЛЕ. Правило «показ берёт крупный арт
    /// уменьшённым вариантом» стояло пятью копиями в четырёх фазах загрузки, и
    /// расходились они молча: фаза, забывшая уменьшение, качает полноразмерный
    /// файл, показ просит другой адрес — арт лежит на диске дважды, а «уже
    /// скачано» не срабатывает.
    /// </summary>
    public class DownloadVariantTests
    {
        private string _suffix;

        [SetUp]
        public void SetUp() { _suffix = DownloadPolicy.PreferredSuffix; DownloadPolicy.PreferredSuffix = "@2k"; }

        [TearDown]
        public void TearDown() => DownloadPolicy.PreferredSuffix = _suffix;

        [Test]
        public void СпрайтКачаетсяУменьшеннымАФонКакЕсть()
        {
            Assert.AreEqual("/art/hero@2k.png", DownloadPolicy.Effective("sprite", "/art/hero.png"));
            Assert.AreEqual("/bg/room.jpg", DownloadPolicy.Effective("bg", "/bg/room.jpg"),
                "фон и звук берут исходный адрес");
            Assert.AreEqual("/audio/theme.ogg", DownloadPolicy.Effective("audio", "/audio/theme.ogg"));
        }

        [Test]
        public void УАссетаБезВариантаАдресОстаётсяИсходным()
        {
            // Effective обязан вернуть ЧТО-ТО: иначе фаза скачает null.
            Assert.AreEqual("/ui/frame.png", DownloadPolicy.Effective("sprite", "/ui/frame.png"));
            Assert.AreEqual("/art/hero.svg", DownloadPolicy.Effective("sprite", "/art/hero.svg"));
        }

        [Test]
        public void РучкаКачестваМеняетТоЧтоСкачается()
        {
            DownloadPolicy.PreferredSuffix = "@1k";
            Assert.AreEqual("/art/hero@1k.png", DownloadPolicy.Effective("sprite", "/art/hero.png"),
                "экономия трафика обязана доходить до самой закачки, а не только до показа");
        }

        [Test]
        public void ПиксельныйАртИИнтерфейсУменьшатьНельзя()
        {
            // Пиксель-арт после даунскейла — каша, а скин интерфейса рисуется
            // девятислойкой по точным полям.
            Assert.IsNull(DownloadPolicy.DownscaleVariant("/pixel/tiles.png"));
            Assert.IsNull(DownloadPolicy.DownscaleVariant("/ui/frame.png"));
        }

        [Test]
        public void ПапкиУзнаютсяВЛюбомРегистре()
        {
            // Урок «/Art/Hero.PNG» был усвоен у LargeStoryArt и не дошёл до
            // соседей в том же файле: /UI/ считался артом истории (растр
            // запрещён), а /Pixel/ получал уменьшенный вариант, который его
            // размазывает. Сервер приводит путь к нижнему регистру — клиент
            // обязан отвечать так же.
            Assert.IsNull(DownloadPolicy.DownscaleVariant("/Pixel/tiles.png"));
            Assert.IsNull(DownloadPolicy.DownscaleVariant("/UI/frame.png"));
            Assert.IsFalse(DownloadPolicy.CodedArt("/Pixel/tiles.png"), "пиксель-арту код не положен, как бы ни писалась папка");
            Assert.IsFalse(DownloadPolicy.RasterForbidden("/UI/menu-canvas.jpg"), "обшивке растр разрешён и с заглавной");
            Assert.IsTrue(DownloadPolicy.RasterForbidden("/Art/hero.png"), "арту истории растр запрещён и с заглавной");
        }

        [Test]
        public void ВариантНаВариантНеВешают()
        {
            Assert.IsNull(DownloadPolicy.DownscaleVariant("/art/hero@2k.png"),
                "«hero@2k@2k.png» на сервере не лежит");
        }

        [Test]
        public void НеРастрВариантаНеИмеет()
        {
            Assert.IsNull(DownloadPolicy.DownscaleVariant("/art/hero.ktx2"));
            Assert.IsNull(DownloadPolicy.DownscaleVariant("/art/hero"));
            Assert.IsNull(DownloadPolicy.DownscaleVariant(null));
        }

        [Test]
        public void УменьшаютсяТолькоПапкиСАртом()
        {
            Assert.IsNull(DownloadPolicy.DownscaleVariant("/misc/thing.png"));
            foreach (var folder in new[] { "/bg/", "/art/", "/sprites/", "/spine/" })
                Assert.IsNotNull(DownloadPolicy.DownscaleVariant(folder + "x.png"), folder);
        }

        [Test]
        public void КрошкаВсегдаPNG()
        {
            // Живой скрин «одни вешалки»: при ktx2-тракте крошка наследовала
            // «.ktx2», которого сервер для неё не кодирует, — сплошные 404.
            DownloadPolicy.PreferredSuffix = "@2k";
            var mini = DownloadPolicy.MiniVariant("/sprites/hill.png");
            Assert.AreEqual("/sprites/hill@mini.png", mini);
            Assert.IsNull(DownloadPolicy.MiniVariant("/ui/frame.png"), "у чего нет варианта — нет и крошки");
        }

        [Test]
        public void СнятиеВариантаВозвращаетИсходноеИмяЛюбогоБокса()
        {
            foreach (var v in DownloadPolicy.Variants)
                Assert.AreEqual("/art/hero.png", DownloadPolicy.StripVariant("/art/hero" + v + ".png"), v);
            Assert.AreEqual("/art/hero.png", DownloadPolicy.StripVariant("/art/hero.png"));
            Assert.IsNull(DownloadPolicy.StripVariant(null));
        }

        [Test]
        public void КрошкаНеУчаствуетВВыбореКачества()
        {
            // «@mini» — заготовка проявления, а не ступень качества: попав в
            // список, она стала бы выбором игрока в настройках.
            CollectionAssert.DoesNotContain(DownloadPolicy.QualityVariants, "@mini");
            CollectionAssert.Contains(DownloadPolicy.Variants, "@mini");
            CollectionAssert.Contains(DownloadPolicy.Variants, DownloadPolicy.DisplayVariant);
        }

        [Test]
        public void НавеситьВариантМожноТолькоНаРастр()
        {
            Assert.AreEqual("/bg/room@1k.jpg", DownloadPolicy.WithVariant("/bg/room.jpg", "@1k"));
            Assert.AreEqual("/bg/room@1k.jpeg", DownloadPolicy.WithVariant("/bg/room.jpeg", "@1k"));
            Assert.IsNull(DownloadPolicy.WithVariant("/bg/room.ktx2", "@1k"));
            Assert.IsNull(DownloadPolicy.WithVariant("/bg/room", "@1k"));
            Assert.IsNull(DownloadPolicy.WithVariant(null, "@1k"));
            Assert.IsNull(DownloadPolicy.WithVariant("/bg/room.jpg", null));
        }

        [Test]
        public void ВариантНавешиваетсяДоРасширенияАНеПослеНего()
        {
            // «room.jpg@1k» сервер не отдаст, а расширение перестанет быть
            // расширением — см. LvnUrl.Extension.
            var url = DownloadPolicy.WithVariant("/bg/room.jpg", "@1k");
            Assert.AreEqual("jpg", LvnUrl.Extension(url));
        }

        [Test]
        public void ОценкаНеизвестногоРазмераСкромная()
        {
            // Занизить — прогресс «замедляется» к концу, завысить — прыгает к
            // завершению; второе читается как обман.
            Assert.Greater(DownloadPolicy.UnknownSizeBytes, 0);
            Assert.Less(DownloadPolicy.UnknownSizeBytes, 1L << 20);
        }
    
        // УМОЛЧАНИЕ БОКСА — СОВЕТ УСТРОЙСТВА, А НЕ КОНСТАНТА.
        //
        // Прогрев витрины спрашивает адрес РАНЬШЕ, чем оболочка успевает
        // присвоить ступень. Пока умолчанием была константа «@2k», прогрев и
        // показ на устройстве полосы 1440 брали РАЗНЫЕ файлы: прогрев грел то,
        // чего показ не просил, а показ платил полную цену растра.
        [Test]
        public void DefaultBoxFollowsTheDeviceNotAConstant()
        {
            var was = DownloadPolicy.PreferredSuffix;
            DownloadPolicy.PreferredSuffix = null;   // как на самом старте
            try
            {
                var expected = DownloadPolicy.SuffixFor(Lvn.LvnDeviceProfile.RecommendedArtQuality());
                StringAssert.EndsWith(expected + ".png",
                    DownloadPolicy.DownscaleVariant("/sprites/hill/body_west.png"),
                    "без присваивания бокс обязан совпадать с советом устройства — " +
                    "иначе прогрев и показ берут разные файлы");
            }
            finally { DownloadPolicy.PreferredSuffix = was; }
        }
    }
}
