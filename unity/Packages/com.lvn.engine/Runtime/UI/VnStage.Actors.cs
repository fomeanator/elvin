using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The actor/obj pipeline: layer resolution (catalog / cast / direct
    /// urls), sticky placement with smart slot arbitration, hotspot and drag
    /// arming, frame preloads and per-actor animations.
    /// </summary>
    public sealed partial class VnStage
    {
        internal static readonly HashSet<string> ReservedActorFields = new HashSet<string>
        {
            "op", "id", "show", "position", "x", "y", "width", "height", "scale",
            "anchor", "anchor_x", "anchor_y", "z", "flip", "mirror", "rotation", "opacity",
            "on_click", "hover_opacity", "breathing", "sprite_url", "body_url", "clothes_url", "hair_url",
            "transition", "transition_duration", "enter", "exit", "play",
        };

        // The last actor command per id — RefreshActor replays it so a wardrobe
        // change re-resolves the SAME pose/placement with the new equipment.
        private readonly Dictionary<string, JObject> _actorCmds = new Dictionary<string, JObject>();

        // Per-actor apply generation: rapid wardrobe browsing fires overlapping
        // ApplyActorAsync calls whose sprite loads finish out of order — only
        // the NEWEST may touch the renderer, or an older outfit "wins" by
        // arriving late.
        private readonly Dictionary<string, int> _actorGen = new Dictionary<string, int>();

        /// <summary>Re-apply an on-screen actor from its last command (art
        /// re-resolves against the current variables + wardrobe). No-op when
        /// the actor isn't on stage.</summary>
        public void RefreshActor(string id)
        {
            if (!string.IsNullOrEmpty(id) && _actorCmds.TryGetValue(id, out var cmd))
                _ = ApplyActorAsync(cmd);
        }

        /// <summary>Ensure an actor is ON stage — used by the in-story wardrobe so it
        /// always has the active hero to dress, even when the beat left the stage empty
        /// (imported novels open the wardrobe without staging anyone). Replays the
        /// actor's last pose forcing it visible, or stages it fresh (centred) from its
        /// catalog entity. No-op for an empty id.</summary>
        public void EnsureActorShown(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            // Already on stage (the story/import staged her) → do NOTHING. Re-applying
            // would reload the whole layered composite and lag the wardrobe open.
            if (_placements.TryGetValue(id, out var pl) && pl.Show) return;
            JObject cmd;
            if (_actorCmds.TryGetValue(id, out var last) && (string)last["op"] == "actor")
            {
                cmd = (JObject)last.DeepClone();
                cmd["show"] = true; // in case the last op hid her
            }
            else
            {
                cmd = new JObject { ["op"] = "actor", ["id"] = id, ["show"] = true, ["position"] = "center" };
            }
            _ = ApplyActorAsync(cmd);
        }

        /// <summary>Ids of actors currently VISIBLE on stage — hosts use it to
        /// pick who an always-open wardrobe should dress.</summary>
        public List<string> ActorsOnStage()
        {
            var list = new List<string>();
            foreach (var kv in _placements)
                if (kv.Value.Show) list.Add(kv.Key);
            return list;
        }

        /// <summary>Take an actor off stage (fade) — the counterpart of
        /// <see cref="EnsureActorShown"/> for a host that staged someone
        /// temporarily (the menu wardrobe) and wants the scene back as it was.</summary>
        public void HideActor(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _ = ApplyActorAsync(new JObject
            {
                ["op"] = "actor", ["id"] = id, ["show"] = false, ["exit"] = "fade",
            });
        }

        private void OnWardrobeChanged(string entity) => RefreshActor(entity);

        private async Task ApplyActorAsync(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;
            int epoch = _stageEpoch; // the scene this apply belongs to (see ResetStage)
            int gen = (_actorGen.TryGetValue(id, out var g) ? g : 0) + 1;
            _actorGen[id] = gen; // this call owns the actor until a newer one starts

            // Spine entities render through the optional spine-unity bridge —
            // a different pipeline entirely (runtime skeleton, own animations).
            var spineEntity = Catalog != null ? Catalog.Get(id) : null;
            if (spineEntity != null && spineEntity.kind == "spine" && spineEntity.spine != null)
            {
                await ApplySpineAsync(id, spineEntity, cmd);
                return;
            }

            // A HIDE needs no art — apply it immediately. Routing it through
            // the show pipeline made the exit WAIT for the very layer fetches
            // it was about to fade out; on a busy/stalled network the actor
            // lingered on stage for whole beats past her dismissal.
            if (!BoolOr(cmd["show"], true))
            {
                bool freshHide = !_placements.TryGetValue(id, out var prevHide);
                var hidePl = freshHide ? PlacementFrom(cmd, SlotsOf(id)) : PlacementFrom(cmd, prevHide, SlotsOf(id));

                if (!freshHide)
                {
                    // Both renderer paths: the Canvas renderer hides via
                    // PlaceActor (its ApplyActor ignores null layers), the
                    // UITK one via ApplyActor (its PlaceActor is a no-op).
                    _renderer?.PlaceActor(id, hidePl);
                    _renderer?.ApplyActor(id, null, hidePl, null, null, null);
                }
                _placements[id] = hidePl;
                _actorCmds[id] = cmd;
                _hotspots.RemoveAll(h => h.id == id);
                _draggables.Remove(id); // a hidden object must not be draggable
                return;
            }

            // Resolve the layer urls, in priority order:
            //   1. catalog id (manifest.sprites) — layered, with conditional `when`;
            //   2. per-doc cast block — layered by the command's axes;
            //   3. direct body/clothes/hair layers, or a single sprite_url.
            List<string> urls;
            List<string> urlIds = null;      // parallel layer ids (catalog path), for blink/lip-sync
            List<Vector4> urlRects = null;    // parallel per-layer sub-rects (x,y,w,h); w≤0 = fill
            List<SpriteCatalog.ResolvedLayer> urlDefs = null; // parallel full defs (bones: parent/pivot/spring)
            if (Catalog != null && Catalog.Has(id))
            {
                var axes = AxesOf(cmd);
                // An actual staging (not the preload scan, which never comes
                // through here) is the outfit "crossing the player's path" —
                // the always-open wardrobe's collection grows from these.
                foreach (var ax in axes) LvnWardrobe.MarkSeen(id, ax.Key, ax.Value);
                var rls = Catalog.ResolveLayers(id, axes, CatalogCond());
                urls = new List<string>(rls.Count);
                urlIds = new List<string>(rls.Count);
                urlRects = new List<Vector4>(rls.Count);
                urlDefs = rls;
                foreach (var rl in rls) { urls.Add(rl.Url); urlIds.Add(rl.Id); urlRects.Add(new Vector4(rl.X, rl.Y, rl.W, rl.H)); }
            }
            else if (_cast != null && _cast.TryGetValue(id, out var entity))
            {
                var axes = AxesFrom(cmd);
                foreach (var ax in axes) LvnWardrobe.MarkSeen(id, ax.Key, ax.Value);
                urls = SpriteComposer.Resolve(entity, axes);
            }
            else
            {
                urls = new List<string>();
                var body = (string)cmd["body_url"]; if (!string.IsNullOrEmpty(body)) urls.Add(body);
                var clothes = (string)cmd["clothes_url"]; if (!string.IsNullOrEmpty(clothes)) urls.Add(clothes);
                var hair = (string)cmd["hair_url"]; if (!string.IsNullOrEmpty(hair)) urls.Add(hair);
                if (urls.Count == 0)
                {
                    var sp = (string)cmd["sprite_url"]; if (!string.IsNullOrEmpty(sp)) urls.Add(sp);
                }
            }

            // Build the click action + placement SYNCHRONOUSLY (everything here runs
            // before the first `await` below). For the Canvas scene we also place the
            // actor and register its hotspot NOW — so it's clickable the instant the
            // obj command runs, before the next command (the room's narration `say`)
            // shows. Otherwise the hotspot armed only a few frames later (after the
            // async art load), and a tap in that gap fell through to "advance",
            // re-printing the room — the "first click does nothing" bug.
            System.Action onClick = null;
            var clickField = cmd["on_click"];
            if (clickField != null)
            {
                if (clickField.Type == JTokenType.Object)
                {
                    var clickObj = (JObject)clickField;
                    var target = (string)clickObj["goto"];
                    var setOps = clickObj["set"] as JObject;
                    onClick = () =>
                    {
                        if (_player == null) return;
                        if (setOps != null)
                        {
                            foreach (var prop in setOps.Properties())
                                _player.Vars[prop.Name] = prop.Value;
                        }
                        if (!string.IsNullOrEmpty(target))
                            _player.GoTo(target);
                        _awaitingTap = false;
                        _curChoices = null;
                        _choices.Dismiss();
                        _player.Advance();
                    };
                }
                else
                {
                    var clickTarget = (string)clickField;
                    if (!string.IsNullOrEmpty(clickTarget))
                        onClick = () =>
                        {
                            if (_player == null) return;
                            _player.GoTo(clickTarget);
                            _awaitingTap = false;
                            _curChoices = null;
                            _choices.Dismiss();
                            _player.Advance();
                        };
                }
            }

            bool fresh = !_placements.TryGetValue(id, out var prevPl);
            var placement = fresh ? PlacementFrom(cmd, SlotsOf(id)) : PlacementFrom(cmd, prevPl, SlotsOf(id));
            // Stage framing: on a FRESH actor, fill the theme's baseline/scale wherever
            // the op left it unset, so every novel gets the standard bottom-anchored
            // pose — tunable from ui.stage without editing the script. A follow-up op
            // inherits via the sticky merge above.
            if (Theme != null)
            {
                // Size/baseline seed the FIRST show; a sticky update inherits them from
                // the previous placement, so only apply on a fresh actor.
                if (fresh)
                {
                    if (cmd["y"] == null) placement.Y = Theme.ActorBaselineY;
                    if (cmd["width"] == null) placement.Width = Placement.DefaultWidth * Theme.ActorScale;
                    if (cmd["height"] == null) placement.Height = Placement.DefaultHeight * Theme.ActorScale;
                }
                // Spread must re-apply on EVERY op that positions by slot: the autostage
                // re-emits position= on each emotion change, so the sticky merge recomputes
                // X from SlotX (0.25/0.75) and would snap the actor back to the un-spread
                // column after the first line. Only when X came from position, not x=.
                if (cmd["x"] == null && cmd["position"] != null && Theme.ActorSpread != 1f)
                    placement.X = 0.5f + (placement.X - 0.5f) * Theme.ActorSpread;
            }
            // Layered/boned entities declare the aspect their art was authored in —
            // the renderer locks the box to it so layers register pixel-exact.
            var aspectEntity = Catalog != null ? Catalog.Get(id) : null;
            if (aspectEntity != null && aspectEntity.aspect > 0f)
                placement.BoxAspect = aspectEntity.aspect;

            // Smart slots: never draw two actors standing inside each other.
            if (placement.Show)
            {
                var arbX = ArbitrateSlotX(placement.X, id, cmd["x"] != null,
                    _placements, SlotsOf(id), out var slotOwner);
                if (slotOwner != null && !Mathf.Approximately(arbX, placement.X))
                {
                    Debug.Log($"[lvn-slot] '{id}' → {placement.X:0.00} занято '{slotOwner}' — авто-сдвиг в {arbX:0.00}");
                    placement.X = arbX;
                }
            }

            // Place first so the slot exists before the (async) art arrives — a
            // no-op on renderers that apply placement together with the art.
            _renderer?.PlaceActor(id, placement);
            _hotspots.RemoveAll(h => h.id == id);
            // Manual hotspot hit-testing only applies to renderers that expose
            // actor rects (the canvas path); the UITK path uses element picking.
            if (onClick != null && placement.Show && UseCanvasScene) _hotspots.Add((id, onClick));

            // Drag & drop: `draggable=true` arms the object; on_drop maps
            // target ids to labels ("bag:apple_in_bag"), on_drop_miss is the
            // released-anywhere-else branch (default: it just stays put).
            if (cmd["draggable"] != null)
            {
                if (BoolOr(cmd["draggable"], false))
                    _draggables[id] = new DragInfo
                    {
                        Home = placement,
                        Drop = ParseDropMap((string)cmd["on_drop"]),
                        MissLabel = (string)cmd["on_drop_miss"],
                        BoundToScreen = (string)cmd["drag_bounds"] != "none",
                    };
                else
                    _draggables.Remove(id);
            }

            // Now load the layer sprites (async) and set them on the placed actor.
            List<Sprite> layers = null;
            List<string> layerIds = null;
            List<Vector4> layerRects = null;
            List<SpriteCatalog.ResolvedLayer> layerDefs = null;
            if (urls != null && urls.Count > 0 && Assets != null)
            {
                layers = new List<Sprite>(urls.Count);
                layerIds = urlIds != null ? new List<string>(urls.Count) : null;
                layerRects = urlRects != null ? new List<Vector4>(urls.Count) : null;
                layerDefs = urlDefs != null ? new List<SpriteCatalog.ResolvedLayer>(urls.Count) : null;
                // Layers load IN PARALLEL — a five-layer character used to pay
                // five sequential fetch+decode round-trips on a cold cache; the
                // loader dedups in-flight urls and decodes on workers, so the
                // wall time is now the slowest layer, not the sum. Order is
                // preserved by index (z-order = author order).
                var loads = new Task<Sprite>[urls.Count];
                for (int i = 0; i < urls.Count; i++)
                    loads[i] = LoadLayerAsync(urls[i]);
                for (int i = 0; i < urls.Count; i++)
                {
                    var s = await loads[i];
                    if (s != null)
                    {
                        layers.Add(s);
                        layerIds?.Add(i < urlIds.Count ? urlIds[i] : null);
                        layerRects?.Add(i < urlRects.Count ? urlRects[i] : Vector4.zero);
                        layerDefs?.Add(i < urlDefs.Count ? urlDefs[i] : default);
                    }
                }
            }

            // A chapter change landed while our sprites loaded — this actor
            // belongs to a scene that no longer exists; never resurrect it on the
            // clean stage (the ghost-actor bug: a per-id gen doesn't catch an id
            // the new chapter never uses, so it's never superseded).
            if (!StageCurrent(epoch)) return;

            // Same self-healing acquisition as the backdrop: a layer that hits a
            // network flap keeps retrying (and wakes on reconnect) for as long as
            // THIS apply is still the actor's newest — a faceless/bodyless actor
            // must not survive a 2-second connectivity blip.
            Task<Sprite> LoadLayerAsync(string u) => LoadSceneSpriteAsync(u, "actor layer",
                () => StageCurrent(epoch) && (!_actorGen.TryGetValue(id, out var curGen) || curGen == gen));
            // A newer apply started while our sprites loaded — ITS art must win;
            // this stale pass may not touch the renderer (late-arrival outfit bug).
            if (_actorGen.TryGetValue(id, out var cur) && cur != gen) return;

            _renderer?.ApplyActor(id, layers, placement, onClick, layerIds, layerRects, layerDefs);
            _placements[id] = placement; // the sticky base for the next command
            _actorCmds[id] = cmd;        // wardrobe changes replay this in place

            // Animations (rigged entities): idle (whole-actor) + blink (a layer)
            // auto-run on show; play="name" fires a one-shot gesture; an
            // auto:"speaking" anim is remembered for lip-sync while this actor talks.
            var animEntity = Catalog != null ? Catalog.Get(id) : null;
            if (animEntity != null && animEntity.anim != null && animEntity.anim.Count > 0)
            {
                await PreloadFramesAsync(id, animEntity);
                // The frame preload awaited network — a chapter change or a newer
                // apply may own the actor now; stale anim state must not leak in.
                if (!StageCurrent(epoch)) return;
                if (_actorGen.TryGetValue(id, out var animGen) && animGen != gen) return;

                LvnAnim idle = null, blink = null, talk = null;
                foreach (var kv in animEntity.anim)
                {
                    var a = kv.Value;
                    if (a == null) continue;
                    if (a.auto == "speaking") { talk = a; continue; }
                    if (a.auto == "true") { if (HasLayerTrack(a)) blink = blink ?? a; else idle = idle ?? a; }
                }
                _talkAnims[id] = talk; // null clears it

                var playName = (string)cmd["play"];
                if (!string.IsNullOrEmpty(playName) && animEntity.anim.TryGetValue(playName, out var gesture))
                    ScenePlayGesture(id, gesture, idle);
                else if (placement.Show && idle != null)
                    SceneEnsureIdle(id, idle);
                if (placement.Show && blink != null) SceneEnsureBlink(id, blink);
            }
        }

        private static bool HasLayerTrack(LvnAnim a)
        {
            if (a.tracks == null) return false;
            foreach (var t in a.tracks) if (t != null && !string.IsNullOrEmpty(t.layer)) return true;
            return false;
        }

        // Preload the sprite variants a frame track needs (e.g. eyes=open/closed),
        // so blink/lip-sync swaps are instant. Resolves each layer's url template
        // with axis=value via the catalog.
        private async Task PreloadFramesAsync(string id, LvnSpriteEntity entity)
        {
            if (entity.anim == null || entity.layers == null || Assets == null || Catalog == null) return;
            var frames = new Dictionary<string, Dictionary<string, Sprite>>();
            foreach (var anim in entity.anim.Values)
            {
                if (anim?.tracks == null) continue;
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || tr.prop != "frame" || string.IsNullOrEmpty(tr.layer) || string.IsNullOrEmpty(tr.axis) || tr.keys == null) continue;
                    string template = null;
                    foreach (var l in entity.layers) if (l != null && l.id == tr.layer) { template = l.url; break; }
                    if (string.IsNullOrEmpty(template)) continue;
                    if (!frames.TryGetValue(tr.layer, out var map)) frames[tr.layer] = map = new Dictionary<string, Sprite>();
                    foreach (var key in tr.keys)
                    {
                        var val = key != null && key.Length > 1 ? key[1]?.ToString() : null;
                        if (string.IsNullOrEmpty(val) || map.ContainsKey(val)) continue;
                        var url = Catalog.FillFor(id, template, new Dictionary<string, string> { { tr.axis, val } });
                        if (string.IsNullOrEmpty(url)) continue;
                        try { var sp = await Assets.LoadSpriteAsync(url, _cts.Token); if (sp != null) map[val] = sp; }
                        catch { }
                    }
                }
            }
            if (frames.Count > 0) SceneSetFrames(id, frames);
        }

        // Build placement from the command — everything in screen fractions so a
        // script controls any object's position, size, anchor, z, flip, rotation
        // and opacity without knowing the resolution.
        /// <summary>Sticky placement: merge an actor command over the actor's
        /// LAST applied placement — only fields the command explicitly mentions
        /// change, so <c>actor id=knight play="Jump"</c> keeps the position a
        /// drag, a move-follow-up or an earlier command left him at.
        /// Transitions are one-shot and always come from the command.</summary>
        /// <summary>A named slot's x for an entity: the catalog def's per-entity
        /// override wins over the global table (see LvnSpriteEntity.slots).</summary>
        internal static float SlotXFor(string position, IReadOnlyDictionary<string, float> slots)
            => position != null && slots != null && slots.TryGetValue(position, out var v)
                ? v : ActorLayer.SlotX(position);

        // ── smart slots ──────────────────────────────────────────────────────
        // A VISIBLE actor owns its X until it hides or moves. Branch-merged
        // content routinely loses a hide on the way into a shared tail (the
        // partner's "two characters standing inside each other" screenshot:
        // choice branch re-shows Matvey right, jumps to the tail, the tail
        // shows Miron right — 673 such flow-order collisions across the cold
        // chapters). The stage must never DRAW that: a claimant resolved into
        // an occupied slot slides to the nearest free slot instead. An explicit
        // numeric x is authorial composition (embraces, crowds) — never touched.

        internal const float SlotClaimRadius = 0.08f;
        private static readonly float[] StandardSlotXs = { 0.12f, 0.25f, 0.38f, 0.50f, 0.62f, 0.75f, 0.88f };

        /// <summary>Resolve where a shown actor may actually stand. Returns the
        /// desired X when the spot is free (or the claim is an explicit x);
        /// otherwise the nearest free slot X, ties broken away from centre so
        /// crowds spread outward. <paramref name="ownerId"/> reports who held
        /// the contested spot (null = no contest).</summary>
        internal static float ArbitrateSlotX(float desired, string id, bool hasExplicitX,
            IEnumerable<KeyValuePair<string, Placement>> visible,
            IReadOnlyDictionary<string, float> entitySlots, out string ownerId)
        {
            ownerId = null;
            if (hasExplicitX) return desired;
            var taken = new List<float>();
            foreach (var kv in visible)
            {
                if (kv.Key == id || !kv.Value.Show) continue;
                taken.Add(kv.Value.X);
                if (ownerId == null && Mathf.Abs(kv.Value.X - desired) < SlotClaimRadius)
                    ownerId = kv.Key;
            }
            if (ownerId == null) return desired;

            var cands = new List<float>(StandardSlotXs);
            if (entitySlots != null) foreach (var v in entitySlots.Values) cands.Add(v);
            cands.Sort((a, b) =>
            {
                int byDist = Mathf.Abs(a - desired).CompareTo(Mathf.Abs(b - desired));
                if (byDist != 0) return byDist;
                return Mathf.Abs(b - 0.5f).CompareTo(Mathf.Abs(a - 0.5f)); // tie → outward
            });
            foreach (var c in cands)
            {
                var free = true;
                foreach (var t in taken)
                    if (Mathf.Abs(t - c) < SlotClaimRadius) { free = false; break; }
                if (free) return c;
            }
            // Every slot taken (crowd): slide just clear of the desired point.
            var shifted = desired + (desired <= 0.5f ? SlotClaimRadius * 1.6f : -SlotClaimRadius * 1.6f);
            return Mathf.Clamp(shifted, 0.05f, 0.95f);
        }

        // The catalog's slot overrides for an actor id (null-safe at every hop).
        private IReadOnlyDictionary<string, float> SlotsOf(string id) => Catalog?.Get(id)?.slots;

        internal static Placement PlacementFrom(JObject cmd, Placement prev,
            IReadOnlyDictionary<string, float> slots = null)
        {
            var p = prev;
            p.Show = BoolOr(cmd["show"], true); // re-issuing an actor shows it (existing semantics)
            if (cmd["x"] != null || cmd["position"] != null)
                p.X = NumOrNull(cmd["x"]) ?? SlotXFor((string)cmd["position"], slots);
            if (cmd["y"] != null) p.Y = NumOr(cmd["y"], p.Y);
            if (cmd["width"] != null) p.Width = NumOrNull(cmd["width"]);
            if (cmd["height"] != null) p.Height = NumOrNull(cmd["height"]);
            if (cmd["z"] != null) p.Z = IntOrNull(cmd["z"]);
            if (cmd["flip"] != null || cmd["mirror"] != null) p.Flip = BoolOr(cmd["flip"] ?? cmd["mirror"], false);
            if (cmd["rotation"] != null) p.Rotation = NumOr(cmd["rotation"], 0f);
            if (cmd["opacity"] != null) p.Opacity = NumOr(cmd["opacity"], 1f);
            if (cmd["hover_opacity"] != null) p.HoverOpacity = NumOr(cmd["hover_opacity"], 1f);
            p.EnterTransition = ParseTransition((string)cmd["enter"]);
            p.ExitTransition = ParseTransition((string)cmd["exit"]);
            p.TransitionDuration = NumOr(cmd["transition_duration"], 0.3f);
            var anch = (string)cmd["anchor"];
            if (!string.IsNullOrEmpty(anch))
            {
                var parts = anch.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay))
                { p.AnchorX = ax; p.AnchorY = ay; }
            }
            else
            {
                if (cmd["anchor_x"] != null) p.AnchorX = NumOr(cmd["anchor_x"], p.AnchorX);
                if (cmd["anchor_y"] != null) p.AnchorY = NumOr(cmd["anchor_y"], p.AnchorY);
            }
            return p;
        }

        internal static Placement PlacementFrom(JObject cmd,
            IReadOnlyDictionary<string, float> slots = null)
        {
            var p = new Placement
            {
                Show = BoolOr(cmd["show"], true),
                X = NumOrNull(cmd["x"]) ?? SlotXFor((string)cmd["position"], slots),
                Y = NumOr(cmd["y"], 1f),
                Width = NumOrNull(cmd["width"]),
                Height = NumOrNull(cmd["height"]),
                AnchorX = 0.5f,
                AnchorY = 1f,
                Z = IntOrNull(cmd["z"]),
                Flip = BoolOr(cmd["flip"] ?? cmd["mirror"], false), // `mirror` is an authoring alias for flip
                Rotation = NumOr(cmd["rotation"], 0f),
                Opacity = NumOr(cmd["opacity"], 1f),
                HoverOpacity = NumOr(cmd["hover_opacity"], 1f),
                EnterTransition = ParseTransition((string)cmd["enter"]),
                ExitTransition = ParseTransition((string)cmd["exit"]),
                TransitionDuration = NumOr(cmd["transition_duration"], 0.3f),
            };

            var anchor = (string)cmd["anchor"];
            if (!string.IsNullOrEmpty(anchor))
            {
                var parts = anchor.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay))
                {
                    p.AnchorX = ax;
                    p.AnchorY = ay;
                }
            }
            else
            {
                if (cmd["anchor_x"] != null) p.AnchorX = NumOr(cmd["anchor_x"], p.AnchorX);
                if (cmd["anchor_y"] != null) p.AnchorY = NumOr(cmd["anchor_y"], p.AnchorY);
            }
            return p;
        }

        // Like AxesFrom but with {var} interpolation against the player's variables,
        // so equipment can be data-driven: `actor hero armor={arm} weapon={wpn}`.
        // An axis that resolves to empty or stays unresolved is DROPPED, leaving its
        // {axis} token unfilled → that layer is skipped (the "nothing equipped" case).
        private Dictionary<string, string> AxesOf(JObject cmd)
        {
            var axes = AxesFrom(cmd);
            var vars = _player?.Vars;
            // Axes whose raw value was a {var} template (e.g. the imported protagonist's
            // outfit={Wardrobe.mainCh_Clothes}) are variable-DRIVEN, not story-forced
            // literals — a live wardrobe preview may override those in realtime, while a
            // literal costume the writer pinned stays put. Track them for MergeInto.
            var templated = new HashSet<string>();
            foreach (var k in new List<string>(axes.Keys))
            {
                var v = axes[k];
                bool wasTemplate = !string.IsNullOrEmpty(v) && v.IndexOf('{') >= 0;
                if (wasTemplate)
                {
                    templated.Add(k);
                    if (vars != null) v = TextInterpolation.Apply(v, vars);
                }
                if (string.IsNullOrEmpty(v) || v.IndexOf('{') >= 0) axes.Remove(k); // no value → no layer
                else axes[k] = v;
            }
            // The player's wardrobe fills axes the script left unset — a story-forced
            // literal still wins, but a preview overrides a variable-driven axis.
            LvnWardrobe.MergeInto(axes, (string)cmd["id"], templated);
            return axes;
        }

        // The actor command's free-form named fields (pose, emotion, prop, …) —
        // everything outside the reserved layout/control set — are the cast axes.
        internal static Dictionary<string, string> AxesFrom(JObject cmd)
        {
            var axes = new Dictionary<string, string>();
            foreach (var p in cmd.Properties())
            {
                if (ReservedActorFields.Contains(p.Name)) continue;
                switch (p.Value.Type)
                {
                    case JTokenType.String:
                    case JTokenType.Integer:
                    case JTokenType.Float:
                    case JTokenType.Boolean:
                        axes[p.Name] = p.Value.ToString();
                        break;
                }
            }
            return axes;
        }
    }
}
