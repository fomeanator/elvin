using System.Reflection;
using System.Threading.Tasks;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class DeferredContentSyncTests
    {
        [Test] public async Task ContentNotificationDuringChapterDoesNotTouchSceneOrNetwork()
        {
            var go = new GameObject("deferred-content-test");
            go.SetActive(false); // no boot; deliberately no loader/shell/stage
            bool previous = LvnScreenDirector.Current.InChapter;
            try
            {
                var app = go.AddComponent<NovelApp>();
                Assert.IsFalse(app.LiveChapterUpdates, "shipping default must not hot-edit a running story");
                LvnScreenDirector.Current.AnnounceChapter(true);
                var method = typeof(NovelApp).GetMethod("OnContentChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                await (Task)method.Invoke(app, null);
                var pending = typeof(NovelApp).GetField("_deferredContentUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsTrue((bool)pending.GetValue(app), "the update must be remembered, not discarded");
                // Missing dependencies would fault if the callback attempted
                // a fetch, manifest adoption or scene replay during the chapter.
            }
            finally
            {
                Object.DestroyImmediate(go);
                LvnScreenDirector.Current.AnnounceChapter(previous);
            }
        }
    }
}
