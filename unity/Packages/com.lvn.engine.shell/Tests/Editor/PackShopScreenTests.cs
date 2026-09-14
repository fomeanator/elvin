using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    // Проверяем карточки и исход покупки без кошелька, сети и платёжного SDK.
    public sealed class PackShopScreenTests
    {
        const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly Type PackType = typeof(PackShopScreen).GetNestedType("Pack", BindingFlags.NonPublic);
        sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }
        static PackShopScreen Shop(bool stage = false, bool modal = true)
        {
            var shop = new PackShopScreen(new NoAssets(), modal);
            if (stage) shop.SetContent(JsonConvert.DeserializeObject<LvnManifest>(
                "{\"ui\":{\"browse\":{\"skin\":\"/test/skin/\"}}}"));
            return shop;
        }
        static object Pack(string sku = "large", bool best = false, string badge = "None", bool bundle = false)
        {
            var p = Activator.CreateInstance(PackType);
            void Set(string name, object value) => PackType.GetField(name).SetValue(p, value);
            Set("Sku", sku); Set("Currency", "crystals"); Set("Amount", 550L);
            Set("Price", "$49.99"); Set("Best", best); Set("Tint", LvnTokens.Accent);
            var badgeField = PackType.GetField("Badge");
            badgeField.SetValue(p, Enum.Parse(badgeField.FieldType, badge));
            if (bundle)
            {
                Set("Grants", new Dictionary<string, long> { ["crystals"] = 550, ["energy"] = 5 });
                Set("Headline", "Starter bundle"); Set("SubLine", "550 crystals · 5 energy");
            }
            return p;
        }
        static object Call(PackShopScreen shop, string name, params object[] args)
        {
            var method = typeof(PackShopScreen).GetMethod(name, Hidden);
            Assert.That(method, Is.Not.Null, "Missing testable operation: " + name);
            return method.Invoke(shop, args);
        }
        static VisualElement Card(PackShopScreen shop, object pack) => (VisualElement)Call(shop, "Card", pack);
        static IList List(params object[] packs)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(PackType));
            foreach (var pack in packs) list.Add(pack);
            return list;
        }
        static T Field<T>(object pack, string field) => (T)PackType.GetField(field).GetValue(pack);

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void RecommendedCardIsWiderHasTallerArtAndBottomGlow(bool stage, bool modal)
        {
            var shop = Shop(stage, modal);
            var small = Card(shop, Pack());
            var hero = Card(shop, Pack(best: true));
            Assert.That(hero.style.width.value.value, Is.GreaterThan(small.style.width.value.value));
            Assert.That(hero.Q("shop-glow"), Is.Not.Null);
            Assert.That(small.Q("shop-glow"), Is.Null);
            Assert.That(hero.Q("shop-art").style.height.value.value,
                Is.GreaterThan(small.Q("shop-art").style.height.value.value));
        }

        [TestCase(false)] [TestCase(true)]
        public void RibbonsUseDifferentWordsAndPlateColors(bool stage)
        {
            var shop = Shop(stage);
            var labels = new List<Label>();
            foreach (var badge in new[] { "Popular", "Value", "BestPrice" })
            {
                var card = Card(shop, Pack(badge: badge));
                var ribbons = card.Query<Label>("shop-ribbon").ToList();
                Assert.That(ribbons.Count, Is.EqualTo(1));
                Assert.That(ribbons[0].style.fontSize.value.value, Is.EqualTo(LvnTokens.TextXs));
                Assert.That(ribbons[0].style.letterSpacing.value.value, Is.GreaterThan(0));
                labels.Add(ribbons[0]);
            }
            Assert.That(labels.Select(x => x.text).Distinct().Count(), Is.EqualTo(3));
            Assert.That(labels.Select(x => x.style.backgroundColor.value).Distinct().Count(), Is.EqualTo(3));
        }

        [TestCase(false)] [TestCase(true)]
        public void PriceIsLargeAndBoldAndGrantsAreCurrencyChips(bool stage)
        {
            var card = Card(Shop(stage), Pack(bundle: true));
            var buy = card.Q<Button>();
            Assert.That(buy.style.fontSize.value.value, Is.GreaterThanOrEqualTo(LvnTokens.TextLg));
            Assert.That(buy.style.unityFontStyleAndWeight.value, Is.EqualTo(FontStyle.Bold));
            Assert.That(card.Q("shop-grants"), Is.Not.Null);
            Assert.That(card.Q("shop-grants").childCount, Is.EqualTo(2));
            Assert.That(card.Query<Label>().ToList().Any(x => x.text == "550 crystals · 5 energy"), Is.False);
        }

        [Test]
        public void HeuristicChoosesOnlyLastPackAndIsRepeatable()
        {
            var shop = Shop();
            var list = List(Pack("small"), Pack("mid"), Pack("large"));
            Call(shop, "Recommend", list); Call(shop, "Recommend", list);
            Assert.That(list.Cast<object>().Count(x => Field<bool>(x, "Best")), Is.EqualTo(1));
            Assert.That(Field<bool>(list[2], "Best"), Is.True);
            Assert.That(Field<object>(list[1], "Badge").ToString(), Is.EqualTo("Popular"));
            Assert.That(Field<object>(list[2], "Badge").ToString(), Is.EqualTo("BestPrice"));
        }

        [Test]
        public void ManifestOverridesRecommendationAndBadgesAndEmptyMapRemovesBadges()
        {
            var shop = Shop();
            shop.SetContent(JsonConvert.DeserializeObject<LvnManifest>(
                "{\"ui\":{\"store\":{\"recommended_sku\":\"small\",\"badges\":{\"small\":\"value\",\"mid\":\"best_price\"}}}}"));
            var list = List(Pack("small"), Pack("mid"), Pack("large"));
            Call(shop, "Recommend", list);
            Assert.That(list.Cast<object>().Count(x => Field<bool>(x, "Best")), Is.EqualTo(1));
            Assert.That(Field<bool>(list[0], "Best"), Is.True);
            Assert.That(Field<object>(list[0], "Badge").ToString(), Is.EqualTo("Value"));
            Assert.That(Field<object>(list[1], "Badge").ToString(), Is.EqualTo("BestPrice"));
            Assert.That(Field<object>(list[2], "Badge").ToString(), Is.EqualTo("None"));
            shop.SetContent(JsonConvert.DeserializeObject<LvnManifest>("{\"ui\":{\"store\":{\"badges\":{}}}}"));
            Call(shop, "Recommend", list);
            Assert.That(list.Cast<object>().All(x => Field<object>(x, "Badge").ToString() == "None"), Is.True);
            Assert.That(Field<bool>(list[2], "Best"), Is.True, "Removing the override restores the heuristic");
        }

        [Test]
        public void ManifestUpdateReordersLoadedCardsAndUnchangedSyncKeepsTheirIdentity()
        {
            var shop = Shop();
            var ids = (List<string>)typeof(PackShopScreen).GetField("_tabIds", Hidden).GetValue(shop);
            ids.Add("crystals");
            var catalog = (IDictionary)typeof(PackShopScreen).GetField("_catalog", Hidden).GetValue(shop);
            catalog["crystals"] = List(Pack("small"), Pack("mid"), Pack("large"));
            shop.Rebuild();
            Assert.That(shop.Q("shop-grid")[0].userData, Is.EqualTo("large"));
            var manifest = JsonConvert.DeserializeObject<LvnManifest>(
                "{\"ui\":{\"store\":{\"recommended_sku\":\"small\",\"badges\":{\"small\":\"value\"}}}}");
            shop.SetContent(manifest);
            var first = shop.Q("shop-grid")[0];
            Assert.That(first.userData, Is.EqualTo("small"));
            shop.SetContent(manifest);
            Assert.That(shop.Q("shop-grid")[0], Is.SameAs(first), "A wallet sync must not erase purchase feedback");
            manifest.ui.store.recommended_sku = "missing";
            shop.SetContent(manifest);
            Assert.That(shop.Query<VisualElement>(className: "shop-recommended").ToList(), Is.Empty);
        }

        [Test]
        public async Task TransportExceptionShowsFailureAndAllowsRetry()
        {
            var shop = Shop();
            var pack = (PackShopScreen.Pack)Pack();
            var buy = Card(shop, pack).Q<Button>();
            var hold = new TaskCompletionSource<bool>();
            shop.Purchase = _ => Task.FromException<bool>(new InvalidOperationException("offline"));
            shop.NoticeDelay = _ => hold.Task;
            var pending = shop.PurchaseAsync(buy, pack);
            Assert.That(buy.text, Is.EqualTo(LvnWords.Of("shop.failed", "Failed")));
            Assert.That(buy.style.color.value, Is.EqualTo(LvnTokens.Warn));
            Assert.That(buy.enabledSelf, Is.False);
            hold.SetResult(true); await pending;
            Assert.That(buy.enabledSelf, Is.True);
            Assert.That(buy.text, Is.EqualTo(pack.Price));
        }

        [TestCase(false)] [TestCase(true)]
        public void ActiveTabHasFilledPlate(bool stage)
        {
            var shop = Shop(stage);
            var ids = (List<string>)typeof(PackShopScreen).GetField("_tabIds", Hidden).GetValue(shop);
            ids.AddRange(new[] { "crystals", "energy" });
            Call(shop, "BuildTabs");
            var row = (VisualElement)typeof(PackShopScreen).GetField("_tabsRow", Hidden).GetValue(shop);
            Assert.That(row[0].style.backgroundColor.value, Is.EqualTo(stage ? LvnTokens.Gold : LvnTokens.Accent));
            Assert.That(row[1].style.backgroundColor.value, Is.Not.EqualTo(row[0].style.backgroundColor.value));
        }

        [TestCase(false)] [TestCase(true)]
        public async Task PurchaseShowsFourDistinctStatesAndRestoresPrice(bool stage)
        {
            var shop = Shop(stage);
            var pack = Pack(best: true);
            var buy = Card(shop, pack).Q<Button>();
            var states = new List<(string, Color)> { (buy.text, buy.style.color.value) };
            foreach (bool ok in new[] { true, false })
            {
                var result = new TaskCompletionSource<bool>();
                var hold = new TaskCompletionSource<bool>();
                int duration = 0, calls = 0;
                var purchase = typeof(PackShopScreen).GetField("Purchase", Hidden);
                Assert.That(purchase, Is.Not.Null, "Purchase must be injectable without a wallet/server");
                Func<Task<bool>> work = () => { calls++; return result.Task; };
                purchase.SetValue(shop, Expression.Lambda(purchase.FieldType,
                    Expression.Invoke(Expression.Constant(work)), Expression.Parameter(PackType)).Compile());
                var delay = typeof(PackShopScreen).GetField("NoticeDelay", Hidden);
                Assert.That(delay, Is.Not.Null);
                delay.SetValue(shop, (Func<int, Task>)(ms => { duration = ms; return hold.Task; }));
                var task = (Task)Call(shop, "PurchaseAsync", buy, pack);
                Assert.That(buy.text, Is.EqualTo("…"));
                Assert.That(buy.enabledSelf, Is.False);
                if (ok) states.Add((buy.text, buy.style.color.value));
                await (Task)Call(shop, "PurchaseAsync", buy, pack);
                Assert.That(calls, Is.EqualTo(1), "Repeated taps must not submit twice");
                result.SetResult(ok);
                for (int i = 0; duration == 0 && i < 20; i++) await Task.Yield();
                Assert.That(duration, Is.EqualTo(ok ? 1000 : 2000));
                Assert.That(buy.text, Is.EqualTo(ok ? LvnWords.Of("common.done", "Done") : LvnWords.Of("shop.failed", "Failed")));
                Assert.That(buy.enabledSelf, Is.False);
                states.Add((buy.text, buy.style.color.value));
                hold.SetResult(true); await task;
                Assert.That(buy.text, Is.EqualTo("$49.99"));
                Assert.That(buy.enabledSelf, Is.True);
                Assert.That(buy.style.color.value, Is.EqualTo(states[0].Item2));
            }
            Assert.That(states.Select(x => x.Item1).Distinct().Count(), Is.EqualTo(4));
            Assert.That(states.Select(x => x.Item2).Distinct().Count(), Is.EqualTo(4));
        }
    }
}
