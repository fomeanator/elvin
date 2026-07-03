using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lvn.Content
{
    /// <summary>
    /// A single composite layer: a URL template (with optional <c>{axis}</c>
    /// tokens) and an optional <c>when</c> condition. The layer is drawn only when
    /// its tokens resolve and its condition holds. Serialises as a bare string
    /// when unconditional (so simple sprites stay terse), or as <c>{url, when}</c>
    /// when conditional — both forms parse.
    /// </summary>
    [JsonConverter(typeof(LvnLayerConverter))]
    public sealed class LvnLayer
    {
        public string url;
        public string when;
        /// <summary>Optional layer id (e.g. <c>eyes</c>, <c>mouth</c>) so animations
        /// can target this layer for blink / lip-sync.</summary>
        public string id;

        /// <summary>Optional placement of this layer within the actor's box, as
        /// fractions (0..1): x,y = top-left; w,h = size. When w or h is ≤ 0 (the
        /// default) the layer fills the whole box — so a full-frame sprite or a
        /// full-frame overlay needs nothing, while a PARTIAL overlay (a face/mouth
        /// crop that must sit at one spot) sets its rect. This is what makes the
        /// character system accept any art: whole sprites, stacked full-frame
        /// overlays, and partial overlays alike.</summary>
        public float x, y, w, h;

        /// <summary>Bone hierarchy: the id of the layer this one is ATTACHED to.
        /// A parented layer inherits the whole ancestor chain's rotation/scale/
        /// movement, composed around each ancestor's pivot — a paper-doll FK
        /// skeleton over plain sprite layers. Draw order stays the layer list
        /// order (a back arm can be the body's child yet draw behind it).</summary>
        public string parent;

        /// <summary>The pivot — this layer's own rotation/scale origin AND the
        /// joint the parent swings it around — as fractions of the LAYER's rect
        /// (0..1). Default = centre. For an arm, put it at the shoulder.</summary>
        public float px = 0.5f, py = 0.5f;

        /// <summary>Secondary motion: &gt; 0 turns the joint into a spring that
        /// LAGS behind the parent's movement (hair, tails, cloth) — the layer
        /// swings from motion and settles by itself, no keyframes. Typical 4–20.</summary>
        public float spring;

        /// <summary>Spring energy loss (only with <see cref="spring"/>).
        /// Higher = calms faster. Default 6.</summary>
        public float damping = 6f;

        public LvnLayer() { }
        public LvnLayer(string url, string when = null, string id = null) { this.url = url; this.when = when; this.id = id; }

        /// <summary>True when this layer has an explicit sub-rect (a partial overlay).</summary>
        public bool HasRect => w > 0f && h > 0f;
    }

    internal sealed class LvnLayerConverter : JsonConverter
    {
        public override bool CanConvert(Type t) => t == typeof(LvnLayer);

        public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
        {
            var tok = JToken.Load(r);
            if (tok.Type == JTokenType.String) return new LvnLayer((string)tok);
            if (tok is JObject o)
            {
                var l = new LvnLayer((string)(o["url"] ?? o["template"]), (string)o["when"], (string)o["id"]);
                l.x = (float?)o["x"] ?? 0f;
                l.y = (float?)o["y"] ?? 0f;
                l.w = (float?)o["w"] ?? 0f;
                l.h = (float?)o["h"] ?? 0f;
                l.parent = (string)o["parent"];
                l.px = (float?)o["px"] ?? 0.5f;
                l.py = (float?)o["py"] ?? 0.5f;
                l.spring = (float?)o["spring"] ?? 0f;
                l.damping = (float?)o["damping"] ?? 6f;
                return l;
            }
            return new LvnLayer(null);
        }

        public override void WriteJson(JsonWriter w, object value, JsonSerializer s)
        {
            var l = (LvnLayer)value;
            bool boned = !string.IsNullOrEmpty(l.parent) || l.spring > 0f || l.px != 0.5f || l.py != 0.5f;
            if (string.IsNullOrEmpty(l.when) && string.IsNullOrEmpty(l.id) && !l.HasRect && !boned) { w.WriteValue(l.url); return; }
            w.WriteStartObject();
            w.WritePropertyName("url"); w.WriteValue(l.url);
            if (!string.IsNullOrEmpty(l.when)) { w.WritePropertyName("when"); w.WriteValue(l.when); }
            if (!string.IsNullOrEmpty(l.id)) { w.WritePropertyName("id"); w.WriteValue(l.id); }
            if (l.HasRect)
            {
                w.WritePropertyName("x"); w.WriteValue(l.x);
                w.WritePropertyName("y"); w.WriteValue(l.y);
                w.WritePropertyName("w"); w.WriteValue(l.w);
                w.WritePropertyName("h"); w.WriteValue(l.h);
            }
            if (!string.IsNullOrEmpty(l.parent)) { w.WritePropertyName("parent"); w.WriteValue(l.parent); }
            if (l.px != 0.5f) { w.WritePropertyName("px"); w.WriteValue(l.px); }
            if (l.py != 0.5f) { w.WritePropertyName("py"); w.WriteValue(l.py); }
            if (l.spring > 0f)
            {
                w.WritePropertyName("spring"); w.WriteValue(l.spring);
                w.WritePropertyName("damping"); w.WriteValue(l.damping);
            }
            w.WriteEndObject();
        }
    }

    /// <summary>
    /// The sprite/entity catalog — resolves an id to its ordered layer urls in a
    /// given state, backing <c>actor</c>/<c>obj</c>/<c>bg id="..."</c>. Pure: axis
    /// substitution (command values over entity defaults) + conditional <c>when</c>
    /// filtering. The condition evaluation is supplied by the host, so it can use
    /// the player's vars and the engine's expression evaluator without coupling
    /// this resolver to them — and tests drive it with a fake evaluator.
    /// </summary>
    public sealed class SpriteCatalog
    {
        private readonly IReadOnlyDictionary<string, LvnSpriteEntity> _entities;

        public SpriteCatalog(IReadOnlyDictionary<string, LvnSpriteEntity> entities)
        {
            _entities = entities ?? new Dictionary<string, LvnSpriteEntity>();
        }

        public bool Has(string id) => !string.IsNullOrEmpty(id) && _entities.ContainsKey(id);

        public LvnSpriteEntity Get(string id) =>
            (id != null && _entities.TryGetValue(id, out var e)) ? e : null;

        /// <summary>Resolve <paramref name="id"/> to its ordered layer urls in the
        /// state given by <paramref name="axes"/> (overlaid on the entity's
        /// defaults). <paramref name="cond"/> evaluates a layer's <c>when</c> —
        /// return true to include it (null → all conditional layers shown). A
        /// layer with an unresolved <c>{token}</c> or a false <c>when</c> is
        /// skipped.</summary>
        public List<string> Resolve(string id, IReadOnlyDictionary<string, string> axes, Func<string, bool> cond = null)
        {
            var urls = new List<string>();
            foreach (var rl in ResolveLayers(id, axes, cond)) urls.Add(rl.Url);
            return urls;
        }

        /// <summary>A resolved layer: its filled url plus its catalog id (for
        /// per-layer animation targeting); id is null for anonymous layers.</summary>
        public struct ResolvedLayer
        {
            public string Url; public string Id; public float X, Y, W, H;
            // bone metadata (see LvnLayer.parent/px/py/spring/damping)
            public string Parent; public float Px, Py, Spring, Damping;
        }

        /// <summary>Like <see cref="Resolve"/> but also returns each layer's id, so
        /// the runtime can map a sprite to a named layer (eyes/mouth) for blink and
        /// lip-sync.</summary>
        public List<ResolvedLayer> ResolveLayers(string id, IReadOnlyDictionary<string, string> axes, Func<string, bool> cond = null)
        {
            var outl = new List<ResolvedLayer>();
            var e = Get(id);
            if (e?.layers == null) return outl;
            foreach (var layer in e.layers)
            {
                if (layer == null || string.IsNullOrEmpty(layer.url)) continue;
                if (!string.IsNullOrEmpty(layer.when) && cond != null && !cond(layer.when)) continue;
                var filled = Fill(layer.url, axes, e.defaults);
                if (filled != null) outl.Add(new ResolvedLayer
                {
                    Url = filled, Id = layer.id, X = layer.x, Y = layer.y, W = layer.w, H = layer.h,
                    Parent = layer.parent, Px = layer.px, Py = layer.py, Spring = layer.spring, Damping = layer.damping,
                });
            }
            return outl;
        }

        /// <summary>Fill a single layer-url template with the given axes (over the
        /// entity's defaults). Exposed so the runtime can resolve a frame variant
        /// (e.g. eyes=closed) for blink/lip-sync. Returns null if a token is unset.</summary>
        public string FillFor(string id, string template, IReadOnlyDictionary<string, string> axes)
        {
            var e = Get(id);
            return Fill(template, axes, e?.defaults);
        }

        private static string Fill(string template,
            IReadOnlyDictionary<string, string> axes, IReadOnlyDictionary<string, string> defaults)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template;
            var sb = new StringBuilder(template.Length);
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c != '{') { sb.Append(c); i++; continue; }
                int end = template.IndexOf('}', i + 1);
                if (end < 0) { sb.Append(template, i, template.Length - i); break; }
                var key = template.Substring(i + 1, end - i - 1);
                string val = null;
                axes?.TryGetValue(key, out val);
                if (string.IsNullOrEmpty(val)) defaults?.TryGetValue(key, out val);
                if (string.IsNullOrEmpty(val)) return null; // unresolved → drop layer
                sb.Append(val);
                i = end + 1;
            }
            return sb.ToString();
        }
    }
}
