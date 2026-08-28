using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Lvn.Editor
{
    /// <summary>
    /// Unity ScriptedImporter for LVNScript (<c>.lvns</c>) source files.
    ///
    /// Drop a <c>.lvns</c> file anywhere under <c>Assets/</c> and Unity compiles it
    /// to the <c>.lvn</c> container automatically (no external CLI, no server) — the
    /// imported asset is a <see cref="TextAsset"/> whose text is the compiled JSON,
    /// ready to hand to <c>VnStage</c>/<c>LvnPlayer</c>. Edit the source and Unity
    /// re-imports on the spot. This is the offline/bundled authoring path; the Go
    /// transcoder and the content server remain the live/served path.
    ///
    /// The compiler is a faithful C# port of the Go transcoder
    /// (<c>tools/lvnconv/internal/lvns/convert.go</c>); a shared golden corpus keeps
    /// the two implementations from drifting (see Tests/Editor).
    /// </summary>
    [ScriptedImporter(1, "lvns")]
    public class LvnsImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            string json;
            try
            {
                // ЧЕРЕЗ ПУТЬ, а не текст: `include` резолвится относительно
                // файла, и текстовая сборка превращала бы его в реплику.
                json = LvnsCompiler.CompileFile(ctx.assetPath);
            }
            catch (LvnsCompileException e)
            {
                // Surface the failure as a real import error (mirrors the Go rule:
                // a malformed script is an error, never a silent skip), but still
                // produce an empty-but-valid asset so the import doesn't hard-fail.
                ctx.LogImportError($"LVNScript compile error in {Path.GetFileName(ctx.assetPath)}: {e.Message}");
                json = "{\"script\":[]}";
            }

            // Compiling only proves the SYNTAX parsed. A script where every line
            // is well-formed can still jump into nothing — and that is the bug
            // that reaches players as "the chapter just ended". The CLI has
            // caught it for a long time (`lvnconv validate`); the Unity path,
            // which the README advertises as the two-minute way in, did not.
            try
            {
                var script = Newtonsoft.Json.Linq.JObject.Parse(json)["script"]
                    as Newtonsoft.Json.Linq.JArray;
                foreach (var problem in LvnsStructureCheck.Run(script))
                    ctx.LogImportError($"LVNScript in {Path.GetFileName(ctx.assetPath)}: {problem}");
            }
            catch (System.Exception e)
            {
                ctx.LogImportError($"LVNScript structure check failed in {Path.GetFileName(ctx.assetPath)}: {e.Message}");
            }

            var lvn = new TextAsset(json)
            {
                name = Path.GetFileNameWithoutExtension(ctx.assetPath),
            };
            ctx.AddObjectToAsset("lvn", lvn);
            ctx.SetMainObject(lvn);
        }
    }
}
