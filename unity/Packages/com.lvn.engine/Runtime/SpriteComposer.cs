using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// A named, parametric sprite entity: ordered full-frame layer URL templates
    /// plus default axis values. See <c>docs/cast.md</c> for the model.
    /// </summary>
    public sealed class CastEntity
    {
        public string Id;
        public string Name;
        public IReadOnlyList<string> Layers;
        public IReadOnlyDictionary<string, string> Defaults;
    }

    /// <summary>
    /// The whole sprite system in one rule: a character is a list of URL
    /// templates parameterised by named axes; to draw it in a state, fill the
    /// templates from the axis values (defaults overlaid by the command) and
    /// stack the layers. Pure — the substitution is engine-agnostic, so any
    /// runtime implements the same model in a few lines.
    /// </summary>
    public static class SpriteComposer
    {
        /// <summary>Parse a <c>cast</c> block (id → definition) into entities.</summary>
        public static Dictionary<string, CastEntity> ParseCast(JObject cast)
        {
            var map = new Dictionary<string, CastEntity>();
            if (cast == null) return map;
            foreach (var prop in cast.Properties())
            {
                if (!(prop.Value is JObject def)) continue;

                var layers = new List<string>();
                if (def["layers"] is JArray arr)
                    foreach (var l in arr)
                    {
                        var s = (string)l;
                        if (!string.IsNullOrEmpty(s)) layers.Add(s);
                    }

                var defaults = new Dictionary<string, string>();
                if (def["defaults"] is JObject d)
                    foreach (var dp in d.Properties())
                        defaults[dp.Name] = dp.Value?.ToString();

                map[prop.Name] = new CastEntity
                {
                    Id = prop.Name,
                    Name = (string)def["name"],
                    Layers = layers,
                    Defaults = defaults,
                };
            }
            return map;
        }

        /// <summary>
        /// Resolve the ordered layer URLs for <paramref name="entity"/> in the
        /// state given by <paramref name="axes"/> (which override the entity's
        /// defaults). A layer with an unresolved <c>{token}</c> is skipped, so
        /// optional parts simply don't appear until an axis supplies them.
        /// </summary>
        public static List<string> Resolve(CastEntity entity, IReadOnlyDictionary<string, string> axes)
        {
            var urls = new List<string>();
            if (entity?.Layers == null) return urls;
            foreach (var template in entity.Layers)
            {
                var filled = LayerTemplate.Fill(template, axes, entity.Defaults);
                if (filled != null) urls.Add(filled);
            }
            return urls;
        }
    }
}
