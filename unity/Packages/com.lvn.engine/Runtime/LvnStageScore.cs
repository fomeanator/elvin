using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// ПАРТИТУРА СЦЕНЫ — слои владения кадром и то, что из них складывается.
    ///
    /// <para>Кадром распоряжаются пятеро: история, катсцена, гардероб, витрина
    /// меню и стражи. Пока они правили ОДИН общий кадр по очереди, каждый
    /// следующий стирал следы предыдущего, и «вернуть как было» приходилось
    /// изобретать заново на каждом переходе: запомнить перед, вернуть после, не
    /// забыть грим, не забыть порядок слоя, не забыть того, кто остался. Список
    /// исключений рос, и каждый раз в нём чего-то не хватало.</para>
    ///
    /// <para>Здесь у каждого СВОЙ слой. История строит свой кадр и никогда его
    /// не теряет — поверх лишь накладываются чужие. Катсцена не «уводит и
    /// возвращает», она открывает наложение и закрывает его; кадр истории
    /// проступает сам, потому что его никто не стирал.</para>
    ///
    /// <para>Итог складывается по старшинству (то же, что у Помрежа): верхний
    /// слой перекрывает нижние по каждому человеку отдельно, а слой, объявивший
    /// <see cref="LvnFrame.Exclusive"/>, прячет всех, кого сам не показывает, —
    /// «в кадре только мои». Это ровно то, чего просит катсцена, и ровно то,
    /// что раньше делалось руками через увод каждого.</para>
    /// </summary>
    public sealed class LvnStageScore
    {
        private readonly Dictionary<LvnSender, LvnFrame> _layers = new Dictionary<LvnSender, LvnFrame>();

        /// <summary>
        /// ЕСТЬ ЛИ У СЛОЯ ХОТЬ КТО-ТО В КАДРЕ.
        ///
        /// <para>Пустой слой истории значит «глава только начинается»; непустой —
        /// «кадр уже собран», так бывает после реплея с сохранения. Разницу
        /// спрашивают те, кто кладёт катсцену поверх: прятать и возвращать
        /// готовый кадр — это переставлять то, что и так стоит правильно.</para>
        /// </summary>
        public bool Dressed(LvnSender who)
        {
            foreach (var _ in Layer(who).Visible()) return true;
            return false;
        }

        /// <summary>Слой отправителя; создаётся при первом обращении. История
        /// пишет сюда каждой своей командой, остальные — на время своей
        /// работы.</summary>
        public LvnFrame Layer(LvnSender sender)
        {
            if (!_layers.TryGetValue(sender, out var f)) _layers[sender] = f = new LvnFrame();
            return f;
        }

        /// <summary>Есть ли у отправителя наложение прямо сейчас.</summary>
        public bool HasLayer(LvnSender sender)
            => _layers.TryGetValue(sender, out var f) && !f.IsEmpty;

        /// <summary>ЗАКРЫТЬ НАЛОЖЕНИЕ. Кадр под ним цел — он и проступит.
        /// Именно это заменяет «вернуть всех, кого уводили».</summary>
        public void Close(LvnSender sender) => _layers.Remove(sender);

        /// <summary>Снести всё: сцена уходит целиком (смена главы).</summary>
        public void Clear() => _layers.Clear();

        /// <summary>Порядок сложения — снизу вверх. История в самом низу:
        /// она держит кадр, всё прочее ложится поверх на время.</summary>
        private static readonly LvnSender[] Order =
        {
            LvnSender.Guard, LvnSender.Story, LvnSender.Replay,
            LvnSender.Menu, LvnSender.Wardrobe, LvnSender.Cutscene,
        };

        /// <summary>
        /// СЛОЖИТЬ ИТОГОВЫЙ КАДР — то, что должно быть на экране.
        ///
        /// <para>Верхний слой перекрывает нижние ПО КАЖДОМУ ЧЕЛОВЕКУ отдельно:
        /// катсцена, поставившая героиню, не отменяет остальных — она отменяет
        /// только прежнюю героиню. А слой, объявивший «в кадре только мои»,
        /// прячет всех чужих, но НЕ СТИРАЕТ их: закроется наложение — и они
        /// вернутся сами, со своими позами и гримом.</para>
        /// </summary>
        public LvnFrame Compose()
        {
            var outFrame = new LvnFrame();
            LvnFrame exclusive = null;

            foreach (var sender in Order)
            {
                if (!_layers.TryGetValue(sender, out var layer) || layer == null) continue;
                if (layer.Exclusive) exclusive = layer;

                foreach (var kv in layer.Actors)
                {
                    outFrame.Actors.TryGetValue(kv.Key, out var have);
                    var next = kv.Value;
                    // Слой мог сказать не всё: поставить человека, не тронув
                    // грим, или наложить грим, не трогая позы. Недосказанное
                    // берётся снизу — иначе наложение молча стирало бы то, о
                    // чём вообще не говорило.
                    if (next.Pose == null) next.Pose = have.Pose;
                    if (next.Fx == null) next.Fx = have.Fx;
                    outFrame.Actors[kv.Key] = next;
                }
                if (layer.Background != null) outFrame.Background = layer.Background;
                if (layer.Veil != null) outFrame.Veil = layer.Veil;
            }

            if (exclusive != null)
            {
                var ids = new List<string>(outFrame.Actors.Keys);
                foreach (var id in ids)
                {
                    if (exclusive.Actors.ContainsKey(id)) continue;
                    var a = outFrame.Actors[id];
                    a.Visible = false;      // спрятан наложением, а НЕ забыт
                    outFrame.Actors[id] = a;
                }
            }
            return outFrame;
        }

        /// <summary>
        /// ЧТО НАДО СДЕЛАТЬ С ЭКРАНОМ, чтобы он совпал с партитурой.
        ///
        /// <para>Разница, а не пересборка: сцена, перестроенная целиком на
        /// каждое изменение, теряет начатые переходы, сбрасывает анимации и
        /// перезагружает арт — это и была цена императивного пути. Здесь
        /// возвращается только то, что ДЕЙСТВИТЕЛЬНО разошлось.</para>
        /// </summary>
        public struct Change
        {
            public string Id;
            public JObject Pose;    // чем поставить (null — только скрыть)
            public JObject Fx;      // и каким гримом покрыть
            public bool Show;
        }

        public List<Change> DiffAgainst(LvnFrame onScreen)
        {
            var want = Compose();
            var changes = new List<Change>();

            foreach (var kv in want.Actors)
            {
                var w = kv.Value;
                onScreen.Actors.TryGetValue(kv.Key, out var have);
                bool sameVisible = have.Visible == w.Visible;
                bool samePose = JToken.DeepEquals(have.Pose, w.Pose);
                bool sameFx = JToken.DeepEquals(have.Fx, w.Fx);
                if (sameVisible && samePose && sameFx) continue;

                changes.Add(new Change
                {
                    Id = kv.Key,
                    Pose = w.Visible ? w.Pose : null,
                    Fx = w.Visible ? w.Fx : null,
                    Show = w.Visible,
                });
            }

            // Кто есть на экране, но кого партитура не знает вовсе — уходит.
            foreach (var kv in onScreen.Actors)
            {
                if (!kv.Value.Visible || want.Actors.ContainsKey(kv.Key)) continue;
                changes.Add(new Change { Id = kv.Key, Show = false });
            }
            return changes;
        }
    }
}
