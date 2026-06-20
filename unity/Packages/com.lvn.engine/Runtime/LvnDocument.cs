using System;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// A parsed .lvn container: an optional scene id and the flat command list.
    /// Commands are kept as <see cref="JObject"/>s so a host can read any field
    /// without the engine modelling every op as a C# type.
    /// </summary>
    public sealed class LvnDocument
    {
        public string Scene { get; }
        public JArray Script { get; }

        private LvnDocument(string scene, JArray script)
        {
            Scene = scene;
            Script = script ?? new JArray();
        }

        /// <summary>Parse a .lvn document from JSON text.</summary>
        public static LvnDocument Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("empty .lvn", nameof(json));

            var root = JObject.Parse(json);
            var scene = (string)root["scene"];
            var script = root["script"] as JArray
                         ?? throw new FormatException(".lvn has no \"script\" array");
            return new LvnDocument(scene, script);
        }
    }
}
