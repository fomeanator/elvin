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
    /// ГАРДЕРОБ НА СЦЕНЕ — часть <see cref="VnStage"/>: как примерка доезжает
    /// до живого актёра.
    ///
    /// <para>Реплей последней команды с новыми осями, схлопывание нескольких
    /// примерок в один кадр (иначе каждая рвала загрузку предыдущей), фокус на
    /// одной героине, пока открыт лист.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        // Реплеи гардероба СХЛОПЫВАЮТСЯ до одного на кадр: открытие листа
        // превьюит несколько осей подряд, и каждый реплей перезапускал полную
        // загрузку слоёв — на сети это серия оборванных показов («очень часто
        // пропадает», живой репорт 27.08).
        private readonly HashSet<string> _wardrobeRefreshPending = new HashSet<string>();

        private void RefreshWardrobeActor(string id, string wardrobeAxis)
        {
            if (string.IsNullOrEmpty(id) || !_wardrobeRefreshPending.Add(id)) return;
            LvnAsync.Fire(RefreshWardrobeActorSoonAsync(id), "WardrobeActor");
        }

        private async Task RefreshWardrobeActorSoonAsync(string id)
        {
            await Task.Yield(); // каскад Preview этого кадра схлопнулся в один реплей
            _wardrobeRefreshPending.Remove(id);
            if (!_actorCmds.TryGetValue(id, out var cmd)) return;
            // Реплей НИКОГДА не воспроизводит hide: смена наряда скрытого
            // актёра доедет с его следующим показом, а реплей hide с новым gen
            // убивал летящий показ соседней команды.
            if (!BoolOr(cmd["show"], true)) return;
            var axis = LvnWardrobe.LastChangedAxis(id);
            // ЭМОЦИЯ — НЕ НАРЯД (Илья 27.08: «плавно одно в другое, будто
            // живые»): смена лица идёт обычным кроссфейдом облика, как
            // сценарное emotion=, а не гардеробным свопом.
            bool emotion = IsEmotionAxis(axis);
            await ApplyActorAsync(cmd, wardrobeSwap: !emotion,
                wardrobeFromTop: !emotion && LvnWardrobeStage.IsHair(axis));
        }

        private static bool IsEmotionAxis(string axis)
        {
            var key = (axis ?? "").ToLowerInvariant();
            return key.Contains("emo") || key.Contains("эмо") || key == "mood" || key == "face";
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
            int gen = _clock.Claim(LvnStageClock.WardrobeFocusLane);
            // ПРЕДМЕТ ЗАНЯТ ГАРДЕРОБОМ. Пока игрок листает наряды, героиня
            // принадлежит ему: команда истории или витрины к ней получит отказ,
            // а не перебьёт примерку на полпути.
            Commands.Hold("actor:" + keepId, LvnSender.Wardrobe);
            HideEveryoneExcept(keepId);
            await WaitForActorExitsAsync(epoch);
            // Нас перебили — показывает он. Поколение спрашиваем у Хронометриста:
            // «чья работа устарела» — его вопрос, и он же отвечает на него для
            // фона, актёров и ожиданий.
            if (!_clock.MayTouch(epoch, LvnStageClock.WardrobeFocusLane, gen)) return;
            if (!StageCurrent(epoch) || (canShow != null && !canShow())) return;
            // Пока ждали, кто-то мог успеть показаться: пересобрать и убрать.
            HideEveryoneExcept(keepId);
            EnsureActorShown(keepId, fadeOnly: true);
        }

        private void HideEveryoneExcept(string keepId)
        {
            // В КАДРЕ ИЛИ В ПОЛЁТЕ: тот, чей показ ещё грузился, в списке
            // видимых не значился — и проявлялся уже поверх примеряемой куклы.
            foreach (var id in ActorsInFrame())
            {
                if (id == keepId) continue;
                if (_actorCmds.TryGetValue(id, out var cmd) && !IsCharacterCommand(cmd)) continue;
                HideActorTemporarily(id, LvnSender.Wardrobe);
            }
        }

        /// <summary>Лист гардероба закрылся — кукла возвращается истории.</summary>
        public void ReleaseWardrobeFocus() => Commands.ReleaseAll(LvnSender.Wardrobe);

        private void OnWardrobeChanged(string entity)
            => RefreshWardrobeActor(entity, LvnWardrobe.LastChangedAxis(entity));
    }
}
