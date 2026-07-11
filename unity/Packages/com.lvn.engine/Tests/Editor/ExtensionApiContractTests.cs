using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// The Extension API contract (docs/embedding.md § API stability): every
    /// seam a plugin may build on, exercised TYPED. If a signature here stops
    /// compiling, a released plugin somewhere stops compiling — treat any red
    /// in this file as a breaking change that needs a major version (or an
    /// additive fix), never a silent rename. The runtime asserts pin the
    /// behavioural guarantees plugins rely on.
    /// </summary>
    public class ExtensionApiContractTests
    {
        // ── ILvnStage: a custom renderer is implementable with exactly this ──
        private sealed class ContractStage : ILvnStage
        {
            public int Says, Ends;
            public void ShowSay(string who, string text, string style) => Says++;
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command) { }
            public void OnEnd() => Ends++;
        }

        // ── ILvnAssets: a custom loader is implementable with exactly this ──
        // (LoadTextAsync/PreloadAsync keep default implementations — adding a
        // required member there is a breaking change this class would catch.)
        private sealed class ContractAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        [TearDown]
        public void Clean()
        {
            LvnOps.Clear();
            StageMenu.RemoveMenuItem("contract-item");
        }

        [Test]
        public void CustomOps_RegisterReplaceUnregister_RouteThroughThePlayer()
        {
            var calls = new List<string>();
            // Register / replace (same name wins last) / Handler signature.
            LvnOps.Handler first = (cmd, ctx) => calls.Add("first");
            LvnOps.Register("contract_op", first);
            LvnOps.Register("contract_op", (cmd, ctx) =>
            {
                // ILvnOpContext surface: Vars store + flow control + stage.
                IDictionary<string, JToken> vars = ctx.Vars;
                vars["contract_var"] = 7;
                ILvnStage stage = ctx.Stage;
                Assert.NotNull(stage);
                calls.Add("second");
            });

            var stage0 = new ContractStage();
            var p = new LvnPlayer(LvnDocument.Parse(
                @"{""script"":[{""op"":""contract_op""},{""op"":""say"",""text"":""x""}]}"), stage0);
            p.Advance();
            Assert.AreEqual(new[] { "second" }, calls, "the LAST registration for an op must win");
            Assert.AreEqual(7, (int)p.Vars["contract_var"], "ctx.Vars must be the story's live store");

            LvnOps.Unregister("contract_op");
            var p2 = new LvnPlayer(LvnDocument.Parse(
                @"{""script"":[{""op"":""contract_op""},{""op"":""say"",""text"":""x""}]}"), new ContractStage());
            Assert.DoesNotThrow(() => p2.Advance(), "an unregistered unknown op must stay ignored");
        }

        [Test]
        public void CustomOps_HoldPausesUntilResume_AndGoToRedirects()
        {
            ILvnOpContext captured = null;
            LvnOps.Register("contract_hold", (cmd, ctx) => { ctx.Hold(); captured = ctx; });

            var stage = new ContractStage();
            var p = new LvnPlayer(LvnDocument.Parse(@"{""script"":[
                {""op"":""contract_hold""},
                {""op"":""say"",""text"":""after""},
                {""op"":""label"",""id"":""skip""},
                {""op"":""say"",""text"":""jumped""}
            ]}"), stage);

            p.Advance();
            Assert.AreEqual(0, stage.Says, "Hold() must pause the script at the op");
            captured.GoTo("skip");
            captured.Resume();
            Assert.AreEqual(1, stage.Says, "Resume() must continue (through GoTo's target)");
        }

        [Test]
        public void MenuSlots_AddAndRemove_AreStable()
        {
            // Action<VnStage> is the pinned callback shape.
            Assert.DoesNotThrow(() => StageMenu.AddMenuItem("contract-item", (VnStage s) => { }));
            Assert.DoesNotThrow(() => StageMenu.RemoveMenuItem("contract-item"));
        }

        [Test]
        public void Events_KeepTheirDelegateShapes()
        {
            // Compile-time pins — no instances needed, the lambda types are the contract.
            Action<VnStage> pinStage = st =>
            {
                st.Saved += (string slot) => { };
                st.ChromeHiddenChanged += (bool hidden) => { };
            };
            Action<NovelApp> pinApp = app =>
            {
                app.ChapterStarted += (LvnTitle t, LvnChapter c) => { };
                app.ChapterFinished += (LvnTitle t, LvnChapter c) => { };
            };
            Assert.NotNull(pinStage);
            Assert.NotNull(pinApp);
        }

        [Test]
        public void SpineBridge_DelegateShapesAndAvailability()
        {
            var prev = LvnSpineBridge.Create;
            try
            {
                LvnSpineBridge.Create = null;
                Assert.IsFalse(LvnSpineBridge.Available, "no Create hook → not available");

                // The exact delegate shapes an external driver package assigns.
                LvnSpineBridge.Create = (RectTransform parent, string json, string atlas,
                    Texture2D[] pages, float scale, Texture2D bg) => null;
                Func<string, string, Texture2D[], Task> prepare = (j, a, p) => Task.CompletedTask;
                LvnSpineBridge.Prepare = prepare;
                LvnSpineBridge.Play = (GameObject go, string name, bool loop) => { };
                LvnSpineBridge.SetVisible = (GameObject go, bool visible) => { };
                LvnSpineBridge.Refit = (GameObject go, float scale, string fit) => { };
                LvnSpineBridge.ClearCache = () => { };
                Assert.IsTrue(LvnSpineBridge.Available, "a Create hook → available");
            }
            finally
            {
                LvnSpineBridge.Create = prev;
                LvnSpineBridge.Prepare = null;
                LvnSpineBridge.Play = null;
                LvnSpineBridge.SetVisible = null;
                LvnSpineBridge.Refit = null;
                LvnSpineBridge.ClearCache = null;
            }
        }

        [Test]
        public void Seams_AreImplementableWithTheDocumentedMembersOnly()
        {
            // Constructing them IS the assertion — the classes above implement
            // ILvnStage/ILvnAssets with only the documented members.
            ILvnStage stage = new ContractStage();
            ILvnAssets assets = new ContractAssets();
            Assert.NotNull(stage);
            Assert.NotNull(assets);
        }
    }
}
