using System;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI.Screens;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class AvatarChoiceTests
    {
        private string _owner, _picked, _url;
        private bool _offline;
        private LvnManifest _manifest;
        [SetUp] public void SetUp()
        {
            _owner = LvnKeep.Owner; _picked = LvnAvatars.Picked;
            _url = LvnBackend.BaseUrl; _offline = LvnNetworkStatus.ForceOffline;
            LvnNetworkStatus.ForceOffline = true;
            LvnBackend.BaseUrl = "";
            LvnWallet.ResetLocal();
            LvnAvatars.Picked = "";
            _manifest = JsonConvert.DeserializeObject<LvnManifest>(@"{""ui"":{""browse"":{
                ""avatar"":""/default.png"",""avatars"":[
                {""id"":""free"",""url"":""/free.png""},
                {""id"":""paid"",""url"":""/paid.png"",""currency"":""crystals"",""price"":150}]}}}");
        }
        [TearDown] public void TearDown()
        {
            LvnKeep.NoteOwner(_owner); LvnAvatars.Picked = _picked;
            LvnWallet.SyncPost = null; LvnWallet.ResetLocal();
            LvnBackend.BaseUrl = _url; LvnNetworkStatus.ForceOffline = _offline;
        }
        [Test] public async Task PurchasedSelectionSurvivesReloadAndDoesNotChargeTwice()
        {
            LvnWallet.Apply(@"{""balances"":{""crystals"":200},""inventory"":{}}");
            Assert.IsTrue(await LvnAvatars.ChooseAsync(_manifest, "paid"));
            Assert.AreEqual(50, LvnWallet.Balance("crystals"));
            Assert.IsTrue(LvnWallet.Has("avatar.paid"));
            LvnWallet.ReloadLocal();
            Assert.AreEqual("paid", LvnAvatars.Picked);
            Assert.AreEqual("/paid.png", LvnAvatars.Url(_manifest));
            Assert.IsTrue(await LvnAvatars.ChooseAsync(_manifest, "free"));
            Assert.IsTrue(await LvnAvatars.ChooseAsync(_manifest, "paid"));
            Assert.AreEqual(50, LvnWallet.Balance("crystals"));
            Assert.AreEqual(1, LvnWallet.PendingCount);
        }
        [Test] public async Task InsufficientFundsKeepThePreviousFace()
        {
            Assert.IsTrue(await LvnAvatars.ChooseAsync(_manifest, "free"));
            Assert.IsFalse(await LvnAvatars.ChooseAsync(_manifest, "paid"));
            Assert.AreEqual("free", LvnAvatars.Picked);
            Assert.AreEqual(0, LvnWallet.PendingCount);
        }
        [Test] public void RemovedOwnershipFallsBackToDefaultWithoutErasingTheSelection()
        {
            LvnAvatars.Picked = "paid";
            Assert.AreEqual("/default.png", LvnAvatars.Url(_manifest));
            LvnWallet.Apply(@"{""balances"":{},""inventory"":{""avatar.paid"":1}}");
            Assert.AreEqual("/paid.png", LvnAvatars.Url(_manifest));
        }
        [Test] public async Task AChangedAccountCannotInheritAnInFlightSelection()
        {
            LvnNetworkStatus.ForceOffline = false;
            LvnWallet.Apply(@"{""balances"":{""crystals"":200},""inventory"":{}}");
            var reply = new TaskCompletionSource<(long, string)>();
            LvnWallet.SyncPost = (_, __) => reply.Task;
            var choosing = LvnAvatars.ChooseAsync(_manifest, "paid");
            string other = "avatar-test-" + Guid.NewGuid().ToString("N");
            LvnKeep.NoteOwner(other);
            reply.SetResult((200, @"{""balances"":{""crystals"":50},""inventory"":{""avatar.paid"":1}}"));
            Assert.IsFalse(await choosing);
            Assert.AreEqual("", LvnAvatars.Picked);
        }
    }
}
