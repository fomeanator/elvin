using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ПЕРЕДАЧА ПРОХОЖДЕНИЯ (TR-17) — отдать свой слот другому человеку и
    /// принять чужой.
    ///
    /// <para>Идея Ильи: «поделиться сохранением, чтобы другой доиграл». Меню
    /// сохранений живёт в движке и о сервере продукта не знает — оно только
    /// говорит, какой слот показывают; отдаёт снимок и разворачивает чужой
    /// хозяин приложения, то есть мы.</para>
    /// </summary>
    public partial class NovelApp
    {
        /// <summary>
        /// ОТДАТЬ ПРОХОЖДЕНИЕ: снимок уходит на сервер, игрок получает код.
        ///
        /// <para>Делимся ПОСЛЕДНИМ сохранением, а не выбранным из списка: «где
        /// я сейчас» — это то место, о котором игрок пишет другу, и лишний
        /// выбор слота тут стоит дороже, чем помогает. Код показываем словами —
        /// его пересылают в чат и набирают руками.</para>
        /// </summary>
        private async Task ShareLatestAsync()
        {
            var titleId = Stage?.SaveTitleId;
            var slot = LatestSlot(titleId);
            if (slot?.Snap == null)
            {
                await _shell.AlertAsync(LvnWords.Of("share.title", "Share"),
                                        LvnWords.Of("share.empty", "This slot is empty."));
                return;
            }
            var snapshot = JObject.FromObject(slot);
            // ОБРАЗ ЕДЕТ ВМЕСТЕ СО СНИМКОМ (TR-18): получатель должен видеть, во
            // что одета героиня у автора ссылки. Видеть — не значит получить:
            // вещи остаются товаром, их продают, а не выдают.
            var look = LookOf();
            if (look != null) snapshot["Look"] = look;
            var code = await Lvn.Services.LvnShare.GiveAsync(titleId, snapshot, slot.Preview);
            if (string.IsNullOrEmpty(code))
            {
                await _shell.AlertAsync(LvnWords.Of("share.title", "Share"),
                                        LvnWords.Of("share.failed", "Could not share right now."));
                return;
            }
            // Ссылку И код: ссылка открывается нажатием, код переживает
            // пересылку через любое место, где ссылки режут.
            await _shell.AlertAsync(
                LvnWords.Of("share.title", "Share"),
                LvnWords.Of("share.ready", "Send this code to a friend: {0}", code));
        }

        /// <summary>Принять чужое прохождение по коду: снимок кладётся в
        /// отдельный слот, СВОИ сейвы не трогаются — иначе подарок стёр бы
        /// собственную игру.</summary>
        private async Task TakeShareAsync(string code)
        {
            Lvn.Services.LvnShare.Taken taken = await Lvn.Services.LvnShare.TakeAsync(code);
            if (taken == null || !string.IsNullOrEmpty(taken.Error) || taken.Body == null)
            {
                await _shell.AlertAsync(LvnWords.Of("share.title", "Share"),
                                        LvnWords.Of("share.gone", "This link is no longer available."));
                return;
            }
            LvnSaveSlot slot = null;
            try { slot = taken.Body.ToObject<LvnSaveSlot>(); }
            catch { /* чужой снимок из другой версии — ниже скажем словом */ }
            if (slot?.Snap == null)
            {
                await _shell.AlertAsync(LvnWords.Of("share.title", "Share"),
                                        LvnWords.Of("share.unreadable", "This playthrough is from another version."));
                return;
            }
            var titleId = string.IsNullOrEmpty(taken.Title) ? Stage?.SaveTitleId : taken.Title;
            if (!LvnSaveStore.Put(titleId, SharedSlot, slot))
            {
                await _shell.AlertAsync(LvnWords.Of("share.title", "Share"),
                                        LvnWords.Of("share.failed", "Could not share right now."));
                return;
            }
            // ВИТРИНА ПРОХОЖДЕНИЯ (TR-18): показать, во что она одета, и почём
            // это повторить. Сами вещи НЕ выдаются — иначе один купил, десять
            // получили даром; прохождение уже лежит в сохранениях, и продолжить
            // можно в своей одежде.
            var look = taken.Body["Look"] as JObject;
            if (look?["worn"] is JObject worn && worn.HasValues)
            {
                await ShowLookAsync((string)look["entity"], worn, taken.Note);
                return;
            }
            await _shell.AlertAsync(
                LvnWords.Of("share.title", "Share"),
                LvnWords.Of("share.taken", "The playthrough is in your saves — open «Load»."));
        }

        /// <summary>Что надето на героине сейчас — для витрины прохождения
        /// (TR-18). Эмоция в образ не входит: лицо не продаётся.</summary>
        private JObject LookOf()
        {
            var hero = _manifest?.ui?.wardrobe?.entity;
            if (string.IsNullOrEmpty(hero)) return null;
            var worn = new JObject();
            foreach (var kv in Lvn.UI.LvnWardrobe.Equipped(hero))
                if (!Lvn.UI.LvnWardrobeStage.IsEmotion(kv.Key)) worn[kv.Key] = kv.Value;
            if (!worn.HasValues) return null;
            return new JObject { ["entity"] = hero, ["worn"] = worn };
        }

        /// <summary>Слот для принятого прохождения. ОДИН на всё: следующий
        /// подарок заменяет предыдущий, но никогда — свою игру.</summary>
        private const string SharedSlot = "shared";

        /// <summary>Самое свежее сохранение новеллы: автосейв или ручной слот —
        /// что новее. Принятый подарок исключён: делиться чужим прохождением
        /// как своим — не то, чего игрок ждёт от этой кнопки.</summary>
        private static LvnSaveSlot LatestSlot(string titleId)
        {
            LvnSaveSlot best = null;
            foreach (var kv in LvnSaveStore.Slots(titleId))
            {
                if (kv.Key == SharedSlot || kv.Value?.Snap == null) continue;
                if (best == null || kv.Value.SavedAtUnixMs > best.SavedAtUnixMs) best = kv.Value;
            }
            return best;
        }

        /// <summary>Открыть витрину чужого образа (TR-18). Экран живёт ровно на
        /// время разговора: вещи он не выдаёт, только показывает и продаёт.</summary>
        private async Task ShowLookAsync(string entity, JObject worn, string note)
        {
            var root = _shell?.Document?.rootVisualElement;
            if (root == null || _assets == null) return;
            var wardrobe = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var kv in worn) wardrobe[kv.Key] = (string)kv.Value;

            var screen = new ShareLookScreen(_assets);
            screen.SetContent(_manifest);
            root.Add(screen);
            try { await screen.RunAsync(entity, wardrobe, note); }
            finally { screen.RemoveFromHierarchy(); }
        }
    }
}
