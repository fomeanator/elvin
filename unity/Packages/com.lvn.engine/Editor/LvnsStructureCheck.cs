using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn.Editor
{
    /// <summary>
    /// Structural checks the Unity import path was missing entirely.
    /// <para>
    /// `LvnsCompiler.Compile` catches SYNTAX and nothing else, so a script whose
    /// every line parses could still jump to a label that does not exist — and
    /// the README's promise ("dangling jumps … caught at build time, not in a
    /// player's hands") held only for the CLI (`lvnconv validate`), never for
    /// the advertised Unity flow of dropping a `.lvns` into Assets/.
    /// </para>
    /// <para>
    /// Deliberately limited to the two checks that need NO op vocabulary:
    /// a dangling jump and a duplicate label. Detecting an *unknown op* would
    /// require a list of valid ops on this side, i.e. yet another copy of a
    /// registry that already exists in four places and has drifted before
    /// (see conformance/ops-owners.json). One is not added here on purpose.
    /// </para>
    /// </summary>
    public static class LvnsStructureCheck
    {
        /// <summary>Built-in jump target: always valid, never declared.</summary>
        private const string EndLabel = "__end";

        /// <summary>
        /// Returns one message per structural problem; empty when the document is
        /// sound. Never throws — a malformed document is the caller's business.
        /// </summary>
        public static List<string> Run(JArray script)
        {
            var problems = new List<string>();
            if (script == null)
                return problems;

            // Pass 1: collect labels, reporting duplicates. A duplicate id makes
            // every jump to it ambiguous, and which one wins is an implementation
            // detail no author should have to know.
            var labels = new HashSet<string>();
            for (int i = 0; i < script.Count; i++)
            {
                var c = script[i] as JObject;
                if (c == null || (string)c["op"] != "label")
                    continue;
                var id = (string)c["id"];
                if (string.IsNullOrEmpty(id))
                {
                    problems.Add($"script[{i}] label: missing id");
                    continue;
                }
                if (!labels.Add(id))
                    problems.Add($"script[{i}] label: duplicate id \"{id}\"");
            }

            // Pass 2: every jump must land somewhere. Covers the same targets the
            // Go validator does, including the ones that hide inside options and
            // clickable objects — those are exactly where a dangling jump survives
            // review, because nothing on screen hints the branch is broken.
            for (int i = 0; i < script.Count; i++)
            {
                var c = script[i] as JObject;
                if (c == null)
                    continue;
                var op = (string)c["op"] ?? "";
                switch (op)
                {
                    case "goto":
                    case "call":
                        Check(problems, labels, i, op, (string)c["label"]);
                        break;
                    case "if":
                        Check(problems, labels, i, op, (string)c["then"]);
                        Check(problems, labels, i, op, (string)c["else"]);
                        break;
                    case "choice":
                        if (c["options"] is JArray opts)
                        {
                            for (int k = 0; k < opts.Count; k++)
                            {
                                if (!(opts[k] is JObject o))
                                    continue;
                                Check(problems, labels, i, $"choice option {k}", (string)o["goto"]);
                                if (o["body"] is JArray body)
                                    foreach (var b in body)
                                        if (b is JObject bo && (string)bo["op"] == "goto")
                                            Check(problems, labels, i, $"choice option {k} body", (string)bo["label"]);
                            }
                        }
                        break;
                    case "actor":
                    case "obj":
                        Check(problems, labels, i, op + " on_click", (string)c["on_click"]);
                        break;
                }
            }
            return problems;
        }

        private static void Check(List<string> problems, HashSet<string> labels, int i, string what, string target)
        {
            if (string.IsNullOrEmpty(target) || target == EndLabel || labels.Contains(target))
                return;
            problems.Add($"script[{i}] {what}: jump to a label that does not exist — \"{target}\"");
        }
    }
}
