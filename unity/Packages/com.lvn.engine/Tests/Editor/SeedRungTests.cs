using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СИД ОТДАЁТ СТУПЕНЬ НИЖЕ ЗАПРОШЕННОЙ — иначе он не отдаёт ничего.
    ///
    /// <para>Сид собирают НА СЕРВЕРЕ, не зная устройства, и потому он везёт
    /// нижнюю ступень (@1k). Устройство просит свою: телефон покрупнее —
    /// «@1440», планшет — «@2k». Пока совпадение искали точное, десять
    /// мегабайт в APK лежали мёртвым грузом, а телефон качал то же самое с
    /// сервера — и первый вход, ради которого сид и придуман, всё равно шёл в
    /// сеть.</para>
    ///
    /// <para>Половинок у этого правила две — здесь и в серверном экспорте
    /// (seedRungs). Разойдись они, файл из APK перестал бы находиться, и
    /// заметить это можно было бы только по трафику на живом телефоне.</para>
    /// </summary>
    public class SeedRungTests
    {
        private ContentLoader _loader;
        private MethodInfo _seedKey;

        [SetUp]
        public void SetUp()
        {
            _loader = new ContentLoader("http://127.0.0.1:9/",
                Path.Combine(Path.GetTempPath(), "lvn-seed-" + Guid.NewGuid().ToString("N")));
            _seedKey = typeof(ContentLoader).GetMethod("SeedKey",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(_seedKey, "SeedKey переименован — правило искать нечем");
        }

        [TearDown]
        public void TearDown() => _loader?.Dispose();

        private void Опись(params string[] entries)
        {
            var field = typeof(ContentLoader).GetField("_seedIndex",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "опись сида переименована");
            field.SetValue(_loader, new HashSet<string>(entries));
        }

        private string Ключ(string url) => (string)_seedKey.Invoke(_loader, new object[] { url });

        [Test]
        public void ТочноеСовпадениеСильнееВсего()
        {
            Опись("content/bg/x@1440.jpg", "content/bg/x@1k.jpg");
            Assert.AreEqual("content/bg/x@1440.jpg", Ключ("http://s/content/bg/x@1440.jpg"));
        }

        [Test]
        public void ПроситВышеПолучаетНиже()
        {
            Опись("content/art/герой@1k.png");
            Assert.AreEqual("content/art/герой@1k.png", Ключ("http://s/content/art/герой@1440.png"),
                "@1440 не принял @1k из APK — сид останется грузом, а телефон пойдёт в сеть");
            Assert.AreEqual("content/art/герой@1k.png", Ключ("http://s/content/art/герой@2k.png"));
            // Без ступени в адресе — тоже проситель: полотно витрины манифест
            // называет оригиналом, а в APK лежит ступенью.
            Assert.AreEqual("content/art/герой@1k.png", Ключ("http://s/content/art/герой.png"));
        }

        [Test]
        public void ВышеЗапрошенногоНеБерём()
        {
            Опись("content/art/герой@2k.png");
            Assert.IsNull(Ключ("http://s/content/art/герой@1k.png"),
                "взяли ступень КРУПНЕЕ просимой: лишний вес и лишний декод на слабом устройстве");
        }

        /// <summary>Спросили код для видеокарты — отдать вместо него PNG значит
        /// вернуть байты, которые вызвавший попытается разобрать как ktx2 и не
        /// сможет. Расширение не меняется никогда.</summary>
        [Test]
        public void РасширениеНеПодменяется()
        {
            Опись("content/art/герой@1k.png");
            Assert.IsNull(Ключ("http://s/content/art/герой@1440.ktx2"),
                "под видом кода для видеокарты отдали PNG — распаковка упадёт");
        }

        /// <summary>Метка версии в адресе — про кэш сервера, а не про
        /// содержимое: в описи сида её нет и быть не может, и адрес с ней
        /// промахивался мимо любого ключа.</summary>
        [Test]
        public void МеткаВерсииНеМешает()
        {
            Опись("content/bg/x@1k.jpg");
            Assert.AreEqual("content/bg/x@1k.jpg", Ключ("http://s/content/bg/x@1k.jpg?v=7"));
            Assert.AreEqual("content/bg/x@1k.jpg", Ключ("http://s/content/bg/x@1440.jpg?v=7"));
        }

        [Test]
        public void ЧегоНетВОписиТогоНетВовсе()
        {
            Опись("content/art/другой@1k.png");
            Assert.IsNull(Ключ("http://s/content/art/герой@1440.png"));
        }
    }
}
