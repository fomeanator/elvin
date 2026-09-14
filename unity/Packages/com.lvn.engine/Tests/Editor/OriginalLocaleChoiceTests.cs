using System.Collections.Generic;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class OriginalLocaleChoiceTests
    {
        private const string Key = "lvn_pref_locale";
        private bool _chosen;
        private string _locale;
        private IReadOnlyList<string> _languages;

        [SetUp] public void SetUp()
        {
            _chosen = LvnPrefs.LocaleChosen; _locale = LvnPrefs.Locale;
            _languages = LvnPrefs.AvailableLocales;
            PlayerPrefs.DeleteKey(Key); LvnPrefs.Reload();
            LvnPrefs.AvailableLocales = new[] { "en" };
        }

        [TearDown] public void TearDown()
        {
            if (_chosen) PlayerPrefs.SetString(Key, _locale); else PlayerPrefs.DeleteKey(Key);
            LvnPrefs.Reload(); LvnPrefs.AvailableLocales = _languages;
        }

        [Test] public void ChoosingRussianOriginalFromFreshAutoPersistsAndNotifies()
        {
            Assert.IsFalse(LvnPrefs.LocaleChosen);
            Assert.AreEqual("", LvnPrefs.Locale, "fresh backing field equals the original choice");
            int calls = 0;
            void Changed() => calls++;
            LvnPrefs.Changed += Changed;
            try { LvnLocale.Chosen = LvnLocale.Original; }
            finally { LvnPrefs.Changed -= Changed; }
            Assert.IsTrue(LvnPrefs.LocaleChosen, "a click must leave Auto even when the backing value was already empty");
            Assert.AreEqual(1, calls, "the running host must hear the explicit choice");
            Assert.AreEqual(LvnLocale.Original, LvnLocale.Effective);
            LvnPrefs.Reload();
            Assert.AreEqual(LvnLocale.Original, LvnLocale.Chosen, "restart must not restore Auto");
        }

        [Test] public void ExplicitOriginalOverridesEnglishBuildDefault()
        {
            var go = new GameObject("locale-default"); go.SetActive(false);
            try
            {
                var app = go.AddComponent<NovelApp>(); app.Locale = "en";
                LvnLocale.Chosen = "en";
                Assert.AreEqual("en", app.CurrentLocale);
                LvnLocale.Chosen = LvnLocale.Original;
                Assert.AreEqual("", app.CurrentLocale, "empty is a valid player choice, not a missing preference");
                LvnPrefs.Reload();
                Assert.AreEqual("", app.CurrentLocale);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void RepeatedOriginalClickDoesNotRebuildTheInterfaceAgain()
        {
            LvnLocale.Chosen = "";
            int calls = 0;
            void Changed() => calls++;
            LvnPrefs.Changed += Changed;
            try { LvnLocale.Chosen = ""; }
            finally { LvnPrefs.Changed -= Changed; }
            Assert.AreEqual(0, calls);
        }
    }
}
