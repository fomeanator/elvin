using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    public sealed class ProfileScreenTests
    {
        const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }
        static ProfileScreen Screen(bool stage)
        {
            var p = new ProfileScreen(new NoAssets());
            if (stage) p.SetContent(new LvnManifest { ui = new LvnUiConfig
                { browse = new BrowseConfig { skin = "/test/skin/" } } });
            return p;
        }
        static ScrollView Body(ProfileScreen p) => (ScrollView)typeof(ProfileScreen).GetField("_body", Hidden).GetValue(p);

        [TestCase(false)] [TestCase(true)]
        public void MinimalHidesOnlyStatsProgressAndAchievements_KeepsAvatarRelationsLinksAndId(bool stage)
        {
            // TR-25 (правило Ильи, ac14dc25): минимальный профиль прячет
            // выдуманные разделы — статы, прогресс, достижения, — а кружок
            // аватара, отношения, пункты и ID остаются: на Time Romance
            // profile_full=false, и «минимальный» — это весь профиль игрока.
            // Версия, обрубавшая его до имени и ID, ушла на сборку и была
            // откачена («где пункты?» — Илья 15.09).
            var p = Screen(stage);
            p.Minimal = true; p.Uid = "player-123"; p.Level = 9; p.XpNext = 100; p.ChaptersDone = 12;
            p.Stats.Add(new ProfileScreen.Stat("123", "Decisions"));
            p.Achievements.Add(new ProfileScreen.Achievement(LvnIcon.Book, "First chapter", true));
            p.Relations.Add(new ProfileScreen.Relation("Alex", .9f));
            p.OnOpenSettings = p.OnOpenCutscenes = p.OnGiveShare = p.OnPickAvatar = () => { };
            p.OnTakeShare = _ => { };
            p.OnSignOut = p.OnDeleteAccount = () => Task.FromResult(true);
            p.Rebuild();
            var body = Body(p);
            Assert.That(body.Q("profile-avatar"), Is.Not.Null, "кружок аватара остаётся в минимальном профиле");
            Assert.That(body.Q("profile-player-name"), Is.Not.Null);
            Assert.That(body.Q("profile-relation"), Is.Not.Null, "отношения — настоящие данные, показываются и в минимальном");
            Assert.That(body.Q("profile-copy-id"), Is.Not.Null, "ID с копированием остаётся");
            Assert.That(body.Q("profile-stat"), Is.Null, "статы спрятаны");
            Assert.That(body.Q("profile-stats-empty"), Is.Null, "и пояснение к статам тоже");
            Assert.That(body.Q("profile-achievement"), Is.Null, "достижения спрятаны");
            Assert.That(body.Q("profile-progress-empty"), Is.Null, "прогресс спрятан");
            var texts = body.Query<Label>().ToList().Select(l => l.text).ToList();
            Assert.That(texts, Has.None.EqualTo("123"), "значение стата просочилось");
            Assert.That(texts, Has.None.EqualTo("First chapter"), "достижение просочилось");
            Assert.That(texts, Has.None.EqualTo("Decisions"));
            Assert.That(body.Query<Button>().ToList().Count, Is.GreaterThan(1),
                "пункты (катсцены, настройки, выход, удаление) остаются — не только «Копировать»");
        }

        [TestCase(false)] [TestCase(true)]
        public void EmptyFullProfileExplainsEveryContentSectionWithoutInventingData(bool stage)
        {
            var p = Screen(stage);
            foreach (string section in new[] { "stats", "progress", "achievements", "relations" })
                Assert.That(Body(p).Q("profile-" + section + "-empty"), Is.Not.Null, section + " disappeared");
            var locks = Body(p).Query<VisualElement>(className: "profile-achievement-locked").ToList();
            Assert.That(locks.Count, Is.EqualTo(4), "An empty collection has a small preview grid");
            Assert.That(p.Stats, Is.Empty); Assert.That(p.Achievements, Is.Empty); Assert.That(p.Relations, Is.Empty);
            Assert.That(Body(p).Query<VisualElement>(className: "profile-achievement-unlocked").ToList(), Is.Empty);
        }

        [Test]
        public void PopulatedSectionsReplaceTheirPlaceholdersAndClearingRestoresThem()
        {
            var p = Screen(false);
            p.Stats.Add(new ProfileScreen.Stat("24", "Decisions")); p.ChaptersDone = 1;
            p.Achievements.Add(new ProfileScreen.Achievement(LvnIcon.Book, "First chapter", true));
            p.Relations.Add(new ProfileScreen.Relation("Alex", .5f)); p.Rebuild();
            foreach (string section in new[] { "stats", "progress", "achievements", "relations" })
                Assert.That(Body(p).Q("profile-" + section + "-empty"), Is.Null);
            Assert.That(Body(p).Query<VisualElement>(className: "profile-achievement-unlocked").ToList().Count, Is.EqualTo(1));
            p.Stats.Clear(); p.Achievements.Clear(); p.Relations.Clear(); p.ChaptersDone = 0; p.Rebuild();
            Assert.That(Body(p).Q("profile-achievements-empty"), Is.Not.Null);
            Assert.That(Body(p).Q("profile-relations-empty"), Is.Not.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void RelationsSortByAffectionThenNameWithoutChangingHostData(bool stage)
        {
            var p = Screen(stage);
            p.Relations.AddRange(new[] { new ProfileScreen.Relation("Zoe", .5f),
                new ProfileScreen.Relation("Mira", .9f), new ProfileScreen.Relation("Alex", .5f) });
            p.Rebuild();
            string[] Names() => Body(p).Query<Label>("profile-relation-name").ToList().Select(l => l.text).ToArray();
            Assert.That(Names(), Is.EqualTo(new[] { "Mira", "Alex", "Zoe" }));
            Assert.That(p.Relations[0].Name, Is.EqualTo("Zoe"), "Rendering must not reorder the host's list");
            p.Relations.Reverse(); p.Rebuild();
            Assert.That(Names(), Is.EqualTo(new[] { "Mira", "Alex", "Zoe" }));
        }

        [Test]
        public void EqualNamesAndAffectionUseCharacterIdAsTheLastTieBreaker()
        {
            var p = Screen(false);
            p.Relations.Add(new ProfileScreen.Relation("Alex", .5f, "story-b:alex"));
            p.Relations.Add(new ProfileScreen.Relation("Alex", .5f, "story-a:alex"));
            p.Rebuild();
            Assert.That(Body(p).Query<VisualElement>("profile-relation").ToList().Select(r => r.userData),
                Is.EqualTo(new[] { "story-a:alex", "story-b:alex" }));
        }

        [Test]
        public void ReturningFromMinimalRestoresTheFullProfileAndAvatar()
        {
            var p = Screen(false);
            p.Minimal = true; p.Rebuild();
            Assert.That(Body(p).Q("profile-avatar"), Is.Not.Null, "кружок аватара есть и в минимальном");
            Assert.That(Body(p).Q("profile-stats-empty"), Is.Null, "статов в минимальном нет");
            p.Minimal = false; p.Rebuild();
            Assert.That(Body(p).Q("profile-avatar").Q("lvn-avatar-picture"), Is.Not.Null);
            Assert.That(Body(p).Q("profile-relations-empty"), Is.Not.Null);
        }

        [Test]
        public void StaticAvatarSetRemainsAvailableWithoutAWardrobeHero()
        {
            var manifest = new LvnManifest { ui = new LvnUiConfig { browse = new BrowseConfig
                { avatars = new List<AvatarChoice> { new AvatarChoice { id = "free", url = "/avatar.png" } } } } };
            Assert.That(LvnAvatars.CanChoose(manifest), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void StatValuesHaveMoreWeightAndFooterCopyIsSecondary(bool stage)
        {
            var p = Screen(stage); p.Stats.Add(new ProfileScreen.Stat("123", "Decisions")); p.Rebuild();
            var value = Body(p).Q<Label>("profile-stat-value");
            var caption = Body(p).Q<Label>("profile-stat-caption");
            Assert.That(value, Is.Not.Null); Assert.That(caption, Is.Not.Null);
            Assert.That(value.style.fontSize.value.value, Is.GreaterThanOrEqualTo(LvnTokens.TextLg));
            Assert.That(value.style.unityFontStyleAndWeight.value, Is.EqualTo(FontStyle.Bold));
            Assert.That(caption.style.color.value, Is.EqualTo(LvnTokens.TextDim));
            var copy = Body(p).Q<Button>("profile-copy-id");
            Assert.That(copy, Is.Not.Null);
            Assert.That(copy.style.backgroundColor.value, Is.EqualTo(LvnTokens.Faint));
        }

        [TestCase(false)] [TestCase(true)]
        public void AvatarChoiceIsAvailableWithOnlyALiveHero(bool stage)
        {
            var manifest = new LvnManifest { ui = new LvnUiConfig
                { wardrobe = new WardrobeConfig { entity = "profile-test-hero" } },
                sprites = new Dictionary<string, LvnSpriteEntity> {
                    ["profile-test-hero"] = new LvnSpriteEntity { layers = new List<LvnLayer> { new LvnLayer("/face.png") } } } };
            if (stage) manifest.ui.browse = new BrowseConfig { skin = "/test/" };
            var canChoose = typeof(LvnAvatars).GetMethod("CanChoose");
            Assert.That(canChoose, Is.Not.Null);
            Assert.That(canChoose.Invoke(null, new object[] { manifest }), Is.EqualTo(true));
            manifest.ui.wardrobe.entity = null;
            Assert.That(canChoose.Invoke(null, new object[] { manifest }), Is.EqualTo(false));
        }
    }
}
