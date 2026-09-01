using System.IO;
using System.Linq;
using System.Text;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class ContentLoaderCacheTests
    {
        [Test]
        public void AtomicWrite_WritesContentAndLeavesNoTemp()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lvn-atomic-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "cache.bin");
                ContentLoader.AtomicWriteAllBytes(path, Encoding.UTF8.GetBytes("hello"));
                Assert.AreEqual("hello", File.ReadAllText(path));

                // Overwrite must replace the content, not append or fail.
                ContentLoader.AtomicWriteAllBytes(path, Encoding.UTF8.GetBytes("world!!"));
                Assert.AreEqual("world!!", File.ReadAllText(path));

                // No staging temp files may be left behind.
                var leftovers = Directory.GetFiles(dir).Where(f => f.Contains(".tmp-")).ToArray();
                CollectionAssert.IsEmpty(leftovers, "atomic write left a temp file behind");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void PickEvictions_OldestFirstUntilUnderBudget()
        {
            const long MB = 1 << 20;
            var entries = new System.Collections.Generic.List<(string, long, long, float, bool)>
            {
                ("old-a", 10 * MB, 1, 0f, false),
                ("old-b", 10 * MB, 2, 0f, false),
                ("newer", 10 * MB, 3, 0f, false),
            };
            // Budget 15MB, total 30MB, all past grace → evict the two oldest.
            var evict = ContentLoader.PickEvictions(entries, 15 * MB, 1000f, 60f);
            CollectionAssert.AreEqual(new[] { "old-a", "old-b" }, evict);
        }

        /// <summary>
        /// ВЕРСИЯ ПРОИЗВОДНОГО ФАЙЛА — ЭТО ВЕРСИЯ ЕГО ИСХОДНИКА.
        ///
        /// <para>Индекс версий сознательно не содержит производных
        /// (<c>@2k</c>-даунскейлы, коды <c>.ktx2</c>):
        /// они появляются на сервере лениво, и их версия ломала бы игрокам
        /// главы посреди сцены. Но «нет версии» означало «вечный ключ кэша»:
        /// автор заменяет фотографию, а клиент продолжает доставать из кэша
        /// перекодировку СТАРОЙ — героиня пережила три замены арта, оставаясь
        /// мыльной миниатюрой. Производный URL обязан наследовать версию
        /// исходной картинки.</para>
        /// </summary>
        [Test]
        public void Lookup_DerivedVariantInheritsTheSourceImageVersion()
        {
            var map = new System.Collections.Generic.Dictionary<string, string>
            {
                ["sprites/hero/dress.png"] = "sha-dress",
                ["bg/city.jpg"] = "sha-city",
            };
            Assert.AreEqual("sha-dress",
                ContentLoader.Lookup(map, "/content/sprites/hero/dress@2k.ktx2"),
                "ktx2 от @2k-варианта должен версионироваться по исходному png");
            Assert.AreEqual("sha-dress",
                ContentLoader.Lookup(map, "/content/sprites/hero/dress@2k.png"),
                "@2k-даунскейл должен версионироваться по исходному png");
            Assert.AreEqual("sha-city",
                ContentLoader.Lookup(map, "/content/bg/city@2k.ktx2"),
                "исходник может быть и jpg — перебираются все расширения");
            Assert.IsNull(ContentLoader.Lookup(map, "/content/bg/unknown@2k.png"),
                "вариант без исходника в индексе остаётся без версии, как раньше");
        }

        /// <summary>Прямая запись в индексе (вдруг когда-то появится) сильнее
        /// выведенной: точное знание побеждает эвристику.</summary>
        [Test]
        public void Lookup_ADirectIndexEntryStillWins()
        {
            var map = new System.Collections.Generic.Dictionary<string, string>
            {
                ["sprites/hero/dress.png"] = "sha-dress",
                ["sprites/hero/dress@2k.ktx2"] = "sha-exact",
            };
            Assert.AreEqual("sha-exact",
                ContentLoader.Lookup(map, "/content/sprites/hero/dress@2k.ktx2"));
        }

        /// <summary>Целостность НИКОГДА не наследует версию: sha исходника
        /// описывает исходник, а не байты перекодировки. На живом прогоне
        /// унаследованная версия в проверке целостности зациклила клиент:
        /// «sha256 mismatch → refetching» на каждом ktx2, бесконечно.</summary>
        [Test]
        public void Lookup_IntegrityModeNeverInheritsFromTheSource()
        {
            var map = new System.Collections.Generic.Dictionary<string, string>
            {
                ["sprites/hero/dress.png"] = "sha-dress",
            };
            Assert.IsNull(
                ContentLoader.Lookup(map, "/content/sprites/hero/dress@2k.ktx2", allowDerived: false),
                "проверка целостности с чужим sha перекачивала бы файл вечно");
            Assert.AreEqual("sha-dress",
                ContentLoader.Lookup(map, "/content/sprites/hero/dress.png", allowDerived: false),
                "точная запись работает в обоих режимах");
        }

        [Test]
        public void SourceCandidates_OnlyDerivedPathsProduceAny()
        {
            CollectionAssert.IsEmpty(
                ContentLoader.SourceCandidates("sprites/hero/dress.png").ToList(),
                "обычный png — не вариант, наследовать нечего");
            CollectionAssert.AreEqual(new[] { "a/b.png" },
                ContentLoader.SourceCandidates("a/b@2k.png").ToList());
            CollectionAssert.AreEqual(new[] { "a/b.png", "a/b.jpg", "a/b.jpeg" },
                ContentLoader.SourceCandidates("a/b@2k.ktx2").ToList(),
                "перекодировка прячет расширение исходника — перебираем как сервер");
        }

        [Test]
        public void PickEvictions_GraceProtectsRecentlyUsed()
        {
            const long MB = 1 << 20;
            var entries = new System.Collections.Generic.List<(string, long, long, float, bool)>
            {
                ("visible-bg", 20 * MB, 1, 995f, false), // requested 5s ago — on screen
                ("stale",      20 * MB, 2, 0f, false),
            };
            var evict = ContentLoader.PickEvictions(entries, 25 * MB, 1000f, 60f);
            CollectionAssert.AreEqual(new[] { "stale" }, evict,
                "recently-requested art is never evicted, even if it's the oldest by sequence");
        }

        [Test]
        public void PickEvictions_GraceIsNotAVeto_PinsAre()
        {
            // Загрузка главы трогает всё за минуту: если grace — абсолютное
            // вето, бюджет не работает ровно тогда, когда нужен. Над бюджетом
            // вытесняются и свежие (старейшие сначала); запиненные — никогда.
            const long MB = 1 << 20;
            var entries = new System.Collections.Generic.List<(string, long, long, float, bool)>
            {
                ("pinned-fresh", 20 * MB, 1, 999f, true),
                ("fresh-old",    20 * MB, 2, 999f, false),
                ("fresh-new",    20 * MB, 3, 999f, false),
            };
            var evict = ContentLoader.PickEvictions(entries, 25 * MB, 1000f, 60f);
            CollectionAssert.AreEqual(new[] { "fresh-old", "fresh-new" }, evict,
                "над бюджетом свежесть не спасает — неприкосновенны только пины");
        }

        [Test]
        public void PickCacheVictims_DeadAlways_SharedNever_QuotaEvictsOldestButNotProtected()
        {
            // «Перс есть во второй главе — с первой его не удаляют»: общий файл
            // живёт, пока его знает хоть одна глава. Мёртвые версии — всегда вон;
            // над квотой уходят давние, защищённые (текущая глава) — никогда.
            const long MB = 1 << 20;
            var files = new System.Collections.Generic.List<(string, long, double)>
            {
                ("dead-old-version", 50 * MB, 100.0),
                ("shared-actor",     50 * MB, 200.0),
                ("old-chapter-bg",   50 * MB, 300.0),
                ("current-chapter",  50 * MB, 400.0),
            };
            var live = new System.Collections.Generic.HashSet<string>
                { "shared-actor", "old-chapter-bg", "current-chapter" };
            var prot = new System.Collections.Generic.HashSet<string> { "current-chapter" };

            // Под квотой: уходит только мёртвое.
            var v1 = ContentLoader.PickCacheVictims(files, live, prot, 200 * MB);
            CollectionAssert.AreEqual(new[] { "dead-old-version" }, v1);

            // Над квотой (лимит 60 МБ на 150 МБ живого): к мёртвому добавляются
            // давние живые; защищённая текущая глава остаётся при любом лимите.
            var v2 = ContentLoader.PickCacheVictims(files, live, prot, 60 * MB);
            CollectionAssert.AreEqual(new[] { "dead-old-version", "shared-actor", "old-chapter-bg" }, v2);
            CollectionAssert.DoesNotContain(v2, "current-chapter");
        }

        [Test]
        public void PickEvictions_UnderBudgetEvictsNothing()
        {
            const long MB = 1 << 20;
            var entries = new System.Collections.Generic.List<(string, long, long, float, bool)>
            {
                ("a", 5 * MB, 1, 0f, false), ("b", 5 * MB, 2, 0f, false),
            };
            CollectionAssert.IsEmpty(ContentLoader.PickEvictions(entries, 100 * MB, 1000f, 60f));
        }

        [Test]
        public void PickEvictions_PinnedNeverEvicted()
        {
            const long MB = 1 << 20;
            var entries = new System.Collections.Generic.List<(string, long, long, float, bool)>
            {
                ("spine-page", 30 * MB, 1, 0f, true),  // pinned: a live skeleton's texture
                ("stale-bg",   30 * MB, 2, 0f, false),
            };
            // Over budget and both past grace, but the pinned page is off-limits —
            // the evictor must take the unpinned one even though it's newer.
            var evict = ContentLoader.PickEvictions(entries, 25 * MB, 1000f, 60f);
            CollectionAssert.AreEqual(new[] { "stale-bg" }, evict,
                "a pinned texture (in use by a live skeleton) is never evicted");
        }

        [Test]
        public void Sha256Matches_AcceptsCorrectRejectsWrong()
        {
            var data = System.Text.Encoding.UTF8.GetBytes("hello");
            const string good = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
            Assert.IsTrue(ContentLoader.Sha256Matches(data, good));
            Assert.IsTrue(ContentLoader.Sha256Matches(data, good.ToUpperInvariant()), "hex case-insensitive");
            Assert.IsFalse(ContentLoader.Sha256Matches(data, good.Replace('2', '3')));
            Assert.IsFalse(ContentLoader.Sha256Matches(data, "deadbeef"), "wrong length rejected");
            Assert.IsFalse(ContentLoader.Sha256Matches(null, good));
            Assert.IsFalse(ContentLoader.Sha256Matches(data, null));
        }

        [Test]
        public void HashKey_IsDeterministic()
        {
            var a = ContentLoader.HashKey("/content/bg/porch.jpg", null);
            var b = ContentLoader.HashKey("/content/bg/porch.jpg", null);
            Assert.AreEqual(a, b);
        }

        [Test]
        public void HashKey_IsSha1Hex()
        {
            var key = ContentLoader.HashKey("/content/bg/porch.jpg", null);
            Assert.AreEqual(40, key.Length);            // sha1 = 20 bytes = 40 hex chars
            StringAssert.IsMatch("^[0-9a-f]+$", key);
        }

        [Test]
        public void HashKey_VersionChangesKey()
        {
            // The whole point of cache-busting: a new content version → a new key
            // → a fresh cache file, leaving the old one as an offline fallback.
            var unversioned = ContentLoader.HashKey("/content/bg/porch.jpg", null);
            var v1 = ContentLoader.HashKey("/content/bg/porch.jpg", "aaaa1111");
            var v2 = ContentLoader.HashKey("/content/bg/porch.jpg", "bbbb2222");

            Assert.AreNotEqual(unversioned, v1);
            Assert.AreNotEqual(v1, v2);
        }

        [Test]
        public void HashKey_DifferentUrlsDiffer()
        {
            var a = ContentLoader.HashKey("/content/bg/a.jpg", "v1");
            var b = ContentLoader.HashKey("/content/bg/b.jpg", "v1");
            Assert.AreNotEqual(a, b);
        }

        // The mobile texture cap: oversized art fits the cap on its longest
        // side, aspect preserved; anything within passes through untouched.
        [Test]
        public void FitWithin_CapsTheLongestSideKeepingAspect()
        {
            Assert.AreEqual(new UnityEngine.Vector2Int(1920, 1080),
                ContentLoader.FitWithin(1920, 1080, 2560), "within the cap → identity");
            Assert.AreEqual(new UnityEngine.Vector2Int(2560, 1440),
                ContentLoader.FitWithin(3840, 2160, 2560), "4K → capped, 16:9 kept");
            Assert.AreEqual(new UnityEngine.Vector2Int(1440, 2560),
                ContentLoader.FitWithin(2160, 3840, 2560), "portrait caps the height");
            Assert.AreEqual(new UnityEngine.Vector2Int(1, 2560),
                ContentLoader.FitWithin(1, 10000, 2560), "degenerate strip never hits 0");
        }
    }
}
