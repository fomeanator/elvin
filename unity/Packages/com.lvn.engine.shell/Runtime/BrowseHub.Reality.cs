using System.Collections.Generic;
using Lvn.Content;

namespace Lvn.UI.Screens
{
    /// <summary>Состояние новеллы для списков: закрыта замком, пройдена или
    /// ждёт игрока (в сюжете реальности — «новое сообщение»).</summary>
    public enum LvnTitleMark { Locked, Done, New }

    /// <summary>
    /// СЮЖЕТ РЕАЛЬНОСТИ — что витрина знает о нём для комнаты сообщений.
    ///
    /// <para>Панель «Сюжет реальности» на главной вела в библиотеку — «пока
    /// у сообщений нет своего экрана». Экран появился (макет 17.09): список
    /// сообщений с состоянием «прочитано / новое / заблокировано». Сообщения —
    /// это новеллы подборки типа <c>reality</c> (тот же тип, что у
    /// <see cref="LvnTitle.type"/>): замок — их <c>unlock</c>, «прочитано» —
    /// пройденная новелла, остальное — новое. Второй сущности «сообщение» в
    /// манифесте не заводится: авторы ведут сюжет реальности как новеллы.</para>
    ///
    /// <para>Замки, ход и открытие детали остаются у хаба: комната сообщений
    /// спрашивает состояние и просит открыть, а не считает сама — иначе замок
    /// в списке и замок в комнате разошлись бы.</para>
    /// </summary>
    public sealed partial class BrowseHub
    {
        /// <summary>Тип подборки и новелл сюжета реальности.</summary>
        public const string RealityType = "reality";

        /// <summary>Дверь в комнату сообщений; ставит оболочка. Пусто (или
        /// сообщений нет) — панель ведёт в библиотеку, как раньше.</summary>
        public System.Action OpenNews;

        /// <summary>Что с новеллой: замок хаба, пройдена, иначе — ждёт.</summary>
        public LvnTitleMark MarkOf(LvnTitle t)
        {
            if (t == null || IsLocked(t)) return LvnTitleMark.Locked;
            return LvnProgress.Finished(t) ? LvnTitleMark.Done : LvnTitleMark.New;
        }

        /// <summary>Сообщения сюжета реальности по порядку подборки типа
        /// <c>reality</c>; без такой подборки — новеллы с этим типом.</summary>
        public IReadOnlyList<LvnTitle> RealityTitles
        {
            get
            {
                var list = new List<LvnTitle>();
                foreach (var c in _collections)
                {
                    if (c == null || c.type != RealityType || c.titles == null) continue;
                    foreach (var id in c.titles)
                        if (id != null && _titles.TryGetValue(id, out var t) && t != null) list.Add(t);
                }
                if (list.Count > 0) return list;
                foreach (var kv in _titles)
                    if (kv.Value != null && kv.Value.type == RealityType) list.Add(kv.Value);
                return list;
            }
        }

        /// <summary>Сколько сообщений ждут: не пройдены и не под замком.</summary>
        public int NewsCount
        {
            get
            {
                int n = 0;
                foreach (var t in RealityTitles) if (MarkOf(t) == LvnTitleMark.New) n++;
                return n;
            }
        }

        /// <summary>«Мир экспедиции: 5» — порядковый номер новеллы в её подборке
        /// (без подборки — в каталоге). 0 — новелла витрине неизвестна.</summary>
        public int WorldNumberOf(LvnTitle t)
        {
            if (t == null) return 0;
            var c = CurrentCollectionOf(t);
            if (c?.titles != null)
            {
                int i = c.titles.IndexOf(t.id);
                if (i >= 0) return i + 1;
            }
            int k = 0;
            foreach (var kv in _titles)
            {
                k++;
                if (kv.Key == t.id) return k;
            }
            return 0;
        }

        /// <summary>Открыть новеллу из чужого списка так же, как с карточки:
        /// замок объясняется подсказкой, остальное идёт в деталь.</summary>
        public void OpenTitle(LvnTitle t)
        {
            if (t == null) return;
            if (IsLocked(t))
            {
                FireLockedHint(LvnWords.Name("title", t.id, t.name), t.locked_hint ?? "");
                return;
            }
            OpenDetail(t, CurrentCollectionOf(t));
        }

        /// <summary>Панель «Сюжет реальности»: в комнату сообщений, если она
        /// есть и есть что показать; иначе — в библиотеку.</summary>
        private void OpenNewsOrLibrary()
        {
            if (OpenNews != null && RealityTitles.Count > 0) OpenNews();
            else ShowLibrary();
        }

        /// <summary>Строка панели: «Новых сообщений: 3» или «Нет новых сообщений».</summary>
        private string NewsLine()
        {
            int n = NewsCount;
            return n > 0
                ? LvnWords.Of("hub.news_count", "{0} new messages", n)
                : LvnWords.Pick("hub.news_empty", _cfg.news_empty_text, "No new messages");
        }
    }
}
