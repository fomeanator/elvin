namespace Lvn.UI.Screens
{
    /// <summary>Use the bundled manifest only after the actual fetch failed
    /// and no server or cached manifest was resolved. A failed connectivity
    /// probe alone is insufficient: the in-flight fetch must finish first.</summary>
    internal static class ManifestSeedFallback
    {
        public static bool ShouldTry(bool fetchFailed, bool hasManifest)
            => fetchFailed && !hasManifest;
    }
}
