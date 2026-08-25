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

        // The placement requested by an actor command is known before its art
        // finishes loading. Dialogue uses this small staging view so a freshly
        // entering speaker's name is on the correct side from the first frame.
        private readonly Dictionary<string, Placement> _actorTargets = new Dictionary<string, Placement>();

        // Per-actor apply generation: rapid wardrobe browsing fires overlapping
        // ApplyActorAsync calls whose sprite loads finish out of order — only
        // the NEWEST may touch the renderer, or an older outfit "wins" by
        // arriving late.
        private readonly Dictionary<string, int> _actorGen = new Dictionary<string, int>();

        // История подбора: 1.4 × 1.3 × 0.8 = 1.456, а 25.08 Илья попросил
        // «перс должен приезжать быстрее» — минус 40%: 1.456 × 0.6 = 0.874.
        // Вместе с фейдом на весь ход (LvnFade.OpacityProgress) вход стал
        // короче и мягче: дефолтный drift ~0.382 s → ~0.229 s.
        private const float ActorVisibilityDurationScale = 0.874f;
        private const float ActorMovementDurationScale = 0.75f;

        // Commands between two dialogue pauses are consumed in one LvnPlayer
        // Advance loop.  Therefore `hide A; show B; say` used to start both
        // transitions in the same frame.  Keep asset loading parallel, but gate
        // the next ACTOR reveal until every already-started actor exit has used
        // its full realtime duration. Objects are deliberately excluded.
        private float _actorExitBarrierUntil;
        // Input uses the same clock to keep a rapid tap from replacing a card
        // while the actor belonging to that beat is still entering or leaving.
        private float _actorVisibilityBarrierUntil;

        private static bool IsCharacterCommand(JObject cmd)
            => !string.Equals((string)cmd?["op"], "obj", StringComparison.OrdinalIgnoreCase);

        private static void LengthenCharacterVisibility(JObject cmd, bool visibilityChanged,
                                                         ref Placement p)
        {
            if (!visibilityChanged || !IsCharacterCommand(cmd) || p.TransitionDuration <= 0.001f)
                return;
            var transition = p.Show ? p.EnterTransition : p.ExitTransition;
            if (transition == TransitionType.None) return;
            p.TransitionDuration *= ActorVisibilityDurationScale;
        }

        private static void ApplyPresentationTempo(ref Placement p)
        {
            if (p.TransitionDuration > 0.001f)
                p.TransitionDuration *= VnTheme.MotionDurationScale;
        }

        /// <summary>Side entrances and changes between stage positions should
        /// read as a quick piece of blocking, not as the actor skating through
        /// the shot. Fade-only exits deliberately keep their own timing.</summary>
        private static void ShortenCharacterMovement(JObject cmd, ref Placement p)
        {
            if (!IsCharacterCommand(cmd) || p.TransitionDuration <= 0.001f) return;
            var visibilityTransition = p.Show ? p.EnterTransition : p.ExitTransition;
            if (p.SmoothPosition || visibilityTransition == TransitionType.Drift)
                p.TransitionDuration *= ActorMovementDurationScale;
        }

        private void ArmActorExitBarrier(Placement p)
        {
            if (p.ExitTransition == TransitionType.None || p.TransitionDuration <= 0.001f) return;
            _actorExitBarrierUntil = Mathf.Max(_actorExitBarrierUntil,
                Time.realtimeSinceStartup + p.TransitionDuration);
        }

        private void ArmActorVisibilityBarrier(JObject cmd, bool visibilityChanged, Placement p)
        {
            if (!visibilityChanged || !IsCharacterCommand(cmd)
                || p.TransitionDuration <= 0.001f) return;
            var transition = p.Show ? p.EnterTransition : p.ExitTransition;
            if (transition == TransitionType.None) return;
            _actorVisibilityBarrierUntil = Mathf.Max(_actorVisibilityBarrierUntil,
                Time.realtimeSinceStartup + p.TransitionDuration);
            // A cold asset can begin its real entrance after the nominal early
            // barrier already unlocked the line. Reclaim input immediately and
            // let the same generation-aware gate reopen it at the new deadline.
            if (_sayUp && _awaitingTap)
            {
                _awaitingTap = false;
                int gen = _dialogueSwapGeneration;
                _dialogue?.schedule.Execute(() => UnlockSayWhenChoreographyReady(gen))
                    .ExecuteLater(1);
            }
            if (_curChoices != null && _curChoices.Count > 0 && _choices != null)
            {
                _choices.SetEnabled(false);
                int gen = _dialogueSwapGeneration;
                _choices.schedule.Execute(() => EnableChoiceWhenChoreographyReady(gen))
                    .ExecuteLater(1);
            }
        }

        private async Task WaitForActorExitsAsync(int epoch)
        {
            while (StageCurrent(epoch))
            {
                float left = _actorExitBarrierUntil - Time.realtimeSinceStartup;
                if (left <= 0.001f) return;
                // LvnFade also runs on realtime, so this barrier finishes on the
                // same clock even when game time is paused or accelerated.
                await Task.Delay(Mathf.Max(1, Mathf.CeilToInt(left * 1000f)));
            }
        }

        /// <summary>Re-apply an on-screen actor from its last command (art
        /// re-resolves against the current variables + wardrobe). No-op when
        /// the actor isn't on stage.</summary>
        public void RefreshActor(string id)
        {
            if (!string.IsNullOrEmpty(id) && _actorCmds.TryGetValue(id, out var cmd))
                LvnAsync.Fire(ApplyActorAsync(cmd), "ApplyActor");
        }

        private void RefreshWardrobeActor(string id, string wardrobeAxis)
        {
            if (!string.IsNullOrEmpty(id) && _actorCmds.TryGetValue(id, out var cmd))
                LvnAsync.Fire(ApplyActorAsync(cmd, wardrobeSwap: true,
                    wardrobeFromTop: IsHairWardrobeAxis(wardrobeAxis)), "WardrobeActor");
        }

        private static bool IsHairWardrobeAxis(string axis)
        {
            var key = (axis ?? "").ToLowerInvariant();
            return key.Contains("hair") || key.Contains("причес") || key.Contains("волос");
        }

        /// <summary>Ensure an actor is ON stage — used by the in-story wardrobe so it
        /// always has the active hero to dress, even when the beat left the stage empty
        /// (imported novels open the wardrobe without staging anyone). Replays the
        /// actor's last pose forcing it visible, or stages it fresh (centred) from its
        /// catalog entity. No-op for an empty id.</summary>
        public void EnsureActorShown(string id, bool fadeOnly = false)
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
                // Манекен гардероба без сценарной постановки: размер как у
                // сценарного зеркала (0.92/1.06), а не дефолтный слот — иначе
                // «в игре и в гардеробе героиня разного роста» (живой репорт).
                cmd = new JObject
                {
                    ["op"] = "actor", ["id"] = id, ["show"] = true, ["position"] = "center",
                    ["width"] = 0.92f, ["height"] = 1.06f,
                };
            }
            if (fadeOnly) cmd["enter"] = "fade";
            LvnAsync.Fire(ApplyActorAsync(cmd), "ApplyActor");
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

        /// <summary>
        /// Команда без <c>enter=</c>/<c>exit=</c> берёт постановочный переход из
        /// темы. У actor и obj разные дефолты: герой въезжает от ближайшего края
        /// и растворяется на месте; реквизит проявляется на месте. Пустая строка
        /// означает мгновенный показ.
        /// </summary>
        private void FillTransitionDefaults(JObject cmd, ref Placement p)
            => ApplyTransitionDefaults(cmd, Theme, ref p);

        internal static void ApplyTransitionDefaults(JObject cmd, VnTheme theme, ref Placement p)
        {
            if (theme == null) return;
            bool isObject = string.Equals((string)cmd?["op"], "obj", StringComparison.OrdinalIgnoreCase);
            if (cmd?["enter"] == null)
                p.EnterTransition = ParseTransition(isObject ? theme.ObjectEnter : theme.ActorEnter);
            if (cmd?["exit"] == null)
                p.ExitTransition = ParseTransition(isObject ? theme.ObjectExit : theme.ActorExit);
            if (cmd?["transition_duration"] == null)
                p.TransitionDuration = Mathf.Max(0f,
                    isObject ? theme.ObjectTransition : theme.ActorTransition);
        }

        /// <summary>Take an actor off stage — the counterpart of
        /// <see cref="EnsureActorShown"/> for a host that staged someone
        /// temporarily (the menu wardrobe) and wants the scene back as it was.</summary>
        public void HideActor(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            string op = "actor";
            if (_actorCmds.TryGetValue(id, out var staged)
                && string.Equals((string)staged["op"], "obj", StringComparison.OrdinalIgnoreCase))
                op = "obj";
            // Без exit=: уход возьмётся из темы (drift/fade/что выбрала
            // новелла). Жёсткий "fade" здесь затирал бы дефолт постановки.
            LvnAsync.Fire(ApplyActorAsync(new JObject
            {
                ["op"] = op, ["id"] = id, ["show"] = false,
            }), "ApplyActor");
        }

        /// <summary>Temporarily remove a wardrobe mannequin and wait until its
        /// fade is fully finished. Preserve the actor's last authored show
        /// command, so restoring it later keeps art axes, emotion and placement
        /// instead of replaying the synthetic hide command.</summary>
        public async Task HideActorTemporarilyAndWaitAsync(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            int epoch = _stageEpoch;
            HideActorTemporarily(id);
            await WaitForActorExitsAsync(epoch);
        }

        private void HideActorTemporarily(string id)
        {
            JObject replay = _actorCmds.TryGetValue(id, out var current)
                ? (JObject)current.DeepClone() : null;
            HideActor(id);
            if (replay != null) _actorCmds[id] = replay;
        }

        /// <summary>Engine-level wardrobe focus: every visible CHARACTER except
        /// <paramref name="keepId"/> is temporarily removed, all exits finish,
        /// then the selected mannequin is faded in. Props are not cast and stay.
        /// A generation predicate lets a host discard a rapid stale selection
        /// before it can show the wrong actor.</summary>
        public async Task FocusWardrobeActorAsync(string keepId, Func<bool> canShow = null)
        {
            if (string.IsNullOrEmpty(keepId)) return;
            int epoch = _stageEpoch;
            // НОВЫЙ ВЫБОР ОТМЕНЯЕТ ПРЕДЫДУЩИЙ, И ЭТО ЗАБОТА САМОЙ ОПЕРАЦИИ.
            // Список «кого убрать» считается ДО ожидания ухода, а показ идёт
            // ПОСЛЕ — значит показ отставшего выбора мог приземлиться уже после
            // того, как следующий выбор его спрятал, и на сцене оставалось
            // двое, наложенных друг на друга (снимок партнёра). Раньше от этого
            // защищал предикат, который передавал только один вызывающий: путь
            // ОТКРЫТИЯ гардероба звал без него, а именно во время открытия и
            // жмут первую «таблетку».
            int gen = ++_wardrobeFocusGen;
            HideEveryoneExcept(keepId);
            await WaitForActorExitsAsync(epoch);
            if (gen != _wardrobeFocusGen) return;   // нас перебили — показывает он
            if (!StageCurrent(epoch) || (canShow != null && !canShow())) return;
            // Пока ждали, кто-то мог успеть показаться: пересобрать и убрать.
            HideEveryoneExcept(keepId);
            EnsureActorShown(keepId, fadeOnly: true);
        }

        private int _wardrobeFocusGen;

        private void HideEveryoneExcept(string keepId)
        {
            foreach (var id in ActorsOnStage())
            {
                if (id == keepId) continue;
                if (_actorCmds.TryGetValue(id, out var cmd) && !IsCharacterCommand(cmd)) continue;
                HideActorTemporarily(id);
            }
        }

        /// <summary>The `clear` op: take every actor and obj off stage in one
        /// command, leaving the backdrop, effects and HUD exactly as they are.
        ///
        /// <para>Each one goes through the ORDINARY hide, so nothing here needs
        /// to know how hiding works: placement stays remembered (a later
        /// `actor id=…` with no position returns her to the slot she left),
        /// hotspots and draggables are dropped, and the exit is the same fade a
        /// hand-written `show=false` would have played. The list is snapshotted
        /// first — <see cref="ActorsOnStage"/> builds a new list — because the
        /// hides mutate the placement map as they run.</para></summary>
        private void ApplyClear()
        {
            foreach (var id in ActorsOnStage()) HideActor(id);
        }

        private void OnWardrobeChanged(string entity)
            => RefreshWardrobeActor(entity, LvnWardrobe.LastChangedAxis(entity));

        private async Task ApplyActorAsync(JObject cmd, bool wardrobeSwap = false,
                                           bool wardrobeFromTop = false)
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
                bool wasVisible = !freshHide && prevHide.Show;
                var hidePl = freshHide ? PlacementFrom(cmd, SlotsOf(id)) : PlacementFrom(cmd, prevHide, SlotsOf(id));
                FillTransitionDefaults(cmd, ref hidePl);
                ApplyPresentationTempo(ref hidePl);
                LengthenCharacterVisibility(cmd, wasVisible, ref hidePl);
                ShortenCharacterMovement(cmd, ref hidePl);
                ArmActorVisibilityBarrier(cmd, wasVisible, hidePl);
                _actorTargets[id] = hidePl;

                if (!freshHide)
                {
                    // Both renderer paths: Canvas PlaceActor updates geometry
                    // without revealing, then ApplyActor runs the exit; UITK's
                    // PlaceActor is a no-op and ApplyActor owns both operations.
                    _renderer?.PlaceActor(id, hidePl);
                    _renderer?.ApplyActor(id, null, hidePl, null, null, null);
                }
                if (wasVisible && IsCharacterCommand(cmd)) ArmActorExitBarrier(hidePl);
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
                // Диагностика облика: «почему лысая/не тот наряд» решается одной
                // строкой лога вместо круга скриншотов — видно, какие слои и из
                // каких осей собрались.
                Debug.Log($"[lvn-actor] {id}: слои [{string.Join(",", rls.ConvertAll(r => r.Id))}] "
                    + $"оси {{{string.Join(", ", System.Linq.Enumerable.Select(axes, kv => kv.Key + "=" + kv.Value))}}}");
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
                        CancelPendingWait(); // a timed hotspot screen: the click wins the race
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
                            CancelPendingWait(); // a timed hotspot screen: the click wins the race
                            _awaitingTap = false;
                            _curChoices = null;
                            _choices.Dismiss();
                            _player.Advance();
                        };
                }
            }

            bool fresh = !_placements.TryGetValue(id, out var prevPl);
            bool wasVisibleBeforeShow = !fresh && prevPl.Show;
            var placement = fresh ? PlacementFrom(cmd, SlotsOf(id)) : PlacementFrom(cmd, prevPl, SlotsOf(id));
            // Силуэт — одноразовое состояние ЗАГОТОВКИ, не липкая постановка:
            // унаследованный от прошлой команды, он затемнял бы уже полный арт
            // на каждой следующей реплике (живой репорт «прыгает»).
            placement.Silhouette = false;
            FillTransitionDefaults(cmd, ref placement);
            ApplyPresentationTempo(ref placement);
            bool visibilityChanged = !wasVisibleBeforeShow && placement.Show;
            LengthenCharacterVisibility(cmd, visibilityChanged, ref placement);
            // Position changes are ordinary stage choreography, not another
            // entrance. This one-shot hint is consumed by the renderer and is
            // cleared before the sticky placement is stored (drag must stay 1:1).
            placement.SmoothPosition = wasVisibleBeforeShow
                && (cmd["position"] != null || cmd["x"] != null || cmd["y"] != null);
            placement.WardrobeSwap = wardrobeSwap;
            placement.WardrobeFromTop = wardrobeFromTop;
            ShortenCharacterMovement(cmd, ref placement);
            ArmActorVisibilityBarrier(cmd, visibilityChanged, placement);
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

            _actorTargets[id] = placement;

            // Place first so the slot exists before the (async) art arrives — a
            // no-op on renderers that apply placement together with the art.
            _renderer?.PlaceActor(id, placement);
            _hotspots.RemoveAll(h => h.id == id);
            // Клик по актёру считается вручную, по прямоугольнику на экране:
            // канвас-сцена — соседний канвас, а не элемент этой панели.
            if (onClick != null && placement.Show) _hotspots.Add((id, onClick));

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

                // «СИЛУЭТ-ПРОЯВЛЕНИЕ» (идея Ильи): медленная СЕТЬ не задерживает
                // выход актёра. Включается ТОЛЬКО когда байтов нет локально —
                // ни в кэше (любой файл показа), ни в сиде APK: локальные байты
                // декодируются за сотни мс, и заготовка лишь мигала бы на каждой
                // смене эмоции (живой репорт «уменьшилась и прыгает»). Актёр
                // входит вовремя крошечной @mini-заготовкой, затемнённой тинтом;
                // полный арт доезжает фоном и проявляет его кроссфейдом облика.
                bool bytesLocal = true;
                if ((Theme?.LoadingSilhouette ?? true) && placement.Show
                    && IsCharacterCommand(cmd) && !wardrobeSwap
                    && (Assets as CachingAssets)?.Loader is Lvn.Content.ContentLoader cl)
                {
                    foreach (var u in urls)
                        if (!cl.HasLocalSpriteBytes(u)) { bytesLocal = false; break; }
                }
                if (!bytesLocal)
                {
                    var allLoads = Task.WhenAll(loads);
                    if (await Task.WhenAny(allLoads, Task.Delay(250)) != allLoads)
                    {
                        var mini = new List<Sprite>(urls.Count);
                        var miniIds = layerIds != null ? new List<string>(urls.Count) : null;
                        var miniRects = layerRects != null ? new List<Vector4>(urls.Count) : null;
                        var miniDefs = layerDefs != null ? new List<SpriteCatalog.ResolvedLayer>(urls.Count) : null;
                        for (int i = 0; i < urls.Count; i++)
                        {
                            var mu = Lvn.Content.DownloadPolicy.MiniVariant(urls[i]);
                            if (mu == null) continue;
                            Sprite ms = null;
                            try { ms = await Assets.LoadSpriteAsync(mu, _cts.Token); }
                            catch (OperationCanceledException) { return; }
                            catch { /* мини недоступен — слой пропускается */ }
                            if (ms == null) continue;
                            mini.Add(ms);
                            miniIds?.Add(i < urlIds.Count ? urlIds[i] : null);
                            miniRects?.Add(i < urlRects.Count ? urlRects[i] : Vector4.zero);
                            miniDefs?.Add(i < urlDefs.Count ? urlDefs[i] : default);
                        }
                        if (mini.Count > 0 && StageCurrent(epoch)
                            && (!_actorGen.TryGetValue(id, out var sg) || sg == gen))
                        {
                            var silPl = placement;
                            silPl.Silhouette = true;
                            Debug.Log($"[lvn-actor] {id}: силуэт-заготовка ({mini.Count} слоёв) — полный арт доедет фоном");
                            _renderer?.ApplyActor(id, mini, silPl, onClick, miniIds, miniRects, miniDefs);
                            _placements[id] = silPl; // полный apply увидит «уже видим» → кроссфейд-проявление
                            wasVisibleBeforeShow = true;
                            visibilityChanged = false;
                        }
                    }
                }

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

            // The outgoing actor is already fading while this actor's layers load.
            // Only the visual reveal is serialized; cached/network work remains
            // concurrent, so the choreography adds no avoidable loading hitch.
            if (!wasVisibleBeforeShow && IsCharacterCommand(cmd))
            {
                await WaitForActorExitsAsync(epoch);
                if (!StageCurrent(epoch)) return;
                if (_actorGen.TryGetValue(id, out cur) && cur != gen) return;
            }

            // Идущий кроссфейд облика ДОИГРЫВАЕТ: новое применение стыкуется за
            // ним, а не срезает в один кадр (срез — это «героиня мелькнула» у
            // гардероба: emotion=happy обрывал шторку смены наряда на середине).
            // Ожидание конечно: дедлайн — фиксированный момент (≤0.3 с), а тот,
            // кто его продлил, сначала уронил наш gen — выйдем по проверке.
            float swapLeft;
            while ((swapLeft = (_renderer?.ActorSwapDeadline(id) ?? 0f)
                    - Time.realtimeSinceStartup) > 0.001f)
            {
                await Task.Delay(Mathf.Max(1, Mathf.CeilToInt(swapLeft * 1000f)));
                if (!StageCurrent(epoch)) return;
                if (_actorGen.TryGetValue(id, out cur) && cur != gen) return;
            }

            // Loading may have outlived the early nominal barrier. Re-arm from
            // the frame where the renderer actually starts the entrance.
            ArmActorVisibilityBarrier(cmd, visibilityChanged, placement);
            _renderer?.ApplyActor(id, layers, placement, onClick, layerIds, layerRects, layerDefs);
            placement.SmoothPosition = false;
            placement.WardrobeSwap = false;
            placement.WardrobeFromTop = false;
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
                        catch { }   // актёр без арта: покажем силуэт, но кадр не потеряем
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
                ? v : Placement.SlotX(position);

        // ── smart slots ──────────────────────────────────────────────────────
        // A VISIBLE actor owns its X until it hides or moves. Branch-merged
        // content routinely loses a hide on the way into a shared tail (the
        // partner's "two characters standing inside each other" screenshot:
        // choice branch re-shows Roman right, jumps to the tail, the tail
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
            // scale= МНОЖИТ размер, а не задаёт его. Поле было объявлено в
            // грамматике, зарезервировано от осей каста и даже переживало
            // реплей — и нигде не применялось: `actor id=x scale=1.4`
            // компилировался и молча ничего не делал.
            ApplyScale(cmd, ref p);
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

        /// <summary>
        /// Размещение с чистого листа. ЧАСТНЫЙ СЛУЧАЙ обновления: берём
        /// умолчания сцены и применяем к ним ту же команду.
        ///
        /// <para>Раньше это была вторая, почти дословная копия — те же
        /// пятнадцать полей, тот же разбор якоря. Две копии одного понятия
        /// расходятся молча: `scale` пришлось чинить дважды, и второй раз я
        /// едва не забыл.</para>
        /// </summary>
        internal static Placement PlacementFrom(JObject cmd,
            IReadOnlyDictionary<string, float> slots = null)
            => PlacementFrom(cmd, FreshPlacement(cmd, slots), slots);

        /// <summary>Умолчания сцены: ноги на нижнем краю, столбец по слоту.</summary>
        private static Placement FreshPlacement(JObject cmd, IReadOnlyDictionary<string, float> slots)
            => new Placement
            {
                Show = true,
                X = SlotXFor((string)cmd?["position"], slots),
                Y = 1f,
                AnchorX = 0.5f,
                AnchorY = 1f,
                // Непрозрачность по умолчанию — ЕДИНИЦА, а не ноль структуры.
                // На этом слияние двух копий и споткнулось: липкий путь берёт
                // прозрачность из предыдущего размещения, и «предыдущим» для
                // свежего актёра оказался пустой struct — персонаж выходил
                // невидимым. Тест поймал сразу.
                Opacity = 1f,
                HoverOpacity = 1f,
            };

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

        // Множитель размера. Работает и когда ширина с высотой не заданы: тогда
        // умножается умолчание темы, иначе `scale` пришлось бы писать вместе с
        // width/height — то есть считать за автора то, что он и хотел поручить
        // движку.
        private static void ApplyScale(JObject cmd, ref Placement p)
        {
            var k = NumOrNull(cmd["scale"]);
            if (k == null || k.Value <= 0f) return;
            p.Width = (p.Width ?? Placement.DefaultWidth) * k.Value;
            p.Height = (p.Height ?? Placement.DefaultHeight) * k.Value;
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
