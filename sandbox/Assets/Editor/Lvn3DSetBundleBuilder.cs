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

    /// <summary>Набор для РЕДАКТОРА. Проверять сцену на маке приходится чаще,
    /// чем собирать под телефон, а «текущая платформа» у проекта — Android:
    /// без отдельной точки входа редактор всегда получал вчерашний бандл, и
    /// правки набора проверялись вслепую.</summary>
    public static void BuildMac() => Build(BuildTarget.StandaloneOSX, "macos");

    private static void Build(BuildTarget target, string platform)
    {
        if (string.IsNullOrEmpty(platform))
            throw new InvalidOperationException($"Unsupported 3D bundle target: {target}");

        var fallbackRoot = Path.Combine(Application.dataPath, "Resources", "Sets");
        var remoteRoot = Path.Combine(Application.dataPath, "ServerSets");
        var prefabs = new[] { fallbackRoot, remoteRoot }
            .Where(Directory.Exists)
            // .unity наравне с .prefab: набор, приехавший из покупной сцены,
            // пакуется СЦЕНОВЫМ бандлом — только так уезжают террейн, деревья
            // и трава, которых префаб не видит.
            .SelectMany(root => Directory.GetFiles(root, "*.prefab", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.GetFiles(root, "*.unity", SearchOption.TopDirectoryOnly)))
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
            // У сценового бандла адрес — путь сцены, а не короткое имя:
            // SceneManager грузит её именно по нему.
            bool isScene = assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
            return new AssetBundleBuild
            {
                assetBundleName = $"{id}.{platform}.bundle",
                assetNames = new[] { assetPath },
                addressableNames = isScene ? null : new[] { id },
            };
        }).ToArray();

        // Absolute project-local output: a relative path depends on the process
        // working directory and made an interactive Editor build race its Temp
        // folder during asset import.
        // НЕ в Library: как только в сборке появляется сцена, Unity отвечает
        // «Building to the Library folder is not allowed» и роняет весь вызов.
        var temp = Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "Build", "Lvn3DSets", platform));
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
                scene = build.assetNames[0].EndsWith(".unity", StringComparison.OrdinalIgnoreCase),
                asset = id,
                hash = built.GetAssetBundleHash(build.assetBundleName).ToString(),
                bytes = new FileInfo(destination).Length,
                models = ModelNames(build.assetNames[0]),
            };
            Debug.Log($"[bg3d-build] {id}/{platform}: {descriptors[id].bytes:N0} bytes, " +
                      descriptors[id].hash);
        }

        AuditSets(prefabs, descriptors);
        UpdateServerManifest(repo, platform, descriptors, fallbackIds);
        Debug.Log($"[bg3d-build] {descriptors.Count} set bundle(s) published to {contentSets}");
    }

    /// <summary>Сверка набора с бюджетами (docs/3d-set-rules.md).
    ///
    /// <para>Предупреждения, а не запреты: набор соберётся в любом случае, но
    /// автор узнает цену ДО того, как игрок будет ждать восемь секунд первого
    /// кадра. Молчаливое превышение — как раз то, из-за чего кузница весит 68
    /// МБ и никого это не смущало.</para></summary>
    private static void AuditSets(string[] prefabs, Dictionary<string, BundleDescriptor> descriptors)
    {
        foreach (var path in prefabs)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var assetPath = "Assets" + path.Substring(Application.dataPath.Length).Replace('\\', '/');
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go == null) continue; // сцена — считать по ней нечего, пропускаем

            int renderers = 0, tris = 0;
            var mats = new HashSet<Material>();
            var texes = new HashSet<Texture>();
            var batches = new HashSet<string>();   // уникальные пары «меш + материал»
            bool instanced = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                var rmf = r.GetComponent<MeshFilter>();
                var meshName = rmf != null && rmf.sharedMesh != null ? rmf.sharedMesh.name : "?";
                foreach (var m0 in r.sharedMaterials)
                {
                    if (m0 == null) continue;
                    batches.Add(meshName + "|" + m0.name);
                    if (!m0.enableInstancing) instanced = false;
                }
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    mats.Add(m);
                    if (m.mainTexture != null) texes.Add(m.mainTexture);
                }
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) tris += mf.sharedMesh.triangles.Length / 3;
            }
            int lights = go.GetComponentsInChildren<Light>(true).Length;
            int colliders = go.GetComponentsInChildren<Collider>(true).Length;
            long bytes = descriptors.TryGetValue(id, out var d) ? d.bytes : 0;

            void Over(string what, long got, long soft, long hard)
            {
                if (got <= soft) return;
                var how = got > hard ? "ПРЕВЫШЕН ПОТОЛОК" : "выше нормы";
                Debug.LogWarning($"[bg3d-audit] {id}: {what} {got} — {how} ({soft}/{hard}). " +
                                 "См. docs/3d-set-rules.md");
            }
            Over("размер бандла, МБ", bytes / (1024 * 1024), 8, 20);
            // Считаем ПАЧКИ, а не объекты: с инстансингом сотня одинаковых
            // сосен стоит одного вызова, и ругаться на их число значит
            // требовать бедную сцену там, где GPU не заметит разницы.
            Over("пачек отрисовки", batches.Count, 150, 400);
            if (!instanced && renderers > 200)
                Debug.LogWarning($"[bg3d-audit] {id}: {renderers} рендереров БЕЗ инстансинга — " +
                                 "включите enableInstancing на материалах, иначе это столько же вызовов");
            Over("материалов", mats.Count, 20, 40);
            Over("треугольников, тыс.", tris / 1000, 150, 400);
            Over("текстур", texes.Count, 30, 60);
            Over("источников света", lights, 2, 3);
            if (colliders > 0)
                Debug.LogWarning($"[bg3d-audit] {id}: коллайдеров {colliders} — фону они не нужны, " +
                                 "а их классы вырезаны из сборки (ID 64/136 в логе устройства)");
            Debug.Log($"[bg3d-audit] {id}: {batches.Count} пачек / {renderers} объектов, {tris / 1000} тыс. тр., " +
                      $"{mats.Count} мат., {texes.Count} текс., {lights} св., {bytes / 1024} КБ");
        }
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
        /// <summary>Бандл несёт СЦЕНУ, а не префаб: рантайм грузит её
        /// аддитивно. Только так доезжают террейн, его деревья и трава.</summary>
        public bool scene;
        public string asset;
        public string hash;
        public long bytes;
        /// <summary>Имена объектов набора — то, что автор пишет в
        /// <c>o3d model=…</c>. Список едет в манифест, и компилятор сверяет с
        /// ним каждую ссылку: опечатка в имени модели иначе тихая, тело просто
        /// не встаёт, а в логе ни слова.</summary>
        public string[] models;
    }

    /// <summary>Имена объектов первого уровня внутри набора — те, по которым
    /// скрипт достаёт модели. Вложенные части моделей не берём: автору нужен
    /// «crypt-a», а не «crypt-a/roof/tile.003».</summary>
    private static string[] ModelNames(string assetPath)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null) return System.Array.Empty<string>();
        var names = new List<string>();
        foreach (Transform child in prefab.transform)
        {
            names.Add(child.name);
            // Библиотека набора: модели часто лежат на «полке» одним слоем.
            foreach (Transform grand in child)
                names.Add(grand.name);
        }
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }
}
