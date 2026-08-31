using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ЯВОЧНЫЙ ЛИСТ СЦЕНЫ — кто на ней есть, кого она помнит и чем его вернуть.
    ///
    /// <para>Показ человека и знание о людях — разные работы, и держать их в
    /// одном файле значило искать ответ на «кто сейчас в кадре» посреди
    /// загрузки слоёв. Здесь только вопросы и короткие распоряжения: кто стоит,
    /// кто летит в кадр, цела ли фигура, помнит ли сцена, чем её пересобрать,
    /// уйти, вернуться на прежнее место, забыть вовсе.</para>
    ///
    /// <para>Различения здесь не косметические, каждое выведено дефектом:
    /// «стоит» и «стоит ИЛИ уже летит» (расталкивание пропускало летящего);
    /// «сцена его помнит» и «он в кадре» (манекен гардероба помнился, но кадру
    /// не принадлежал); «уйти» и «уйти на время» (примерка прячет и возвращает,
    /// а не выводит из главы).</para>
    /// </summary>
    public sealed partial class VnStage
    {
        /// <summary>Re-apply an on-screen actor from its last command (art
        /// re-resolves against the current variables + wardrobe). No-op when
        /// the actor isn't on stage.</summary>
        public void RefreshActor(string id)
        {
            if (string.IsNullOrEmpty(id) || !_memory.TryCommand(id, out var cmd)) return;
            if (!BoolOr(cmd["show"], true)) return; // скрытого не воскрешать
            LvnAsync.Fire(ApplyActorAsync(cmd), "ApplyActor");
        }

        /// <summary>Актёр виден ИЛИ его показ уже в полёте (слои грузятся).
        /// Хосту меню это отличает «кукла есть/едет» от «пропала — переслать».</summary>
        public bool ActorVisibleOrPending(string id)
            => !string.IsNullOrEmpty(id)
               && ((_memory.TryWhere(id, out var p) && p.Show)
                   || (_memory.TryTarget(id, out var t) && t.Show));

        /// <summary>
        /// ЗАБЫТЬ НАДЕТОЕ. Правило «тот же облик — не пересобирать» сравнивает
        /// СПИСОК СЛОЁВ, а живое обновление контента меняет не список, а сами
        /// файлы под теми же именами. Без этого сброса реплей после обновления
        /// показал бы прежний арт «как есть» — то есть не показал бы правку.
        /// </summary>
        public void ForgetLooks() => _memory.ForgetLooks();

        /// <summary>ФИГУРА ЦЕЛА: слои на месте, и каждому есть чем рисовать.
        /// Такую показывают как есть — включением, а не сборкой.</summary>
        public bool ActorArtAlive(string id)
            => !string.IsNullOrEmpty(id)
               && _renderer is CanvasSceneRenderer csr && csr.ActorArtAlive(id);

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
            if (_memory.TryWhere(id, out var pl) && pl.Show) return;
            JObject cmd;
            if (_memory.TryCommand(id, out var last) && (string)last["op"] == "actor")
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
            _memory.Forget(id);
        }

        /// <summary>Стояла ли эта роль на сцене по СЦЕНАРИЮ — то есть помнит ли
        /// сцена её команду. Хост спрашивает перед примеркой, чтобы понимать,
        /// свой это актёр или приведённый гардеробом манекен; переход в меню
        /// спрашивает то же самое другими словами — «известен ли облик»:
        /// команда и есть весь облик, и второго имени этому вопросу не
        /// полагается.</summary>
        public bool RememberedByScript(string id)
            => _memory.Knows(id);

        /// <summary>Ids of actors currently VISIBLE on stage — hosts use it to
        /// pick who an always-open wardrobe should dress.</summary>
        public List<string> ActorsOnStage()
        {
            var list = new List<string>();
            foreach (var kv in _memory.Wheres())
                if (kv.Value.Show) list.Add(kv.Key);
            return list;
        }

        /// <summary>
        /// КТО В КАДРЕ ИЛИ УЖЕ ЛЕТИТ В НЕГО — видимые плюс те, чей показ ещё
        /// грузится.
        ///
        /// <para><see cref="ActorsOnStage"/> отвечает только про УЖЕ
        /// применённые размещения, и для гардероба этого хватает. Но тот, кому
        /// нужно расчистить кадр, спрашивает о другом: кого зритель увидит
        /// через миг. Показ актёра — асинхронный, между командой и картинкой
        /// лежит загрузка слоёв, и в этом промежутке он не «на сцене».
        /// Расталкивание по <see cref="ActorsOnStage"/> его пропускало — и он
        /// всплывал уже посреди катсцены, рядом с героиней. Ровно это
        /// выглядело как «героиню рисуют на нём».</para>
        /// </summary>
        public List<string> ActorsInFrame()
        {
            var list = new List<string>();
            foreach (var kv in _memory.Wheres())
                if (kv.Value.Show) list.Add(kv.Key);
            foreach (var kv in _memory.Targets())
                if (kv.Value.Show && !list.Contains(kv.Key)) list.Add(kv.Key);
            return list;
        }

        /// <summary>
        /// ПЕРЕСТАВИТЬ, НЕ ПЕРЕОДЕВАЯ.
        ///
        /// <para>Героиня в меню и героиня в главе — ОДНА И ТА ЖЕ: она уходит на
        /// миссию и возвращается. Значит наряд и эмоция, с которыми кончилась
        /// глава, обязаны пережить переход — их нельзя пересобирать из
        /// умолчаний. Меню знает только МЕСТО (центр, рост куклы), а во что она
        /// одета — знает последняя команда сцены.</para>
        ///
        /// <para>Поэтому здесь берётся последняя авторская команда актёра и
        /// накрываются только поля размещения. Оси — наряд, эмоция, поза — не
        /// трогаются вовсе. Если актёра на сцене не было, ставить нечего:
        /// звонящий отправит обычный показ.</para>
        /// </summary>
        public bool Restage(string id, JObject placement, LvnSender sender = LvnSender.Story)
        {
            if (string.IsNullOrEmpty(id) || !_memory.TryCommand(id, out var last)) return false;
            var cmd = (JObject)last.DeepClone();
            if (placement != null)
                foreach (var prop in placement.Properties())
                    cmd[prop.Name] = prop.Value.DeepClone();
            cmd["id"] = id;
            cmd["show"] = true;
            LvnLog.Trace($"[lvn-actor] {id}: перестановка без переодевания → "
                       + $"{string.Join(", ", System.Linq.Enumerable.Select(AxesFrom(cmd), kv => kv.Key + "=" + kv.Value))}");
            ApplyStage(cmd, sender);
            return true;
        }

        /// <summary>Увести актёра со сцены — обратное
        /// <see cref="EnsureActorShown"/>. <paramref name="sender"/> — кто
        /// уводит: история доигралась, катсцена расчищает кадр, гардероб убрал
        /// манекен. Раньше эти трое ходили в движок МИМО команд, и Помреж об их
        /// работе не знал вовсе.</summary>
        public void HideActor(string id, LvnSender sender = LvnSender.Story)
        {
            if (string.IsNullOrEmpty(id)) return;
            ApplyStage(HideCommandFor(id), sender);
        }

        /// <summary>
        /// КОМАНДА «УЙТИ» ДЛЯ ЭТОЙ ФИГУРЫ.
        ///
        /// <para>Исполнителя выбирает ПАМЯТЬ: тот, кто вышел предметом,
        /// уводится предметом — иначе команда уедет не тому, и уход не
        /// сыграет.</para>
        ///
        /// <para>Без <c>exit=</c>: уход возьмётся из темы (drift/fade — что
        /// выбрала новелла). Жёсткий «fade» затирал бы умолчание
        /// постановки.</para>
        ///
        /// <para>Выбор исполнителя был записан дважды — здесь и там, где
        /// катсцена убирает фигуру С ЭКРАНА, не трогая партитуру. Второй раз он
        /// сопровождался комментарием «тот же выбор, что и у обычного увода»:
        /// правило знали оба, а дома у него не было.</para>
        /// </summary>
        private JObject HideCommandFor(string id)
        {
            string op = _memory.TryCommand(id, out var staged)
                        && string.Equals((string)staged["op"], "obj", StringComparison.OrdinalIgnoreCase)
                ? "obj" : "actor";
            return new JObject { ["op"] = op, ["id"] = id, ["show"] = false };
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

        private void HideActorTemporarily(string id, LvnSender sender = LvnSender.Wardrobe)
        {
            JObject replay = _memory.TryCommand(id, out var current)
                ? (JObject)current.DeepClone() : null;
            HideActor(id, sender);
            if (replay != null) _memory.RememberCommandOnly(id, replay);
        }


        /// <summary>The `clear` op: take every actor and obj off stage in one
        /// command, leaving the backdrop, effects and HUD exactly as they are.
        ///
        /// <para>Each one goes through the ORDINARY hide, so nothing here needs
        /// to know how hiding works: placement stays remembered (a later
        /// `actor id=…` with no position returns her to the slot she left),
        /// hotspots and draggables are dropped, and the exit is the same fade a
        /// hand-written `show=false` would have played. The list is snapshotted
        /// first — <see cref="ActorsInFrame"/> builds a new list — because the
        /// hides mutate the placement map as they run.</para>
        ///
        /// <para>Список берётся ПО КАДРУ, а не по видимым: `clear` сразу после
        /// показа обязан убрать и того, чьи слои ещё грузятся, — иначе он
        /// проявляется на уже убранной сцене.</para></summary>
        private void ApplyClear(LvnSender sender = LvnSender.Story)
        {
            foreach (var id in ActorsInFrame()) HideActor(id, sender);
        }
    }
}
