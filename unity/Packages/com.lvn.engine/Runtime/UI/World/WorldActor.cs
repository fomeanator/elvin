using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.UI.World
{
    /// <summary>
    /// An actor rendered on a uGUI Canvas (RectTransform + Image layers), animated
    /// in the Update loop for smooth 60fps — the rendering path Liminal uses and
    /// the one that also hosts Spine (SkeletonGraphic). It plays the SAME
    /// <see cref="LvnAnim"/> data and channels (base/blink/talk/gesture) as the
    /// UITK path, reusing the static sampling in <see cref="ActorAnimator"/>; only
    /// the apply target differs (RectTransform/CanvasGroup/Image vs VisualElement).
    ///
    /// <para>Composition: this MonoBehaviour's RectTransform is the placed slot;
    /// a child <c>rig</c> RectTransform carries the animation transform and the
    /// Image layers, so animating the rig never fights the slot's placement.</para>
    /// </summary>
    public sealed class WorldActor : MonoBehaviour
    {
        private RectTransform _slot;
        private RectTransform _rig;
        private RectTransform _transition;
        private CanvasGroup _group;
        private LvnActorComposite _transitionComposite;
        private readonly Dictionary<string, Image> _layers = new Dictionary<string, Image>();
        private readonly Dictionary<string, Sprite> _baseSprite = new Dictionary<string, Sprite>();
        private Dictionary<string, Dictionary<string, Sprite>> _frames;
        private readonly Dictionary<string, Active> _channels = new Dictionary<string, Active>();
        private readonly Dictionary<string, Queue<LvnAnim>> _queue = new Dictionary<string, Queue<LvnAnim>>(); // mode=queue pending steps
        private Vector2 _slotBase;
        private Vector2 _slotMoveFrom, _slotMoveTo;
        private float _slotMoveStart = -1f, _slotMoveDuration;

        /// <summary>Reference content size (canvas units) for screen_x/screen_y travel.</summary>
        public Vector2 ContentSize = new Vector2(1080f, 1920f);

        /// <summary>The placed slot RectTransform (this MonoBehaviour's own). The
        /// host positions it via <see cref="WorldPlacement"/>; animation only ever
        /// moves the child rig, so placement and animation never fight.</summary>
        public RectTransform Slot { get { EnsureRig(); return _slot; } }

        /// <summary>The animated rig (child of the slot) — mount runtime guests
        /// (a Spine skeleton) here so anim/move channels and the CanvasGroup
        /// fade drive them exactly like sprite layers.</summary>
        public RectTransform Rig { get { EnsureRig(); return _rig; } }

        /// <summary>Узел, которым двигает ПЕРЕХОД (вход/уход со сносом вбок).
        /// Свой, чтобы не делить поле позиции с постановкой и анимацией.</summary>
        public RectTransform Transition { get { EnsureRig(); return _transition; } }

        /// <summary>Make a layered actor one visual for the duration of a normal
        /// alpha transition.  A single-layer actor stays on the ordinary path.</summary>
        public bool BeginTransitionVisual()
        {
            EnsureRig();
            // Прокси-композит рисует сырые текстуры слоёв — надетый sfx (тёмный
            // силуэт, голограмма) он воспроизвести не может: герой-голограмма
            // «раздевался» до светлого арта на время каждого фейда. С эффектом
            // переход играет живыми слоями.
            if (LvnSpriteFxDriver.WearsAuthoredFx(gameObject)) return false;
            if (_transitionComposite == null)
                _transitionComposite = GetComponent<LvnActorComposite>() ?? gameObject.AddComponent<LvnActorComposite>();
            return _transitionComposite.Begin(_transition, _rig);
        }

        /// <summary>Snapshot even a single-layer current look before replacing
        /// it. Wardrobe/emotion swaps use this as the opaque outgoing card.</summary>
        public bool BeginArtSwapVisual()
        {
            EnsureRig();
            // Та же причина, что у переходов: снимок-прокси не умеет носить sfx.
            if (LvnSpriteFxDriver.WearsAuthoredFx(gameObject)) return false;
            if (_transitionComposite == null)
                _transitionComposite = GetComponent<LvnActorComposite>() ?? gameObject.AddComponent<LvnActorComposite>();
            return _transitionComposite.Begin(_transition, _rig, includeSingleLayer: true);
        }

        /// <summary>Авторский sfx применили ПОСРЕДИ композитного перехода:
        /// сценарий пишет «actor …» и «sfx …» подряд, и переход стартует на
        /// команду раньше эффекта. Прокси рисует сырые слои и эффект не
        /// наденет — возвращаем живые (уже одетые) слои; CanvasGroup-фейд
        /// перехода продолжает вести их дальше без скачка.</summary>
        public void DropCompositeForFx()
        {
            if (_transitionComposite != null && _transitionComposite.Active)
                EndTransitionVisual();
        }

        public void CrossfadeArtSwap(float seconds, bool wardrobeFlow = false,
                                     bool wardrobeFromTop = false)
        {
            if (_transitionComposite != null && _transitionComposite.Active)
                _transitionComposite.CrossfadeToLive(seconds, wardrobeFlow, wardrobeFromTop);
            else EndTransitionVisual();
        }

        /// <summary>Идёт ли сейчас композитный переход (снимок-прокси активен).
        /// Пустое пере-применение не смеет его гасить.</summary>
        public bool HasActiveTransitionVisual
            => _transitionComposite != null && _transitionComposite.Active;

        /// <summary>Тёмный тинт «силуэта-проявления»: @mini-заготовка рисуется
        /// почти чёрной, полный арт возвращает слоям белый (кроссфейд облика
        /// смешивает тёмное со светлым — актёр «проявляется»).</summary>
        public void SetSilhouette(bool on)
        {
            EnsureRig();
            var tint = on ? new Color(0.07f, 0.08f, 0.09f) : Color.white;
            foreach (var img in _rig.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                img.color = tint;
        }

        /// <summary>Слои есть, а рисовать нечем: спрайт умер уже ПОСЛЕ того,
        /// как его поставили (LRU/выгрузка забрали текстуру из-под живой
        /// куклы). Image без спрайта заливает свой прямоугольник сплошным
        /// цветом — это и есть «белый прямоугольник вместо героини». Сцена
        /// чинит такое пересборкой облика; см. VnStage.HealDeadActors.</summary>
        public bool HasDeadLayers()
        {
            // ПОГАШЕННЫЙ СЛОЙ — ВСЁ ЕЩЁ БОЛЬНОЙ. Гашение убирает пятно с
            // экрана, но выключенный Image перестаёт попадать в проверку ниже
            // (она смотрит на enabled), и Лекарь считал фигуру здоровой — а
            // героини на экране не было вовсе: погасили пять слоёв и не
            // пересобрали ни одного (живой лог Ильи 28.08). Гашение обязано
            // ЗВАТЬ лечение, а не заменять его.
            if (_hushed.Count > 0) return true;
            foreach (var pair in _layers)
            {
                var img = pair.Value;
                if (img == null) continue;
                // Спрайт может пережить свою ТЕКСТУРУ: выгружается атлас, а
                // ссылка на спрайт остаётся живой. Рисуется при этом ровно тот
                // же сплошной прямоугольник, поэтому смотрим до конца.
                if (img.enabled && (img.sprite == null || img.sprite.texture == null)) return true;
            }
            return false;
        }

        /// <summary>
        /// НЕЧЕМ РИСОВАТЬ — НЕ РИСУЕМ. Image без спрайта заливает свой
        /// прямоугольник сплошным цветом: белым в кадре, серым под вуалью
        /// перехода — «серый спрайт при выходе из новеллы» (Илья 28.08).
        ///
        /// <para>Это не сокрытие поломки: слой, потерявший спрайт, ПУСТ, и
        /// честно нарисовать пустоту значит не рисовать ничего. Сама поломка
        /// никуда не девается — тем же признаком её видит Лекарь, пишет в
        /// журнал и пересобирает облик. Разница в том, кто это видит: лог или
        /// игрок.</para>
        ///
        /// <para>Возвращает, сколько слоёв пришлось погасить.</para>
        /// </summary>
        public int HideDeadLayers()
        {
            int hushed = 0;
            foreach (var pair in _layers)
            {
                var img = pair.Value;
                if (img == null || !img.enabled) continue;
                if (img.sprite != null && img.sprite.texture != null) continue;
                img.enabled = false;
                _hushed.Add(pair.Key);   // помним: этот слой ждёт лечения
                hushed++;
            }
            return hushed;
        }

        /// <summary>Слои, погашенные за неимением картинки. Пока список не
        /// пуст, фигура НЕ ЦЕЛА, сколько бы живых слоёв на ней ни осталось.</summary>
        private readonly System.Collections.Generic.HashSet<string> _hushed
            = new System.Collections.Generic.HashSet<string>();

        /// <summary>Что на фигуре надето — спрайты живых слоёв. Кладовщик
        /// закрепляет их заново, когда фигуру показали без пересборки.</summary>
        public System.Collections.Generic.List<Sprite> Sprites()
        {
            System.Collections.Generic.List<Sprite> list = null;
            foreach (var pair in _layers)
            {
                var img = pair.Value;
                if (img == null || img.sprite == null) continue;
                (list ??= new System.Collections.Generic.List<Sprite>()).Add(img.sprite);
            }
            return list;
        }

        /// <summary>Фигура ЦЕЛА: слои на месте и каждому есть чем рисовать.
        /// Показать такую — значит включить её, а не собирать заново.</summary>
        public bool ArtAlive()
        {
            if (_hushed.Count > 0) return false;   // дырявую фигуру не показываем как есть
            bool any = false;
            foreach (var pair in _layers)
            {
                var img = pair.Value;
                if (img == null) continue;
                if (img.sprite == null || img.sprite.texture == null) return false;
                any = true;
            }
            return any;
        }

        /// <summary>Return from the flat transition visual to live animated layers.</summary>
        public void EndTransitionVisual()
        {
            if (_transitionComposite != null) _transitionComposite.End();
            // Transition is a disposable visual offset, never stage placement.
            // Keep a hard invariant at every hand-off back to the live layers so
            // a cancelled/disabled drift cannot leak its edge offset into the
            // following dialogue beat. The parent Slot owns all real movement.
            if (_transition != null) _transition.anchoredPosition = Vector2.zero;
        }

        /// <summary>Static placement opacity (multiplied by alpha tracks).</summary>
        public void SetBaseOpacity(float a)
        {
            EnsureRig();
            _heldAlpha = Mathf.Clamp01(a);
            if (_group != null) _group.alpha = _heldAlpha;
        }

        // A finished one-shot alpha tween HOLDS its final value (a faded-out
        // actor must not pop back when the channel ends) — the genre standard.
        private float _heldAlpha = 1f;

        private sealed class Active
        {
            public LvnAnim anim; public float start; public Action onDone;
            public float[] Arc; // arc-length table for a spline path pair (built lazily)
        }

        private void Awake() => EnsureRig();

        private void EnsureRig()
        {
            if (_slot != null) return;
            _slot = (RectTransform)transform;
            _slotBase = _slot.anchoredPosition;
            // У КАЖДОГО ТРАНСФОРМА ОДИН ХОЗЯИН. Слот принадлежит постановке и
            // жестам актёра, rig — анимации. Переходу нужен свой узел: пока он
            // писал в слот, они с анимацией дрались за одно поле каждый кадр
            // (порядок Update не определён) — отсюда рывки и «уехавшая» база.
            var transitionGo = new GameObject("transition", typeof(RectTransform));
            _transition = (RectTransform)transitionGo.transform;
            _transition.SetParent(_slot, false);
            WorldPlacement.Stretch(_transition);
            var rigGo = new GameObject("rig", typeof(RectTransform), typeof(CanvasGroup));
            _rig = (RectTransform)rigGo.transform;
            _rig.SetParent(_transition, false);
            WorldPlacement.Stretch(_rig);
            _group = rigGo.GetComponent<CanvasGroup>();
        }


        /// <summary>Build (or rebuild) the layer Images from resolved sprites + ids.
        /// <paramref name="layerDefs"/> carries bone metadata (parent/pivot/spring).</summary>
        public void Configure(IReadOnlyList<Sprite> sprites, IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects = null,
            IReadOnlyList<Lvn.Content.SpriteCatalog.ResolvedLayer> layerDefs = null)
        {
            EnsureRig();
            // ТОТ ЖЕ ОБЛИК — НИЧЕГО НЕ ДЕЛАЕМ. Пересборка слоёв уничтожала и
            // создавала заново ВСЕ Image на каждом применении `actor`, даже
            // когда набор спрайтов не менялся. В живой главе таких применений
            // 775 на 730 реплик: почти каждая строка диалога роняла батчи,
            // перестраивала канвас и кормила сборщик мусора. Это и есть те
            // микрозадержки на показе и скрытии.
            var signature = VisualSignature(sprites, layerIds, layerRects, layerDefs);
            if (signature == _signature && _rig.childCount > 0)
            {
                // ТОТ ЖЕ ОБЛИК, НО СЛОИ МОГЛИ БЫТЬ ПОГАШЕНЫ. Слой, потерявший
                // спрайт, замолкает (HideDeadLayers), и если спрайт вернулся —
                // а сюда мы попали именно потому, что набор прежний, — его
                // нужно снова дать рисовать. Иначе фигура осталась бы навсегда
                // с дырой вместо погашенного слоя.
                foreach (var pair in _layers)
                {
                    var img0 = pair.Value;
                    if (img0 == null || img0.enabled) continue;
                    if (img0.sprite == null || img0.sprite.texture == null) continue;
                    img0.enabled = true;
                    _hushed.Remove(pair.Key);   // слой вернулся — он больше не болен
                }
                return;
            }
            _signature = signature;
            for (int i = _rig.childCount - 1; i >= 0; i--) Destroy(_rig.GetChild(i).gameObject);
            _layers.Clear(); _baseSprite.Clear(); _bones.Clear();
            _hushed.Clear();   // фигуру собирают заново — прежние жалобы неактуальны
            if (sprites == null) { _signature = 0; return; }
            // A layer is a bone when it has bone data OR when someone attaches to it.
            HashSet<string> boneParents = null;
            if (layerDefs != null)
                foreach (var d0 in layerDefs)
                    if (!string.IsNullOrEmpty(d0.Parent))
                        (boneParents ??= new HashSet<string>()).Add(d0.Parent);
            for (int i = 0; i < sprites.Count; i++)
            {
                var sp = sprites[i];
                if (sp == null) continue;
                var go = new GameObject("layer" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_rig, false);
                // Partial overlay (rect w,h > 0) → anchored to its sub-rect of the box
                // (fractions, top-left origin → uGUI bottom-up anchors); else fill.
                var r = layerRects != null && i < layerRects.Count ? layerRects[i] : Vector4.zero;
                if (r.z > 0f && r.w > 0f)
                {
                    rt.anchorMin = new Vector2(r.x, 1f - (r.y + r.w));
                    rt.anchorMax = new Vector2(r.x + r.z, 1f - r.y);
                    rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                }
                else WorldPlacement.Stretch(rt);
                var img = go.GetComponent<Image>();
                img.sprite = sp;
                img.raycastTarget = false;
                img.preserveAspect = true;
                var lid = layerIds != null && i < layerIds.Count ? layerIds[i] : null;
                if (!string.IsNullOrEmpty(lid))
                {
                    // Stable address for `sfx id=… part=<layer id>`. The layer
                    // dictionary is private runtime state; the transform name lets
                    // per-part effects survive without coupling the FX driver to
                    // the actor renderer's implementation.
                    go.name = "layer:" + lid;
                    _layers[lid] = img; _baseSprite[lid] = sp;
                    if (layerDefs != null && i < layerDefs.Count)
                    {
                        var d = layerDefs[i];
                        if (!string.IsNullOrEmpty(d.Parent) || d.Spring > 0f
                            || (boneParents != null && boneParents.Contains(lid)))
                        {
                            var rr = r.z > 0f && r.w > 0f ? r : new Vector4(0f, 0f, 1f, 1f);
                            rt.pivot = new Vector2(d.Px, 1f - d.Py); // rotation/scale joint (uGUI y-up)
                            _bones[lid] = new BoneSolver.RigBone
                            {
                                Parent = d.Parent,
                                PivotBox = new Vector2(rr.x + d.Px * rr.z, rr.y + d.Py * rr.w),
                                Rect = rr, Spring = d.Spring, Damping = d.Damping,
                            };
                        }
                    }
                }
            }
            // Пересборка уничтожила прежние Image вместе с надетым материалом
            // эффектов — свежие слои надо одеть заново, иначе липкое состояние
            // sfx (тёмный силуэт, аура) слетает в момент догрузки слоя.
            LvnSpriteFxDriver.Reskin(gameObject);
        }
        private int _signature;   // облик, из которого собраны текущие слои

        public bool VisualWouldChange(IReadOnlyList<Sprite> sprites, IReadOnlyList<string> layerIds,
            IReadOnlyList<Vector4> layerRects = null,
            IReadOnlyList<Lvn.Content.SpriteCatalog.ResolvedLayer> layerDefs = null)
        {
            EnsureRig();
            return VisualSignature(sprites, layerIds, layerRects, layerDefs) != _signature
                || _rig.childCount == 0;
        }

        /// <summary>Отпечаток ВИДИМОГО состава: сами спрайты, их адреса, куски
        /// рамки и кости. Всё, от чего зависит построенное дерево слоёв, — и
        /// ничего сверх того, иначе одинаковый облик перестанет узнаваться.</summary>
        internal static int VisualSignature(IReadOnlyList<Sprite> sprites, IReadOnlyList<string> layerIds,
            IReadOnlyList<Vector4> layerRects, IReadOnlyList<Lvn.Content.SpriteCatalog.ResolvedLayer> layerDefs)
        {
            if (sprites == null) return 0;
            unchecked
            {
                int h = 17;
                for (int i = 0; i < sprites.Count; i++)
                {
                    h = h * 31 + (sprites[i] != null ? sprites[i].GetInstanceID() : 0);
                    if (layerIds != null && i < layerIds.Count && layerIds[i] != null)
                        h = h * 31 + layerIds[i].GetHashCode();
                    if (layerRects != null && i < layerRects.Count)
                        h = h * 31 + layerRects[i].GetHashCode();
                    if (layerDefs != null && i < layerDefs.Count)
                    {
                        var d = layerDefs[i];
                        h = h * 31 + (d.Parent != null ? d.Parent.GetHashCode() : 0);
                        h = h * 31 + d.Px.GetHashCode() * 7 + d.Py.GetHashCode() * 13;
                        h = h * 31 + d.Spring.GetHashCode() * 3 + d.Damping.GetHashCode() * 5;
                    }
                }
                return h == 0 ? 1 : h;   // 0 держим за «слоёв нет»
            }
        }

        private readonly Dictionary<string, BoneSolver.RigBone> _bones = new Dictionary<string, BoneSolver.RigBone>();
        private float _lastTick = -1f;

        public Vector2 SlotBase { get { EnsureRig(); return _slotBase; } }

        public void SetSlotBase(Vector2 anchored)
        {
            EnsureRig();
            _slotMoveStart = -1f;
            _slotBase = anchored;
            _slot.anchoredPosition = anchored;
        }

        /// <summary>Tween the placement-owned base while rig animation continues
        /// to add its own screen_x/screen_y offset on top.</summary>
        public void MoveSlotBase(Vector2 from, Vector2 to, float seconds)
        {
            EnsureRig();
            if (seconds <= 0.001f || (from - to).sqrMagnitude <= 0.0001f)
            {
                SetSlotBase(to);
                return;
            }
            _slotMoveFrom = from;
            _slotMoveTo = to;
            _slotMoveDuration = seconds;
            _slotMoveStart = ActorAnimator.Clock();
            _slotBase = from;
            _slot.anchoredPosition = from;
        }
        public void SetFrames(Dictionary<string, Dictionary<string, Sprite>> frames) => _frames = frames;

        public bool Has(string channel) => _channels.ContainsKey(channel);
        public LvnAnim Current(string channel) => _channels.TryGetValue(channel, out var a) ? a.anim : null;

        public void Play(string channel, LvnAnim anim, Action onDone = null)
        {
            if (!LvnAnim.Playable(anim)) { onDone?.Invoke(); return; }
            _channels[channel] = new Active { anim = anim, start = ActorAnimator.Clock(), onDone = onDone };
        }

        /// <summary>Play after the current anim on this channel finishes (mode=queue).
        /// Free lane → plays now; queued steps run FIFO. Don't queue behind a loop.</summary>
        public void PlayQueued(string channel, LvnAnim anim)
        {
            if (!LvnAnim.Playable(anim)) return;
            if (!_channels.ContainsKey(channel)) { Play(channel, anim); return; }
            if (!_queue.TryGetValue(channel, out var q)) _queue[channel] = q = new Queue<LvnAnim>();
            q.Enqueue(anim);
        }
        public void Stop(string channel) { _channels.Remove(channel); _queue.Remove(channel); if (_channels.Count == 0) ResetTargets(); }
        public void StopAll() { _channels.Clear(); _queue.Clear(); ResetTargets(); }

        /// <summary>Stop every script-driven lane ("script:*"), leaving engine lanes.</summary>
        public void StopScript()
        {
            // Правила дорожек — в AnimLanes; здесь остаётся только СВОЙ способ
            // вернуть цели в покой, ради которого копия и существовала.
            if (AnimLanes.DropScript(_channels, _queue)) ResetTargets();
        }

        /// <summary>Stop one lane by exact name or by the derived "script:&lt;target&gt;".</summary>
        public void StopTarget(string target)
        {
            _queue.Remove(target);
            _queue.Remove("script:" + target);
            bool r = _channels.Remove(target);
            r |= _channels.Remove("script:" + target);
            if (r && _channels.Count == 0) ResetTargets();
        }

        public void EnsureIdle(string id, LvnAnim idle) { if (idle != null && !ReferenceEquals(Current("base"), idle)) Play("base", idle); }
        public void EnsureBlink(string id, LvnAnim blink) { if (blink != null && !ReferenceEquals(Current("blink"), blink)) Play("blink", blink); }
        public void Talk(LvnAnim talk, bool on) { if (on) { if (talk != null && !ReferenceEquals(Current("talk"), talk)) Play("talk", talk); } else Stop("talk"); }
        public void PlayGesture(LvnAnim anim, LvnAnim idle)
        {
            if (anim == null) return;
            if (anim.loop) { Play("gesture", anim); return; }
            Stop("base");
            Play("gesture", anim, onDone: () => { if (idle != null) EnsureIdle(null, idle); });
        }

        private void Update()
        {
            float now = ActorAnimator.Clock();
            bool moved = StepSlotMove(now);
            // Springs keep swinging after their driving channel ends.
            if (_channels.Count > 0 || BoneSolver.AnySpringLive(_bones.Values)) Tick(now);
            else if (moved) _slot.anchoredPosition = _slotBase;
        }

        private bool StepSlotMove(float now)
        {
            if (_slotMoveStart < 0f) return false;
            float t = Mathf.Clamp01((now - _slotMoveStart) / Mathf.Max(0.0001f, _slotMoveDuration));
            float k = Mathf.SmoothStep(0f, 1f, t);
            _slotBase = Vector2.LerpUnclamped(_slotMoveFrom, _slotMoveTo, k);
            if (t >= 1f) _slotMoveStart = -1f;
            return true;
        }

        // One composite step — internal so tests can drive it with ActorAnimator.Clock.
        internal void Tick(float now)
        {
            if (_rig == null) EnsureRig();
            float tx = 0f, ty = 0f, scx = 1f, scy = 1f, rot = 0f, al = 1f, sx = 0f, sy = 0f;
            var layerX = new Dictionary<string, float[]>(); // id -> {tx,ty,scx,scy,rot,al}
            Dictionary<string, string> layerFrame = null;
            List<string> done = null;

            foreach (var kv in _channels)
            {
                var act = kv.Value;
                var anim = act.anim;
                // Где анимация сейчас — спрашиваем у часов канала: петля,
                // качание, конец и равномерная скорость вдоль пути одинаковы
                // для плоской фигуры и для этой (см. ActorAnimator.ChannelClock).
                var clock = ActorAnimator.ClockOf(anim, now - act.start, ref act.Arc);
                float t = clock.T;

                LvnAnimTrack orientX = null, pathY = null; // move … orient=true: face along the path
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || tr.keys == null || tr.keys.Count == 0 || string.IsNullOrEmpty(tr.prop)) continue;
                    if (!Lvn.UI.LvnAnimProp.Check(tr.prop, tr.layer)) continue;
                    if (tr.prop == "frame")
                    {
                        if (!string.IsNullOrEmpty(tr.layer))
                            (layerFrame ??= new Dictionary<string, string>())[tr.layer] = ActorAnimator.SampleFrame(tr, t);
                        continue;
                    }
                    bool onPath = clock.OnPath(tr);
                    float v = ActorAnimator.Sample(tr, clock.TimeOf(tr), easeless: onPath);
                    if (string.IsNullOrEmpty(tr.layer))
                    {
                        switch (tr.prop)
                        {
                            case "x": tx = v; break;
                            case "y": ty = v; break;
                            case "screen_x": sx = v; if (tr.orient) orientX = tr; break;
                            case "screen_y": sy = v; pathY = tr; break;
                            case "scale": scx = v; scy = v; break;
                            case "scalex": scx = v; break;
                            case "scaley": scy = v; break;
                            case "rotation": rot = v; break;
                            case "alpha": al = v; break;
                        }
                    }
                    else
                    {
                        if (!layerX.TryGetValue(tr.layer, out var a)) layerX[tr.layer] = a = new[] { 0f, 0f, 1f, 1f, 0f, 1f };
                        switch (tr.prop)
                        {
                            case "x": a[0] = v; break;
                            case "y": a[1] = v; break;
                            case "scale": a[2] = v; a[3] = v; break;
                            case "scalex": a[2] = v; break;
                            case "scaley": a[3] = v; break;
                            case "rotation": a[4] = v; break;
                            case "alpha": a[5] = v; break;
                        }
                    }
                }
                // OrientAngle is y-down clockwise-positive; Canvas euler z is
                // counter-clockwise-positive — negate.
                if (orientX != null && pathY != null)
                    rot = -ActorAnimator.OrientAngle(orientX, pathY, clock.OrientT, clock.Duration);
                if (clock.Finished) (done ??= new List<string>()).Add(kv.Key);
            }

            ApplyRig(_rig, _group, tx, ty, scx, scy, rot, al);
            _heldAlpha = al; // survives the channel ending (fade-out stays faded)
            _slot.anchoredPosition = _slotBase + new Vector2(sx * ContentSize.x, -sy * ContentSize.y);

            // Bone layers compose through the FK chain (+ springs). The solver
            // works y-down/clockwise (the UITK convention) — flip on both ends.
            Dictionary<string, BoneSolver.Pose> bonePoses = null;
            if (_bones.Count > 0)
            {
                float bdt = _lastTick >= 0f ? Mathf.Clamp(now - _lastTick, 0f, 0.1f) : 0f;
                var bones = new List<BoneSolver.Bone>(_bones.Count);
                foreach (var kv in _bones)
                {
                    var m = kv.Value;
                    float[] la = layerX.TryGetValue(kv.Key, out var arr) ? arr : null;
                    bones.Add(new BoneSolver.Bone
                    {
                        Id = kv.Key, Parent = m.Parent, Pivot = m.PivotBox,
                        Tx = (la?[0] ?? 0f) * m.Rect.z, Ty = (la?[1] ?? 0f) * m.Rect.w,
                        Angle = -(la?[4] ?? 0f), Sx = la?[2] ?? 1f, Sy = la?[3] ?? 1f,
                    });
                }
                bonePoses = BoneSolver.Solve(bones);
                // slot travel (drag / move) sways the springs like local motion
                var slotNorm = new Vector2(_slot.anchoredPosition.x / ContentSize.x,
                                           -_slot.anchoredPosition.y / ContentSize.y);
                bool anySpring = false;
                for (int i = 0; i < bones.Count; i++)
                {
                    var m = _bones[bones[i].Id];
                    if (m.Spring <= 0f) continue;
                    m.State = BoneSolver.SpringStep(m.State, bonePoses[bones[i].Id].PivotWorld + slotNorm, bonePoses[bones[i].Id].Angle, m.Spring, m.Damping, bdt);
                    if (Mathf.Abs(m.State.Angle) > 0.01f || Mathf.Abs(m.State.Velocity) > 0.01f) anySpring = true;
                    var b = bones[i]; b.Angle += m.State.Angle; bones[i] = b;
                }
                if (anySpring) bonePoses = BoneSolver.Solve(bones);
            }
            _lastTick = now;

            var rigSize = _rig.rect.size;
            foreach (var pair in _layers)
            {
                var img = pair.Value;
                var lrt = (RectTransform)img.transform;
                if (bonePoses != null && _bones.TryGetValue(pair.Key, out var bm) && bonePoses.TryGetValue(pair.Key, out var pose))
                {
                    var dlt = pose.PivotWorld - bm.PivotBox;
                    lrt.anchoredPosition = new Vector2(dlt.x * rigSize.x, -dlt.y * rigSize.y);
                    lrt.localEulerAngles = new Vector3(0f, 0f, -pose.Angle);
                    lrt.localScale = new Vector3(pose.Sx, pose.Sy, 1f);
                    var c2 = img.color; c2.a = layerX.TryGetValue(pair.Key, out var la2) ? la2[5] : 1f; img.color = c2;
                }
                else if (layerX.TryGetValue(pair.Key, out var a)) ApplyRig(lrt, null, a[0], a[1], a[2], a[3], a[4], a[5], img);
                else ApplyRig(lrt, null, 0, 0, 1, 1, 0, 1, img);
                if (layerFrame != null && layerFrame.TryGetValue(pair.Key, out var fv))
                {
                    if (_frames != null && _frames.TryGetValue(pair.Key, out var map) && map.TryGetValue(fv, out var sp) && sp != null)
                        img.sprite = sp;
                }
                else if (_baseSprite.TryGetValue(pair.Key, out var bs) && bs != null) img.sprite = bs;
            }

            if (done != null)
                foreach (var c in done)
                {
                    var cb = _channels.TryGetValue(c, out var x) ? x.onDone : null;
                    _channels.Remove(c);
                    cb?.Invoke();
                    // mode=queue: start the next queued step on this lane
                    if (_queue.TryGetValue(c, out var q) && q.Count > 0)
                        _channels[c] = new Active { anim = q.Dequeue(), start = now };
                    if (_queue.TryGetValue(c, out var q2) && q2.Count == 0) _queue.Remove(c);
                }
            if (_channels.Count == 0) ResetTargets();
        }

        private void ApplyRig(RectTransform rt, CanvasGroup group, float tx, float ty, float scx, float scy, float rot, float al, Image img = null)
        {
            var size = rt.rect.size;
            rt.anchoredPosition = new Vector2(tx * size.x, -ty * size.y);
            rt.localScale = new Vector3(scx, scy, 1f);
            rt.localEulerAngles = new Vector3(0f, 0f, rot);
            if (group != null) group.alpha = al;
            else if (img != null) { var c = img.color; c.a = al; img.color = c; }
        }

        private void ResetTargets()
        {
            if (_rig == null) return;
            ApplyRig(_rig, _group, 0, 0, 1, 1, 0, _heldAlpha);
            _slot.anchoredPosition = _slotBase;
            foreach (var pair in _layers)
            {
                ApplyRig((RectTransform)pair.Value.transform, null, 0, 0, 1, 1, 0, 1, pair.Value);
                if (_baseSprite.TryGetValue(pair.Key, out var bs) && bs != null) pair.Value.sprite = bs;
            }
        }
    }
}
