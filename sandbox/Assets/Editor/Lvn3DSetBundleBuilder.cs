using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Exports every Assets/ServerSets/*.prefab (remote-only) and
/// Assets/Resources/Sets/*.prefab (offline fallback) as an independently
/// replaceable, platform-specific AssetBundle and writes its descriptor into
/// the server's live manifest.
/// </summary>
public static class Lvn3DSetBundleBuilder
{
    [MenuItem("Elvin/3D Sets/Build Android bundles")]
    public static void Android() => Build(BuildTarget.Android, "android");

    [MenuItem("Elvin/3D Sets/Build current platform bundles")]
    public static void Current()
    {
        var target = EditorUserBuildSettings.activeBuildTarget;
        Build(target, PlatformKey(target));
    }

    /// <summary>Batch-mode entry point:
    /// -executeMethod Lvn3DSetBundleBuilder.Android</summary>
    public static void BuildAndroid() => Android();

    private static void Build(BuildTarget target, string platform)
    {
        if (string.IsNullOrEmpty(platform))
            throw new InvalidOperationException($"Unsupported 3D bundle target: {target}");

        var fallbackRoot = Path.Combine(Application.dataPath, "Resources", "Sets");
        var remoteRoot = Path.Combine(Application.dataPath, "ServerSets");
        var prefabs = new[] { fallbackRoot, remoteRoot }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(
                root, "*.prefab", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (prefabs.Length == 0)
            throw new InvalidOperationException(
                "No prefabs in Assets/ServerSets or Assets/Resources/Sets");
        var duplicate = prefabs.GroupBy(Path.GetFileNameWithoutExtension)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException(
                $"Duplicate 3D set id '{duplicate.Key}' in authoring roots");
        var fallbackIds = new HashSet<string>(
            prefabs.Where(path => path.StartsWith(fallbackRoot, StringComparison.Ordinal))
                .Select(Path.GetFileNameWithoutExtension),
            StringComparer.Ordinal);

        var builds = prefabs.Select(path =>
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var assetPath = "Assets" + path.Substring(Application.dataPath.Length)
                .Replace('\\', '/');
            return new AssetBundleBuild
            {
                assetBundleName = $"{id}.{platform}.bundle",
                assetNames = new[] { assetPath },
                addressableNames = new[] { id },
            };
        }).ToArray();

        // Absolute project-local output: a relative path depends on the process
        // working directory and made an interactive Editor build race its Temp
        // folder during asset import.
        var temp = Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "Library", "Lvn3DSets", platform));
        Directory.CreateDirectory(temp);
        var built = BuildPipeline.BuildAssetBundles(
            temp, builds,
            BuildAssetBundleOptions.ChunkBasedCompression |
            BuildAssetBundleOptions.StrictMode,
            target);
        if (built == null) throw new InvalidOperationException("AssetBundle build failed");

        var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        var contentSets = Path.Combine(repo, "server", "content", "sets");
        Directory.CreateDirectory(contentSets);
        var descriptors = new Dictionary<string, BundleDescriptor>();

        foreach (var build in builds)
        {
            var id = Path.GetFileName(build.assetNames[0]);
            id = Path.GetFileNameWithoutExtension(id);
            var source = Path.Combine(temp, build.assetBundleName);
            var destination = Path.Combine(contentSets, build.assetBundleName);
            File.Copy(source, destination, true);
            descriptors[id] = new BundleDescriptor
            {
                url = "/content/sets/" + build.assetBundleName,
                asset = id,
                hash = built.GetAssetBundleHash(build.assetBundleName).ToString(),
                bytes = new FileInfo(destination).Length,
            };
            Debug.Log($"[bg3d-build] {id}/{platform}: {descriptors[id].bytes:N0} bytes, " +
                      descriptors[id].hash);
        }

        UpdateServerManifest(repo, platform, descriptors, fallbackIds);
        Debug.Log($"[bg3d-build] {descriptors.Count} set bundle(s) published to {contentSets}");
    }

    private static void UpdateServerManifest(
        string repo, string platform, Dictionary<string, BundleDescriptor> descriptors,
        HashSet<string> fallbackIds)
    {
        var path = Path.Combine(repo, "server", "content", "manifest.json");
        var root = JObject.Parse(File.ReadAllText(path));
        var sets = root["sets3d"] as JObject;
        if (sets == null)
        {
            sets = new JObject();
            root.AddFirst(new JProperty("sets3d", sets));
        }

        foreach (var pair in descriptors)
        {
            var entry = sets[pair.Key] as JObject ?? new JObject();
            if (fallbackIds.Contains(pair.Key))
                entry["fallback_resource"] = "Sets/" + pair.Key;
            else
                entry.Remove("fallback_resource");
            var platforms = entry["platforms"] as JObject ?? new JObject();
            platforms[platform] = JObject.FromObject(pair.Value);
            entry["platforms"] = platforms;
            sets[pair.Key] = entry;
        }

        File.WriteAllText(path, root.ToString(Formatting.Indented) + Environment.NewLine);
    }

    private static string PlatformKey(BuildTarget target)
    {
        switch (target)
        {
            case BuildTarget.Android: return "android";
            case BuildTarget.iOS: return "ios";
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64: return "windows";
            case BuildTarget.StandaloneOSX: return "macos";
            case BuildTarget.StandaloneLinux64: return "linux";
            case BuildTarget.WebGL: return "webgl";
            default: return null;
        }
    }

    [Serializable]
    private sealed class BundleDescriptor
    {
        public string url;
        public string asset;
        public string hash;
        public long bytes;
    }
}
