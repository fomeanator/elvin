using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// Locates the repository's cross-runtime conformance data (see
    /// <c>/conformance/README.md</c>). It lives outside every package on purpose —
    /// Go, C# and JS all read the same files — so the tests that use it have to find
    /// the repository root rather than an asset path.
    ///
    /// <para>Everything returns null when the data isn't there: a consumer who
    /// installed only the UPM package has no <c>/conformance</c>, and those tests
    /// ignore rather than fail. The repository's own CI is where the contract gates.</para>
    /// </summary>
    internal static class ConformanceCorpus
    {
        // Application.dataPath is <repo>/unity/TestHost/Assets, so the root is a few
        // hops up. Probing for the marker file (rather than counting hops) keeps this
        // working if the test project ever moves.
        internal static string Root()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "conformance", "ops-owners.json")))
                    return dir.FullName;
            return null;
        }

        internal static string CasesDir()
        {
            var root = Root();
            if (root == null) return null;
            var dir = Path.Combine(root, "conformance", "cases");
            return Directory.Exists(dir) ? dir : null;
        }

        /// <summary>The op → owning package / dispatch site table, or null.</summary>
        internal static JObject Owners()
        {
            var root = Root();
            if (root == null) return null;
            return (JObject)JObject.Parse(File.ReadAllText(
                Path.Combine(root, "conformance", "ops-owners.json")))["ops"];
        }

        internal const string Missing =
            "no /conformance data above the project — it ships with the repository, not with the UPM package";
    }
}
