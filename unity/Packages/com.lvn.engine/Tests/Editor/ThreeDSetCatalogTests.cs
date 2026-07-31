using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class ThreeDSetCatalogTests
    {
        [Test]
        public void Manifest_DeserializesPlatformBundleAndOfflineFallback()
        {
            const string json = @"{
              ""sets3d"": {
                ""forest"": {
                  ""fallback_resource"": ""Sets/forest"",
                  ""platforms"": {
                    ""android"": {
                      ""url"": ""/content/sets/forest.android.bundle"",
                      ""asset"": ""forest"",
                      ""hash"": ""abc123"",
                      ""bytes"": 42
                    }
                  }
                }
              }
            }";

            var manifest = JsonConvert.DeserializeObject<LvnManifest>(json);
            var set = manifest.sets3d["forest"];
            var bundle = CachingAssets.Select3DBundle(set, "android");

            Assert.AreEqual("Sets/forest", set.fallback_resource);
            Assert.AreEqual("/content/sets/forest.android.bundle", bundle.url);
            Assert.AreEqual("forest", bundle.asset);
            Assert.AreEqual("abc123", bundle.hash);
            Assert.AreEqual(42, bundle.bytes);
        }

        [Test]
        public void BundleSelection_UsesDefaultOnlyWhenExactPlatformIsAbsent()
        {
            var android = new Lvn3DBundle { url = "android" };
            var fallback = new Lvn3DBundle { url = "default" };
            var set = new Lvn3DSet
            {
                platforms = new Dictionary<string, Lvn3DBundle>
                {
                    ["android"] = android,
                    ["default"] = fallback,
                }
            };

            Assert.AreSame(android, CachingAssets.Select3DBundle(set, "android"));
            Assert.AreSame(fallback, CachingAssets.Select3DBundle(set, "linux"));
        }

        [Test]
        public void SetAsset_ReleaseLeaseExactlyOnce()
        {
            var prefab = new GameObject("leased-set");
            var releases = 0;
            var asset = new Lvn3DSetAsset("set", prefab, remote: true,
                release: () => releases++);

            asset.Dispose();
            asset.Dispose();

            Assert.AreEqual(1, releases);
            Object.DestroyImmediate(prefab);
        }
    }
}
