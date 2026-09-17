using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СКЕЛЕТ НАЗЫВАЕТСЯ ОДНИМ АДРЕСОМ.
    ///
    /// <para>Spine — отраслевой стандарт с неизменным комплектом выгрузки:
    /// <c>имя.json</c>, <c>имя.atlas(.txt)</c> рядом и страницы, которые атлас
    /// называет сам. Раньше автор был обязан завести сущность в каталоге
    /// спрайтов и вписать туда четыре пути руками — плата за то, что выводится
    /// само. Теперь достаточно назвать файл скелета в самой команде.</para>
    ///
    /// <para>Проверяется вывод адресов: ошибка здесь означает 404 посреди
    /// сцены, а её автор увидит только на устройстве.</para>
    /// </summary>
    public class SpineOneUrlTests
    {
        [Test]
        public void ИзJsonВыводитсяАтласРядом()
        {
            var sp = LvnSpineRef.FromUrl("/content/spine/hero/hero.json");
            Assert.AreEqual("/content/spine/hero/hero.json", sp.json);
            Assert.AreEqual("/content/spine/hero/hero.atlas.txt", sp.atlas,
                "атлас обязан выводиться из имени скелета — иначе автор пишет его руками");
            // ЗАПАСНОЕ НАПИСАНИЕ — снятие хвоста, а не сосед по имени. Сосед
            // срезает последнее расширение и давал «hero.atlas.atlas»: адрес,
            // которого нет нигде. Прогон это поймал, и проверка осталась здесь.
            Assert.AreEqual("/content/spine/hero/hero.atlas",
                Lvn.UI.VnStage.AtlasWithoutTxt(sp.atlas),
                "второе написание атласа не выводится — комплект из Spine перестанет открываться");
            Assert.IsNull(Lvn.UI.VnStage.AtlasWithoutTxt("/s/a.atlas"),
                "у атласа без хвоста запасного имени быть не должно — пробовать нечего");
        }

        [Test]
        public void ПапкаЗначитКомплектВнутриНеё()
        {
            var sp = LvnSpineRef.FromUrl("/content/spine/hero/");
            Assert.AreEqual("/content/spine/hero/hero.json", sp.json,
                "папка не развернулась в комплект — а так выгружают чаще всего");
            Assert.AreEqual("/content/spine/hero/hero.atlas.txt", sp.atlas);
        }

        [Test]
        public void ДвоичныйСкелетТожеЗнаетСвойАтлас()
        {
            foreach (var url in new[] { "/s/a.skel", "/s/a.skel.bytes" })
                Assert.AreEqual("/s/a.atlas.txt", LvnSpineRef.FromUrl(url).atlas,
                    $"для {url} атлас выведен неверно");
        }

        [Test]
        public void ПодложкаИПроигрышПередаютсяКакЕсть()
        {
            var sp = LvnSpineRef.FromUrl("/s/a.json", "/s/back.jpg", "idle");
            Assert.AreEqual("/s/back.jpg", sp.bg);
            Assert.AreEqual("idle", sp.auto, "названная анимация не доехала — фигура встанет неподвижной");
        }

        [Test]
        public void ПустойАдресНеСоздаётПустогоСкелета()
        {
            Assert.IsNull(LvnSpineRef.FromUrl(null));
            Assert.IsNull(LvnSpineRef.FromUrl("   "));
            Assert.IsNull(LvnSpineRef.FromUrl("/"), "корень — это не комплект, имени в нём нет");
        }

        // ── ОДНА СУЩНОСТЬ: задник сцены — часть сцены (17.09) ──────────────

        [Test]
        public void ЗадникБерётсяИзМанифестаГдеБыСценуНиСобрали()
        {
            // Карточка новеллы строила спайн без задника («у агентства нет
            // фона, только эффект»), а полотно меню — с задником, переданным
            // руками. Теперь задник знает реестр, и его получает любой вход.
            LvnSpineRef.ForgetBackdrops();
            try
            {
                var m = new LvnManifest
                {
                    ui = new LvnUiConfig
                    {
                        browse = new BrowseConfig
                        {
                            canvas_options = new List<CanvasOption>
                            {
                                new CanvasOption { id = "garden", url = "/content/bg/menu/garden.jpg", spine = "/content/spine/garden/Garden.json" },
                            },
                        },
                    },
                };
                LvnSpineRef.LearnBackdrops(m);
                var byUrl = LvnSpineRef.FromUrl("/content/spine/garden/Garden.json");
                Assert.AreEqual("/content/bg/menu/garden.jpg", byUrl.bg, "FromUrl — задник из реестра");
                var resolved = LvnSpineRef.Resolve(null, "/content/spine/garden/Garden.json");
                Assert.AreEqual("/content/bg/menu/garden.jpg", resolved.bg, "Resolve — тот же задник");
                Assert.AreEqual("cover", resolved.fit, "сцена по адресу — во весь кадр, если посадку не назвали");
                var explicitBg = LvnSpineRef.Resolve(null, "/content/spine/garden/Garden.json", bg: "/content/bg/other.jpg");
                Assert.AreEqual("/content/bg/other.jpg", explicitBg.bg, "явный задник сильнее реестра");
                Assert.IsNull(LvnSpineRef.FromUrl("/content/spine/unknown/unknown.json").bg, "незнакомая сцена — без задника");
            }
            finally { LvnSpineRef.ForgetBackdrops(); }
        }

        [Test]
        public void КлючКаталогаДаётКопиюСущностиАНеЕёСамоё()
        {
            // Фигура из каталога знает свою посадку; переопределения не должны
            // портить общий экземпляр каталога.
            var catalog = new Dictionary<string, LvnSpriteEntity>
            {
                ["noel"] = new LvnSpriteEntity { kind = "spine", spine = new LvnSpineRef { json = "/content/spine/noel/noel.json", atlas = "/content/spine/noel/noel.atlas.txt", fit = "width" } },
            };
            var a = LvnSpineRef.Resolve(catalog, "noel");
            Assert.AreEqual("/content/spine/noel/noel.json", a.json);
            Assert.AreEqual("width", a.fit, "посадка фигуры из каталога остаётся её собственной");
            var b = LvnSpineRef.Resolve(catalog, "noel", play: "wave", fit: "cover");
            Assert.AreEqual("wave", b.auto);
            Assert.AreEqual("cover", b.fit);
            Assert.IsNull(catalog["noel"].spine.auto, "каталог не тронут переопределением");
            Assert.AreEqual("width", catalog["noel"].spine.fit);
            Assert.IsNull(LvnSpineRef.Resolve(catalog, "   "), "пустой ключ — нет сцены");
        }
    }
}
