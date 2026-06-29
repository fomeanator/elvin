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
            string src = File.ReadAllText(ctx.assetPath);
            string json;
            try
            {
                json = LvnsCompiler.Compile(src);
            }
            catch (LvnsCompileException e)
            {
                // Surface the failure as a real import error (mirrors the Go rule:
                // a malformed script is an error, never a silent skip), but still
                // produce an empty-but-valid asset so the import doesn't hard-fail.
                ctx.LogImportError($"LVNScript compile error in {Path.GetFileName(ctx.assetPath)}: {e.Message}");
                json = "{\"script\":[]}";
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
