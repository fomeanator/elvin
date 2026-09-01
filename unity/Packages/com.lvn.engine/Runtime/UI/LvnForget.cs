using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ЗАБВЕНИЕ — единственный список того, что стереть, когда игрок просит
    /// начать заново или удалить аккаунт.
    ///
    /// <para>Личное игрока лежит в ДЕСЯТИ хранилищах: слоты сохранений и их
    /// миниатюры-файлы, открытые кадры галереи, прочитанные реплики, надетое в
    /// гардеробе, переменные новеллы и их база синхронизации, кросс-новелльные
    /// статы, прогресс, имя, флаги пройденного вступления. Стирали же его двумя
    /// перечислениями в вызывающем коде — «начать заново» и «удалить
    /// аккаунт», — и каждое знало ЧАСТЬ.</para>
    ///
    /// <para>Что из этого выходило: <c>LvnGalleryStore.Clear</c> и
    /// <c>LvnReadStore.Clear</c> были написаны и не позваны ни разу — открытые
    /// кадры и прочитанные реплики переживали и «начать заново», и удаление
    /// аккаунта. <c>LvnWardrobe.Clear</c> прямо помечен «tests / profile reset»
    /// — и тоже не позван. Миниатюры сейвов оставались PNG-файлами на диске
    /// после сноса самих слотов. А кросс-новелльные статы, которые при рестарте
    /// ОДНОЙ новеллы сохраняются намеренно (они принадлежат игроку, а не
    /// экспедиции), при удалении аккаунта обязаны уходить — и не уходили.</para>
    ///
    /// <para>Дело не в забывчивости автора: пока список живёт в вызывающем
    /// коде, следующее хранилище в него не попадёт — и промах будет ровно таким
    /// же незаметным. Игрок увидит его в единственном месте, где это больно: он
    /// попросил себя забыть, а игра помнит.</para>
    ///
    /// <para>Обрядов два, и разница между ними — смысловая, а не техническая:
    /// <see cref="Title"/> стирает ОДНУ экспедицию (её сейвы, кадры,
    /// прочитанное, переменные), <see cref="All"/> — игрока целиком, вместе с
    /// тем, что переживает любую отдельную новеллу. Всё, что не является
    /// личным, оба обряда не трогают: кэш докачанного контента, настройки
    /// громкости и языка, выбранный сервер.</para>
    ///
    /// <para>Хранилища верхних сборок (прогресс, серверный сейф прогресса,
    /// прогресс) движку сверху не видны — они объявляют себя сами через
    /// <see cref="Register"/>. Постоянные метки игрока и устройства стирает
    /// <see cref="LvnMark.ForgetAll"/>: список имён держит тот, кто их выдал. За полнотой списка следит страж
    /// <c>TestPersonalDataIsForgettable</c>: файл, который пишет личное в
    /// записную книжку, обязан быть назван здесь.</para>
    /// </summary>
    public static class LvnForget
    {
        private readonly struct Keeper
        {
            public readonly string What;
            public readonly Action<string> Title;
            public readonly Action All;
            public Keeper(string what, Action<string> title, Action all)
            { What = what; Title = title; All = all; }
        }

        private static readonly List<Keeper> _registered = new List<Keeper>();

        /// <summary>Объявить хранилище, которое движок сам не видит. Повторная
        /// регистрация того же имени заменяет прежнюю — подъём приложения не
        /// обязан начинаться с чистой статики.</summary>
        /// <param name="what">имя для журнала — по нему потом читают, что стёрли</param>
        /// <param name="forgetTitle">забыть одну новеллу; <c>null</c>, если
        /// хранилище не делится по новеллам</param>
        /// <param name="forgetAll">забыть всё; <c>null</c>, если хранилище
        /// целиком складывается из новелльных частей</param>
        public static void Register(string what, Action<string> forgetTitle, Action forgetAll)
        {
            if (string.IsNullOrEmpty(what)) return;
            _registered.RemoveAll(k => k.What == what);
            _registered.Add(new Keeper(what, forgetTitle, forgetAll));
        }

        /// <summary>Забыть всё об одной новелле: «начать заново». Статы игрока
        /// (<c>global.*</c>) остаются — они принадлежат игроку, а не
        /// экспедиции.</summary>
        public static void Title(string titleId)
        {
            if (string.IsNullOrEmpty(titleId)) return;
            using (LvnKeep.Batch())
            {
                Safe("сейвы", () => LvnSaveStore.DeleteAll(titleId));
                Safe("галерея", () => LvnGalleryStore.Clear(titleId));
                Safe("прочитанное", () => LvnReadStore.Clear(titleId));
                Safe("переменные", () => Lvn.Content.LocalStateStore.Forget(titleId));
                foreach (var k in _registered)
                    if (k.Title != null) Safe(k.What, () => k.Title(titleId));
            }
            LvnLog.Info($"[lvn-forget] новелла «{titleId}» забыта");
        }

        /// <summary>Забыть игрока целиком: удаление аккаунта. Названные новеллы
        /// стираются по одной, затем уходит то, что их переживает.</summary>
        /// <param name="titleIds">все новеллы каталога — хранилища по новеллам
        /// адресуются по ключу и списка своих ключей не ведут</param>
        /// <param name="entities">кого игрок мог одевать: гардероб живёт по
        /// персонажу, а не по новелле</param>
        public static void All(IEnumerable<string> titleIds, IEnumerable<string> entities = null)
        {
            if (titleIds != null)
                foreach (var id in titleIds) Title(id);

            using (LvnKeep.Batch())
            {
                if (entities != null)
                    foreach (var e in entities)
                        Safe("гардероб", () => LvnWardrobe.Clear(e));

                // Кросс-новелльные статы — единственное, что переживает
                // «начать заново» намеренно и обязано уйти здесь.
                Safe("статы игрока", () =>
                    Lvn.Content.LocalStateStore.Forget(Lvn.Content.LvnGlobalStats.ScopeId));

                // Постоянные метки — у паспортиста (LvnMark): он их выдал, он
                // и знает всех, включая те, что заведут после этой строки.
                Safe("метки", LvnMark.ForgetAll);

                Safe("последний кадр", VnStage.ForgetLastSceneBg);
                Safe("имя", () => LvnPlayerName.Set(""));
                Safe("флаги вступления", () =>
                {
                    LvnPrefs.IntroDone = false;
                    LvnPrefs.SeenWelcome = false;
                });

                foreach (var k in _registered)
                    if (k.All != null) Safe(k.What, k.All);
            }
            LvnLog.Info("[lvn-forget] игрок забыт — личные данные на устройстве стёрты");
        }

        // Одно упавшее хранилище не имеет права остановить забвение: игрок
        // попросил стереть себя, и «стёрли половину, потом упали» — худший
        // из возможных исходов. Промах слышен в журнале поимённо.
        private static void Safe(string what, Action act)
        {
            try { act(); }
            catch (Exception e) { Debug.LogWarning($"[lvn-forget] {what}: {e.Message}"); }
        }
    }
}
