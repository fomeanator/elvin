using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests.Runtime
{
    public partial class SoloTests
    {
        private sealed class DelayedPortalArt : ILvnAssets
        {
            public readonly TaskCompletionSource<Sprite> Ready = new TaskCompletionSource<Sprite>();
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Ready.Task;
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private static JObject PortalPose(float x)
        {
            var pose = Show("hero");
            pose["x"] = x;
            pose["crop"] = true;
            pose["enter"] = "none";
            pose["transition_duration"] = VnStage.DeclareMovement(0.3f);
            return pose;
        }

        [UnityTest]
        public IEnumerator PortalDissolveDoesNotSurviveChapterReplay()
        {
            var script = new JObject { ["script"] = new JArray(PortalPose(0.5f),
                new JObject { ["op"] = "say", ["text"] = "Saved heroine" },
                new JObject { ["op"] = "say", ["text"] = "Next line" }) }.ToString();
            _stage.KeepActorAlive = "hero";
            _stage.Play(script);
            yield return null;
            var snapshot = _stage.Player.Save();
            var original = Doll("hero");
            Assert.IsNotNull(original);

            for (int visit = 0; visit < 2; visit++)
            {
                _stage.ApplyStage(new JObject { ["op"] = "sfx", ["id"] = "hero",
                    ["dissolve"] = 1f, ["dur"] = 0f }, LvnSender.Cutscene);
                Assert.IsTrue(LvnSpriteFxDriver.WearsAuthoredFx(original));
                _stage.Play(script, warmIntroSpine: false);
                var restore = (Task)typeof(VnStage).GetMethod("RestoreSnapshotAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(_stage, new object[] { snapshot });
                float deadline = Time.realtimeSinceStartup + 3f;
                while (!restore.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(restore.IsCompleted && !restore.IsFaulted);
                yield return null;
                Assert.AreSame(original, Doll("hero"), "Retain the existing art across replay");
                Assert.IsTrue(original.activeInHierarchy);
                foreach (var graphic in original.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                    if (graphic.material != null && graphic.material.HasProperty("_Dissolve"))
                        Assert.Less(graphic.material.GetFloat("_Dissolve"), 0.01f,
                            "Replay kept the portal's invisibility on the live heroine, visit " + visit);
                Assert.IsFalse(LvnSpriteFxDriver.WearsAuthoredFx(original),
                    "Portal effects must not become authored effects in the next chapter");
            }
        }

        [UnityTest]
        public IEnumerator PortalWaitsForDrawableArtInsteadOfPendingSlot()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""frame""}]}");
            yield return null;
            var source = new OneSpriteAssets();
            var delayed = new DelayedPortalArt();
            _stage.Assets = delayed;
            _stage.ApplyStage(PortalPose(0.5f), LvnSender.Cutscene);
            var wait = _stage.WaitForActorArtAsync("hero");
            yield return null;
            Assert.IsFalse(wait.IsCompleted, "An allocated slot is not loaded art");
            delayed.Ready.SetResult(source.Sprite);
            float deadline = Time.realtimeSinceStartup + 3f;
            while (!wait.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(wait.IsCompleted && !wait.IsFaulted);
            Assert.IsTrue(_stage.ActorArtAlive("hero"));
        }

        [UnityTest]
        public IEnumerator MenuReturnKeepsItsSenderAndMovesFromCutscenePosition()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""frame""}]}");
            yield return null;
            var home = PortalPose(0.2f);
            _stage.ShowMenuDoll("hero", home);
            yield return null;
            Assert.IsTrue(_stage.Memory.TryPoseSender("hero", out var sender));
            Assert.AreEqual(LvnSender.Menu, sender, "Menu composition must retain its sender");
            var actor = Doll("hero").GetComponent<WorldActor>();
            var homePosition = actor.SlotBase;
            _stage.ApplyStage(PortalPose(0.8f), LvnSender.Cutscene);
            yield return new WaitForSecondsRealtime(0.4f);
            var portalPosition = actor.SlotBase;
            Assert.Greater(Vector2.Distance(homePosition, portalPosition), 10f);
            _stage.ShowMenuDoll("hero", home);
            Assert.Less(Vector2.Distance(actor.SlotBase, portalPosition), 1f,
                "Return should begin at the visible portal position, without a snap");
            yield return new WaitForSecondsRealtime(0.1f);
            Assert.Greater(Vector2.Distance(actor.SlotBase, portalPosition), 1f, "Return must actually start moving");
            Assert.Greater(Vector2.Distance(actor.SlotBase, homePosition), 1f, "Movement needs intermediate frames");
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.Less(Vector2.Distance(actor.SlotBase, homePosition), 1f, "Return must reach the menu slot");
        }

        [UnityTest]
        public IEnumerator DeferredStoryCommandIsRememberedAfterWardrobeReleasesIt()
        {
            yield return Staged("hero");
            _stage.Commands.Hold("actor:hero", LvnSender.Wardrobe);
            _stage.ApplyStage(PortalPose(0.7f));
            _stage.Commands.ReleaseAll(LvnSender.Wardrobe);
            yield return null;
            Assert.AreEqual(0.7f, (float?)_stage.StoryFrame.Actors["hero"].Pose["x"],
                "A deferred story command must update the frame used after cutscenes");
        }
        [UnityTest]
        public IEnumerator LeavingLiftedHomeReturnsHeroineToTheThemeBaseline()
        {
            yield return Staged("hero");
            _stage.Prima.Cast("hero");
            _stage.Theme.ActorBaselineY = 0.96f;
            for (int visit = 0; visit < 2; visit++)
            {
                Assert.IsTrue(_stage.Prima.Stand(LvnSender.Menu, place: "left", lift: 0.1f));
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.IsTrue(_stage.Memory.TryWhere("hero", out var home));
                Assert.AreEqual(0.9f, home.Y, 0.001f);
                float homeY = Doll("hero").GetComponent<WorldActor>().SlotBase.y;

                Assert.IsTrue(_stage.Prima.Stand(LvnSender.Menu, place: "center", lift: 0f));
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.IsTrue(_stage.Memory.TryWhere("hero", out var store));
                Assert.AreEqual(0.96f, store.Y, 0.001f, "zero lift resets the sticky Y to the theme baseline");
                Assert.Less(Doll("hero").GetComponent<WorldActor>().SlotBase.y, homeY - 1f,
                    "the visible actor must move down, not just the remembered command");
            }
        }
    }
}
