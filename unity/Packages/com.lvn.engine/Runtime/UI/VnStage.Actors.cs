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

        // Поколение показа у каждого актёра — дорожка Хронометриста: быстрый
        // перебор нарядов запускает несколько ApplyActorAsync, чьи загрузки
        // финишируют вразнобой, и трогать рендерер имеет право только самый
        // новый (иначе прежний наряд «выигрывает», приехав позже).

        // История подбора: 1.4 × 1.3 × 0.8 = 1.456; 25.08 Илья попросил
        // «быстрее» дважды — минус 40%, затем ещё минус 15%:
        // 1.456 × 0.6 × 0.85 = 0.743. Вместе с фейдом на весь ход
        // (LvnFade.OpacityProgress) дефолтный drift ~0.382 s → ~0.195 s.
        private const float ActorVisibilityDurationScale = 0.743f;
        private const float ActorMovementDurationScale = 0.75f;

        // Commands between two dialogue pauses are consumed in one LvnPlayer
        // Advance loop.  Therefore `hide A; show B; say` used to start both
        // transitions in the same frame.  Keep asset loading parallel, but gate
        // the next ACTOR reveal until every already-started actor exit has used
        // its full realtime duration. Objects are deliberately excluded.
        // Барьеры уходов и видимости — тоже у Хронометриста
        // (LvnStageClock.ActorExitBarrier / ActorVisibilityBarrier): уходящий
        // доигрывает уход прежде, чем войдёт следующий, а тап не меняет
        // реплику, пока актёр этой реплики ещё летит.





        private void ArmActorExitBarrier(Placement p)
        {
            if (p.ExitTransition == TransitionType.None || p.TransitionDuration <= 0.001f) return;
            _clock.Hold(LvnStageClock.ActorExitBarrier, p.TransitionDuration);
        }

        private void ArmActorVisibilityBarrier(JObject cmd, bool visibilityChanged, Placement p)
        {
            if (!visibilityChanged || !IsCharacterCommand(cmd)
                || p.TransitionDuration <= 0.001f) return;
            var transition = p.Show ? p.EnterTransition : p.ExitTransition;
            if (transition == TransitionType.None) return;
            _clock.Hold(LvnStageClock.ActorVisibilityBarrier, p.TransitionDuration);
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
                float left = _clock.Remaining(LvnStageClock.ActorExitBarrier);
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
            if (string.IsNullOrEmpty(id) || !_actorCmds.TryGetValue(id, out var cmd)) return;
            if (!BoolOr(cmd["show"], true)) return; // скрытого не воскрешать
            LvnAsync.Fire(ApplyActorAsync(cmd), "ApplyActor");
        }





        // ── ЖИВЫЕ СПРАЙТЫ СЦЕНЫ ЗАКРЕПЛЕНЫ (27.08): LRU стримингового окна
        // уничтожал текстуры прямо на экране — кукла меню становилась белым
        // квадратом, канвас серел («переключение актёров фон убивает»). Grace
        // окна считается от последнего ЗАПРОСА, а показанному давно арту
        // запросы не приходят. Всё, что сцена сейчас рисует, пиннится в
        // лоадере; замена или уход снимает пин. Слоты: "bg", "actor:<id>".
        private readonly Dictionary<string, List<Sprite>> _scenePins
            = new Dictionary<string, List<Sprite>>();

        private void RepinSceneSprites(string slot, IReadOnlyList<Sprite> next)
        {
            var cl = (Assets as CachingAssets)?.Loader;
            if (cl == null) return;
            List<Sprite> keep = null;
            if (next != null && next.Count > 0)
            {
                keep = new List<Sprite>(next.Count);
                foreach (var s in next)
                    // pin ДО unpin прежних: общий слой переживает замену
                    if (s != null) { cl.PinSprite(s, true); keep.Add(s); }
            }
            if (_scenePins.TryGetValue(slot, out var prev) && prev != null)
            {
                // Анпин ПРЕЖНИХ — С ЗАДЕРЖКОЙ: прокси смены облика ещё
                // показывает старые слои весь кроссфейд, и мгновенный анпин
                // отдавал их LRU прямо под ним — актёр вставал БЕЛЫМ
                // прямоугольником (живой скрин 27.08). Две секунды покрывают
                // самый длинный своп с запасом.
                LvnAsync.Fire(UnpinLaterAsync(prev), "UnpinLater");
            }
            if (keep == null) _scenePins.Remove(slot);
            else _scenePins[slot] = keep;
        }

        private async Task UnpinLaterAsync(List<Sprite> sprites)
        {
            await Task.Delay(2000);
            var cl = (Assets as CachingAssets)?.Loader;
            if (cl == null) return;
            foreach (var s in sprites) cl.PinSprite(s, false);
        }

        /// <summary>Актёр, чьи слои НЕ отпускаются при уборке сцены. Кукла меню
        /// стоит между главами всё время, и выгружать её арт на вход в главу
        /// значит перезагружать его на выходе — а пока он едет, слои рисуют
        /// сплошные прямоугольники («белый квадрат вместо героини»). Дешевле
        /// удержать один облик в памяти, чем каждый раз собирать заново
        /// (мысль Ильи 26.08: «нахера очищать героиню — её надо переодевать»).
        /// Хост ставит сюда своего фаворита меню.</summary>
        public string KeepActorAlive { get; set; }

        private void UnpinAllSceneSprites()
        {
            var cl = (Assets as CachingAssets)?.Loader;
            string keep = string.IsNullOrEmpty(KeepActorAlive) ? null : "actor:" + KeepActorAlive;
            List<string> drop = null;
            foreach (var kv in _scenePins)
            {
                if (keep != null && kv.Key == keep) continue; // этот облик остаётся жить
                if (cl != null) foreach (var s in kv.Value) cl.PinSprite(s, false);
                (drop ??= new List<string>()).Add(kv.Key);
            }
            if (drop != null) foreach (var k in drop) _scenePins.Remove(k);
        }

        /// <summary>Актёр виден ИЛИ его показ уже в полёте (слои грузятся).
        /// Хосту меню это отличает «кукла есть/едет» от «пропала — переслать».</summary>
        public bool ActorVisibleOrPending(string id)
            => !string.IsNullOrEmpty(id)
               && ((_placements.TryGetValue(id, out var p) && p.Show)
                   || (_actorTargets.TryGetValue(id, out var t) && t.Show));

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

        /// <summary>
        /// ЗАБЫТЬ АКТЁРА — сцена больше не помнит ни его последней команды, ни
        /// постановки, будто его в этой главе не ставили.
        ///
        /// <para>Нужно тому, кто выводил АКТЁРА НЕ ПО СЦЕНАРИЮ: гардероб,
        /// открытый посреди реплик, показывает манекен своей синтетической
        /// командой (центр, 0.92×1.06). Команда липкая — следующая авторская
        /// без position наследует от неё место и размер, и героиня остаётся
        /// стоять по центру до конца главы (живой репорт партнёра 28.08:
        /// «открыл гардероб, нажал полный рост, вернулся — ГГ по центру»).
        /// Спрятать манекен мало: память сцены о нём тоже должна уйти.</para>
        /// </summary>
        public void ForgetActor(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _actorCmds.Remove(id);
            _placements.Remove(id);
            _actorTargets.Remove(id);
        }

        /// <summary>Стояла ли эта роль на сцене по СЦЕНАРИЮ — то есть помнит ли
        /// сцена её команду. Хост спрашивает перед примеркой, чтобы понимать,
        /// свой это актёр или приведённый гардеробом манекен.</summary>
        public bool RememberedByScript(string id)
            => !string.IsNullOrEmpty(id) && _actorCmds.ContainsKey(id);

        /// <summary>Ids of actors currently VISIBLE on stage — hosts use it to
        /// pick who an always-open wardrobe should dress.</summary>
        public List<string> ActorsOnStage()
        {
            var list = new List<string>();
            foreach (var kv in _placements)
                if (kv.Value.Show) list.Add(kv.Key);
            return list;
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


        private int _wardrobeFocusGen;


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


        private async Task ApplyActorAsync(JObject cmd, bool wardrobeSwap = false,
                                           bool wardrobeFromTop = false)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;
            int epoch = _stageEpoch; // the scene this apply belongs to (see ResetStage)
            var lane = LvnStageClock.ActorLane(id);
            int gen = _clock.Claim(lane); // показ мой, пока не начнётся новее

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
                RepinSceneSprites("actor:" + id, null); // ушёл — окно свободно
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
                LvnLog.Trace($"[lvn-actor] {id}: слои [{string.Join(",", rls.ConvertAll(r => r.Id))}] "
                    + $"оси {{{string.Join(", ", System.Linq.Enumerable.Select(axes, kv => kv.Key + "=" + kv.Value))}}}");
                urls = new List<string>(rls.Count);
                urlIds = new List<string>(rls.Count);
                urlRects = new List<Vector4>(rls.Count);
                urlDefs = rls;
                foreach (var rl in rls) { urls.Add(rl.Url); urlIds.Add(rl.Id); urlRects.Add(new Vector4(rl.X, rl.Y, rl.W, rl.H)); }
            }
            else if (_cast != null && _cast.TryGetValue(id, out var entity))
            {
                // ЧЕРЕЗ КОСТЮМЕРА, как и путь каталога. Раньше здесь брались
                // СЫРЫЕ оси команды: на персонажа из блока `cast` не
                // действовали ни переменные ({var} уезжал в имя файла как
                // есть), ни гардероб — примерка и надетое до него просто не
                // доходили. Два пути одевали героя по разным правилам, и
                // отличались они одной буквой в имени метода.
                var axes = AxesOf(cmd);
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
            // …и где внутри этого холста стоит сама фигура: рост героя не должен
            // зависеть от того, сколько прозрачных полей оставил художник.
            if (aspectEntity?.content is LvnBox box && box.w > 0f && box.h > 0f)
            {
                placement.ContentX = box.x; placement.ContentY = box.y;
                placement.ContentW = box.w; placement.ContentH = box.h;
            }

            // Smart slots: never draw two actors standing inside each other.
            if (placement.Show)
            {
                var arbX = ArbitrateSlotX(placement.X, id, cmd["x"] != null,
                    _placements, SlotsOf(id), out var slotOwner);
                if (slotOwner != null && !Mathf.Approximately(arbX, placement.X))
                {
                    LvnLog.Trace($"[lvn-slot] '{id}' → {placement.X:0.00} занято '{slotOwner}' — авто-сдвиг в {arbX:0.00}");
                    placement.X = arbX;
                }
            }

            _actorTargets[id] = placement;
            // Команда запоминается ДО асинхронной загрузки слоёв: реплей
            // гардероба (Preview во время входа) обязан видеть ЭТУ команду.
            // Пока запись жила в конце апплая, реплей брал предыдущую — а после
            // переключения персонажа там лежал hide: свап прятал актёра и
            // новее-gen убивал летящий показ («Виктория на место не встаёт»,
            // живой скрин 27.08).
            _actorCmds[id] = cmd;

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
                // Силуэт — только для ПЕРВОГО входа: на уже видимом актёре
                // затемнённая заготовка читается как «вспышка» посреди смены
                // лица/наряда (живой репорт) — видимый держит прежний облик,
                // пока едет новый, и меняется одним кроссфейдом.
                if ((Theme?.LoadingSilhouette ?? true) && placement.Show
                    && IsCharacterCommand(cmd) && !wardrobeSwap && !wasVisibleBeforeShow
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
                        if (mini.Count > 0 && _clock.MayTouch(epoch, lane, gen))
                        {
                            var silPl = placement;
                            silPl.Silhouette = true;
                            LvnLog.Trace($"[lvn-actor] {id}: силуэт-заготовка ({mini.Count} слоёв) — полный арт доедет фоном");
                            _renderer?.ApplyActor(id, mini, silPl, onClick, miniIds, miniRects, miniDefs);
                            RepinSceneSprites("actor:" + id, mini); // заготовка на экране — держим
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
                () => _clock.MayTouch(epoch, lane, gen));
            // A newer apply started while our sprites loaded — ITS art must win;
            // this stale pass may not touch the renderer (late-arrival outfit bug).
            if (!_clock.IsNewest(lane, gen)) return;

            // The outgoing actor is already fading while this actor's layers load.
            // Only the visual reveal is serialized; cached/network work remains
            // concurrent, so the choreography adds no avoidable loading hitch.
            if (!wasVisibleBeforeShow && IsCharacterCommand(cmd))
            {
                await WaitForActorExitsAsync(epoch);
                if (!_clock.MayTouch(epoch, lane, gen)) return;
            }

            // Идущий кроссфейд облика ДОИГРЫВАЕТ: новое применение стыкуется за
            // ним, а не срезает в один кадр (срез — это «героиня мелькнула» у
            // гардероба: emotion=happy обрывал шторку смены наряда на середине).
            // Ожидание конечно: дедлайн — фиксированный момент (≤0.3 с), а тот,
            // кто его продлил, сначала уронил наш gen — выйдем по проверке.
            float swapLeft;
            while ((swapLeft = _clock.Remaining(LvnStageClock.SwapBarrier(id))) > 0.001f)
            {
                await Task.Delay(Mathf.Max(1, Mathf.CeilToInt(swapLeft * 1000f)));
                if (!_clock.MayTouch(epoch, lane, gen)) return;
            }

            // Loading may have outlived the early nominal barrier. Re-arm from
            // the frame where the renderer actually starts the entrance.
            ArmActorVisibilityBarrier(cmd, visibilityChanged, placement);
            _renderer?.ApplyActor(id, layers, placement, onClick, layerIds, layerRects, layerDefs);
            RepinSceneSprites("actor:" + id, layers); // что на экране — LRU не трогает
            placement.SmoothPosition = false;
            placement.WardrobeSwap = false;
            placement.WardrobeFromTop = false;
            _placements[id] = placement; // the sticky base for the next command
            // _actorCmds записана в синхронной части (см. выше): поздняя запись
            // здесь могла бы затереть более новую команду, прилетевшую пока
            // грузились слои.

            // Animations (rigged entities): idle (whole-actor) + blink (a layer)
            // auto-run on show; play="name" fires a one-shot gesture; an
            // auto:"speaking" anim is remembered for lip-sync while this actor talks.
            var animEntity = Catalog != null ? Catalog.Get(id) : null;
            if (animEntity != null && animEntity.anim != null && animEntity.anim.Count > 0)
            {
                await PreloadFramesAsync(id, animEntity);
                // The frame preload awaited network — a chapter change or a newer
                // apply may own the actor now; stale anim state must not leak in.
                if (!_clock.MayTouch(epoch, lane, gen)) return;

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
    }
}
