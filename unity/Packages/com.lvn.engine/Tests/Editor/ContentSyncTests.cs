using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class ContentSyncTests
    {

        [Test]
        public void ParseVersion_ReadsVersionField()
        {
            Assert.AreEqual("abc123", ContentSync.ParseVersion("{\"version\":\"abc123\"}"));
        }

        [Test]
        public void ParseVersion_NullForGarbageOrMissing()
        {
            Assert.IsNull(ContentSync.ParseVersion(""));
            Assert.IsNull(ContentSync.ParseVersion(null));
            Assert.IsNull(ContentSync.ParseVersion("not json"));
            Assert.IsNull(ContentSync.ParseVersion("{\"other\":1}"));
        }

        [Test]
        public void FirstPoll_NotifiesWhenBootReconciliationIsEnabled()
        {
            string last = null;
            Assert.IsTrue(ContentSync.AdvanceVersion(ref last, "after-save", notifyOnFirst: true));
            Assert.AreEqual("after-save", last);
        }
        /// <summary>
        /// ТОЧКА ОТСЧЁТА СНЯТА В НАЧАЛЕ ЗАПУСКА — первый опрос СРАВНИВАЕТ.
        ///
        /// <para>Раньше первый опрос объявлял смену всегда, и глава, которая
        /// только началась, тут же переигрывалась заново — на КАЖДОМ запуске
        /// (живой трейс 04.09). Отменить объявление нельзя: правка, сделанная
        /// между забором главы и стартом опроса, иначе не доедет никогда.
        /// Ответ — точка отсчёта, снятая ДО забора контента.</para>
        /// </summary>
        [Test]
        public void СервёрТотЖе_ПерваяСверкаМолчит()
        {
            string last = "снято-в-начале-запуска";   // ContentSync.Baseline
            Assert.IsFalse(ContentSync.AdvanceVersion(ref last, "снято-в-начале-запуска", notifyOnFirst: false),
                "контент не менялся, а опрос объявил смену — глава переиграется на ровном месте");
        }

        /// <summary>Правка, доехавшая по дороге, по-прежнему поднимает флаг:
        /// точка отсчёта снята ДО забора контента, значит расхождение с ней и
        /// есть «нас обогнали».</summary>
        [Test]
        public void СервёрСменилсяПоДороге_ПерваяСверкаГоворит()
        {
            string last = "снято-в-начале-запуска";
            Assert.IsTrue(ContentSync.AdvanceVersion(ref last, "правка-автора", notifyOnFirst: false),
                "контент сменился между началом запуска и первым опросом, а перезагрузки нет");
        }


        [Test]
        public void FirstPoll_DefaultOnlyEstablishesBaseline()
        {
            string last = null;
            Assert.IsFalse(ContentSync.AdvanceVersion(ref last, "baseline", notifyOnFirst: false));
            Assert.AreEqual("baseline", last);
            Assert.IsFalse(ContentSync.AdvanceVersion(ref last, "baseline", notifyOnFirst: true));
            Assert.IsTrue(ContentSync.AdvanceVersion(ref last, "changed", notifyOnFirst: true));
        }

        [Test]
        public void Carousel_SetTitles_RebuildsAndClampsIndex()
        {
            var c = new TitleCarousel(
                new List<LvnTitle> { new LvnTitle { id = "a", name = "A" } },
                new CarouselConfig(), new TestAssets());
            Assert.AreEqual("a", c.Current.id);

            c.SetTitles(new List<LvnTitle>
            {
                new LvnTitle { id = "x", name = "X" },
                new LvnTitle { id = "y", name = "Y" },
            });
            Assert.AreEqual("x", c.Current.id);   // index 0 preserved, now points at the new first title
            Assert.AreEqual(0, c.Index);

            c.SetTitles(new List<LvnTitle>());     // empty set must not throw or break
            Assert.IsNull(c.Current);
            Assert.AreEqual(0, c.Index);
        }
    }
}
