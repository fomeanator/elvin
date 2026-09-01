using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// РАЗБОР КОМАНДЫ АКТЁРА — часть <see cref="VnStage"/>: как строчка скрипта
    /// («actor victoria center emotion=idle w=0.92») превращается в постановку.
    ///
    /// <para>Именованные места, арбитраж занятых слотов, липкое наследование от
    /// прошлой команды, оси облика, переходы по умолчанию и темп. Чистая
    /// логика без загрузок — потому и стоит отдельно от тракта, который потом
    /// эту постановку исполняет.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        private static bool IsCharacterCommand(JObject cmd)
            => !string.Equals((string)cmd?["op"], "obj", StringComparison.OrdinalIgnoreCase);

        /// <summary>ВИДЕН ЛИ ПЕРЕХОД У ЭТОЙ КОМАНДЫ — один вопрос, два
        /// потребителя.
        ///
        /// <para>Спрашивают об этом двое и делают разное: один удлиняет
        /// переход, другой держит ввод на его время. Формула стояла у обоих
        /// своей копией, а расходиться им нельзя ни на волос: разойдись —
        /// и ввод откроется РАНЬШЕ, чем героиня доехала, либо будет ждать
        /// перехода, которого нет. Оба случая игрок видит, а лог молчит.</para>
        ///
        /// <para>Четыре условия и все обязательны: видимость и правда
        /// поменялась, команда про фигуру (у фона свои правила), длительность
        /// не нулевая, и переход назван словом, а не <c>None</c>.</para>
        /// </summary>
        private static bool ShowsVisibleTransition(JObject cmd, bool visibilityChanged, Placement p)
        {
            if (!visibilityChanged || !IsCharacterCommand(cmd) || p.TransitionDuration <= 0.001f)
                return false;
            return (p.Show ? p.EnterTransition : p.ExitTransition) != TransitionType.None;
        }

        private static void LengthenCharacterVisibility(JObject cmd, bool visibilityChanged,
                                                         ref Placement p)
        {
            if (!ShowsVisibleTransition(cmd, visibilityChanged, p)) return;
            p.TransitionDuration *= ActorVisibilityDurationScale;
        }

        private static void ApplyPresentationTempo(ref Placement p)
        {
            if (p.TransitionDuration > 0.001f)
                p.TransitionDuration = VnTheme.Motion(p.TransitionDuration);
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

        // Build placement from the command — everything in screen fractions so a
        // script controls any object's position, size, anchor, z, flip, rotation
        // and opacity without knowing the resolution.
        /// <summary>A named slot's x for an entity: the catalog def's per-entity
        /// override wins over the global table (see LvnSpriteEntity.slots).</summary>
        internal static float SlotXFor(string position, IReadOnlyDictionary<string, float> slots)
            => position != null && slots != null && slots.TryGetValue(position, out var v)
                ? v : Placement.SlotX(position);

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
            // УШЕДШИЙ ЗА КАДР НЕ УЧАСТВУЕТ В ТОЛКОТНЕ. Двое, уведённые в одну
            // кулису, считались занявшими одно место, и второго «расталкивание»
            // возвращало на ближайший свободный слот — то есть В КАДР, к левому
            // краю. Актёр, которого автор убрал со сцены, выходил обратно.
            if (desired < 0f || desired > 1f) return desired;
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

        /// <summary>ЛИПКАЯ РАССТАНОВКА: команда актёра накладывается на его
        /// ПОСЛЕДНЮЮ применённую — меняются только поля, которые команда назвала
        /// прямо. Поэтому <c>actor id=knight play="Jump"</c> оставляет героя
        /// там, куда его увели перетаскивание, движение или прежняя команда.
        /// Переходы одноразовы и всегда приходят из команды.</summary>
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

        // Во что актёр одет в этой команде — вопрос КОСТЮМЕРА: сцена лишь
        // приносит ему оси, как их написал автор, и умение развернуть {var}
        // по переменным игрока. Правило (шаблон ведёт переменная и примерка
        // вправе его перебить, литерал автора — сюжетный и неприкосновенен,
        // неразрешённая ось выпадает) живёт в одном месте и там же проверяется.
        private Dictionary<string, string> AxesOf(JObject cmd)
        {
            var vars = _player?.Vars;
            return LvnCostumer.Look(AxesFrom(cmd), (string)cmd["id"],
                vars != null ? (Func<string, string>)(v => TextInterpolation.Apply(v, vars)) : null);
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
