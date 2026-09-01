using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class DownloadPolicyTests
    {
        [Test]
        public void Classify_ScriptAndAudioWinOverPath()
        {
            // script/audio extension beats any folder bucket.
            Assert.AreEqual(AssetClass.Script, DownloadPolicy.Classify("/content/ui/ch1.lvn"));
            Assert.AreEqual(AssetClass.Audio, DownloadPolicy.Classify("/content/bg/theme.ogg"));
        }

        [Test]
        public void Classify_LoadingBeatsUi()
        {
            // /loading/ must be checked before /ui/ — a loading bg is a ChapterBg.
            Assert.AreEqual(AssetClass.ChapterBg, DownloadPolicy.Classify("/content/ui/loading/ch1.png"));
            Assert.AreEqual(AssetClass.Ui, DownloadPolicy.Classify("/content/ui/frame_top.png"));
        }

        [Test]
        public void Classify_FoldersAndFallback()
        {
            Assert.AreEqual(AssetClass.Cover, DownloadPolicy.Classify("/content/covers/novel1.png"));
            Assert.AreEqual(AssetClass.Actor, DownloadPolicy.Classify("/content/actors/mara.png"));
            Assert.AreEqual(AssetClass.SceneBg, DownloadPolicy.Classify("/content/bg/porch.jpg"));
            Assert.AreEqual(AssetClass.Other, DownloadPolicy.Classify("/content/misc/x.png"));
            Assert.AreEqual(AssetClass.Other, DownloadPolicy.Classify(null));
        }

        [Test]
        public void Classify_IgnoresQueryString()
        {
            Assert.AreEqual(AssetClass.SceneBg, DownloadPolicy.Classify("/content/bg/porch.jpg?v=abc123"));
            Assert.AreEqual(AssetClass.Script, DownloadPolicy.Classify("/content/scripts/ch1.lvn?v=deadbeef"));
        }

        [Test]
        public void Kind_MapsByExtension()
        {
            Assert.AreEqual(LvnParts.Sprite, DownloadPolicy.Kind("/a/b.png"));
            Assert.AreEqual(LvnParts.Sprite, DownloadPolicy.Kind("/a/b.webp"));
            Assert.AreEqual(LvnParts.Audio, DownloadPolicy.Kind("/a/b.ogg"));
            // Скрипт зовётся скриптом. Прежде здесь стояло «bin»: словарь
            // определителя не знал слова, на котором ветвится загрузчик, — и
            // рядом с каждым вызовом приходилось держать проверку-дублёр.
            Assert.AreEqual(LvnParts.Script, DownloadPolicy.Kind("/a/b.lvn"));
        }

        [Test]
        public void WarmToMemory_OnlyImmediateArt()
        {
            // Warm what the player sees right away; disk-only for chapter-scoped art.
            Assert.IsTrue(DownloadPolicy.WarmToMemory(AssetClass.Ui));
            Assert.IsTrue(DownloadPolicy.WarmToMemory(AssetClass.ChapterBg));
            Assert.IsTrue(DownloadPolicy.WarmToMemory(AssetClass.Cover));
            Assert.IsFalse(DownloadPolicy.WarmToMemory(AssetClass.Actor));
            Assert.IsFalse(DownloadPolicy.WarmToMemory(AssetClass.SceneBg));
            Assert.IsFalse(DownloadPolicy.WarmToMemory(AssetClass.Audio));
        }

        // «Это тот же самый арт?» спрашивают сид, дисковый кэш и выгрузка
        // главы. Перечень суффиксов, размазанный по вызовам, однажды разъедется
        // с тем, что реально кодирует сервер.
        [Test]
        public void StripVariant_BringsEveryEncodeBackToTheOriginal()
        {
            Assert.AreEqual("/content/bg/x.jpg", DownloadPolicy.StripVariant("/content/bg/x@2k.jpg"));
            Assert.AreEqual("/content/bg/x.jpg", DownloadPolicy.StripVariant("/content/bg/x@1440.jpg"));
            Assert.AreEqual("/content/bg/x.jpg", DownloadPolicy.StripVariant("/content/bg/x@1k.jpg"));
            Assert.AreEqual("/content/art/a.png", DownloadPolicy.StripVariant("/content/art/a@mini.png"));
            Assert.AreEqual("/content/bg/x.jpg", DownloadPolicy.StripVariant("/content/bg/x.jpg"),
                "исходник остаётся собой");
            Assert.IsNull(DownloadPolicy.StripVariant(null));
            CollectionAssert.Contains(DownloadPolicy.Variants, DownloadPolicy.PreferredSuffix,
                "бокс показа обязан быть среди известных вариантов — иначе кэш не узнает свой же файл");
        }

        [Test]
        public void NeededAtBoot_ExcludesChapterScopedArt()
        {
            Assert.IsTrue(DownloadPolicy.NeededAtBoot("/content/ui/frame.png"));
            Assert.IsTrue(DownloadPolicy.NeededAtBoot("/content/covers/n.png"));
            Assert.IsFalse(DownloadPolicy.NeededAtBoot("/content/actors/mara.png"));
            Assert.IsFalse(DownloadPolicy.NeededAtBoot("/content/bg/porch.jpg"));
        }

        [Test]
        public void ПервыйКадрЖдётТолькоИнтерфейсИПолотно()
        {
            // Запуск ждал ВЕСЬ набор меню — обложки всех новелл и фоны загрузки
            // всех глав, сотня мегабайт до первого окна. Первому кадру нужен
            // интерфейсный арт и полотно витрины; остальное догоняет фоном,
            // а витрина рисует заглушки.
            Assert.IsTrue(DownloadPolicy.NeededForFirstFrame("/content/ui/frame.png"));
            Assert.IsFalse(DownloadPolicy.NeededForFirstFrame("/content/covers/n.png"));
            Assert.IsFalse(DownloadPolicy.NeededForFirstFrame("/content/bg/ch1.jpg"));

            const string canvas = "/content/menu/canvas.jpg";
            Assert.IsTrue(DownloadPolicy.NeededForFirstFrame(canvas, canvas),
                "полотно витрины знает только манифест — по имени файла оно обычная картинка");
            Assert.IsFalse(DownloadPolicy.NeededForFirstFrame(canvas),
                "без манифеста это просто картинка, и ждать её незачем");
        }

        // ЧТО ЭТО ЗА ФАЙЛ — один ответ. Определителей было два: политика и
        // самодельная копия внутри планировщика. На знакомых расширениях они
        // совпадали, а на незнакомом расходились — планировщик считал такой
        // файл КАРТИНКОЙ и грел его как картинку.
        [Test]
        public void РодФайлаНазываетсяСловамиОписи()
        {
            Assert.AreEqual(LvnParts.Sprite, DownloadPolicy.Kind("/content/bg/a.png"));
            Assert.AreEqual(LvnParts.Sprite, DownloadPolicy.Kind("/content/bg/a.WEBP"));
            Assert.AreEqual(LvnParts.Audio, DownloadPolicy.Kind("/content/sfx/a.ogg"));
            Assert.AreEqual(LvnParts.Bin, DownloadPolicy.Kind("/content/fonts/a.ttf"),
                "незнакомое расширение — не картинка: греть его как картинку значит "
                + "распаковывать в текстуру то, что текстурой не является");
        }

        // Слово «скрипт» в словаре определителя завелось не для красоты: ровно
        // на нём ветвится загрузчик (текстом или байтами). Пока его не было,
        // рядом с каждым вызовом стояла проверка-дублёр.
        [Test]
        public void СкриптРодНазываетсяСкриптом()
        {
            Assert.AreEqual(LvnParts.Script, DownloadPolicy.Kind("/content/scripts/ch1.lvn"));
            Assert.AreEqual(LvnParts.Script, DownloadPolicy.Kind("/content/scripts/ch1.lvn?v=7"),
                "запрос за адресом не меняет род файла");
        }

        // АДРЕС СОСЕДА. Каталог перевода лежит рядом со скриптом: ch1.lvn →
        // ch1.ru.json. Пока хвост клеили к сырому адресу, версия в запросе
        // попадала в СЕРЕДИНУ имени — файла с таким именем нет, каталог не
        // находился, и глава молча оставалась на языке автора.
        [Test]
        public void АдресСоседаНеЛомаетсяОЗапрос()
        {
            Assert.AreEqual("/s/ch1.ru.json", LvnUrl.Sibling("/s/ch1.lvn", ".ru.json"));
            Assert.AreEqual("/s/ch1.ru.json?v=7", LvnUrl.Sibling("/s/ch1.lvn?v=7", ".ru.json"),
                "запрос обязан ехать в конец: в середине имени он даёт несуществующий файл");
            Assert.AreEqual("/s/ch1.ru.json", LvnUrl.Sibling("/s/ch1", ".ru.json"),
                "адрес без расширения — тоже адрес");
        }

        // ЗАПРОС ЗА АДРЕСОМ — вторая половина того же факта, что и «чистый
        // адрес»: где кончается путь. Обе половины жили врозь — дом знал
        // первую, разбор ссылки-диплинка писал вторую сам, — и якорь «#top»
        // помнил только один из них.
        [Test]
        public void ЗапросБерётсяБезЯкоря()
        {
            Assert.AreEqual("title=cold", LvnUrl.Query("lvn://open?title=cold"));
            Assert.AreEqual("title=cold", LvnUrl.Query("lvn://open?title=cold#top"),
                "якорь попал в запрос — разбор увидит лишний параметр и откроет не то");
            Assert.AreEqual("", LvnUrl.Query("lvn://open"), "запроса нет — и брать нечего");
            Assert.AreEqual("", LvnUrl.Query(null));
        }
    }
}
