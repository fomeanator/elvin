using System;
using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// РЕЖИССЁР ЭКРАНА — кто сейчас на экране, кто из слоёв главный и что
    /// значит «назад».
    ///
    /// <para>Работу эту делали восемь мест сразу, и каждое по-своему. «Убрать
    /// интерфейс» просили трое независимо — катсцена, режим «во весь рост» и
    /// долгое нажатие, — а держал их один БУЛЕВ ФЛАГ. Отпустил палец посреди
    /// катсцены, и хром вернулся: причина ушла одна, а флаг общий. «Идёт ли
    /// глава» знали семь файлов, каждый своей копией. Ввод гасили три разных
    /// сторожа.</para>
    ///
    /// <para>Отсюда правило: интерфейс скрыт, пока держит хоть ОДНА причина.
    /// Не флаг, а список: кто попросил — тот и снимает, а чужую просьбу
    /// отменить нельзя. То же и с поверхностями: экран знает, кто на нём
    /// стоит, и «назад» всегда закрывает верхнего.</para>
    /// </summary>
    public sealed class LvnScreenDirector
    {
        /// <summary>Один режиссёр на приложение: сцена, оболочка и строка
        /// состояния обязаны видеть одну и ту же картину экрана, а не каждый
        /// свою копию.</summary>
        public static LvnScreenDirector Current { get; } = new LvnScreenDirector();

        /// <summary>Что-то на экране изменилось: режим, скрытие интерфейса или
        /// набор поверхностей. Подписчики перерисовывают себя.</summary>
        public event Action Changed;

        private void Note() => Changed?.Invoke();

        // ── режим: что ведёт экран ────────────────────────────────────────────

        /// <summary>Играет глава (сцена ведёт) или игрок в меню.</summary>
        public bool InChapter { get; private set; }

        public void EnterChapter()
        {
            if (InChapter) return;
            InChapter = true;
            Note();
        }

        public void LeaveChapter()
        {
            if (!InChapter) return;
            InChapter = false;
            Note();
        }

        // ── скрытие интерфейса: по причинам, а не флагом ──────────────────────

        private readonly HashSet<string> _hidden = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Интерфейс скрыт, пока держит хоть одна причина.</summary>
        public bool ChromeHidden => _hidden.Count > 0;

        /// <summary>Держит ли интерфейс именно эта причина.</summary>
        public bool HiddenBecause(string reason) => _hidden.Contains(reason ?? "");

        /// <summary>Попросить убрать интерфейс. Повторная просьба той же
        /// причиной ничего не меняет — причина одна, сколько бы раз о ней ни
        /// сказали.</summary>
        public void HideChrome(string reason)
        {
            if (string.IsNullOrEmpty(reason) || !_hidden.Add(reason)) return;
            if (_hidden.Count == 1) Note();   // экран только что закрылся
        }

        /// <summary>Своя причина отпала. Чужие остаются: катсцена не кончается
        /// оттого, что игрок отпустил палец.</summary>
        public void ShowChrome(string reason)
        {
            if (string.IsNullOrEmpty(reason) || !_hidden.Remove(reason)) return;
            if (_hidden.Count == 0) Note();   // держать больше некому
        }

        /// <summary>Снять ВСЕ причины разом — сброс сцены: скрытый интерфейс не
        /// имеет права пережить главу, в которой его спрятали.</summary>
        public void ShowChromeAll()
        {
            if (_hidden.Count == 0) return;
            _hidden.Clear();
            Note();
        }

        // ── поверхности: кто главный и куда ведёт «назад» ─────────────────────
        // Стопка, а не флаги: из открытой истории «назад» обязан закрыть
        // историю, а не увести на другую вкладку.

        private readonly List<string> _stack = new List<string>();

        /// <summary>Поверхность встала на экран. Повторное открытие поднимает
        /// её наверх, а не дублирует.</summary>
        public void Open(string surface)
        {
            if (string.IsNullOrEmpty(surface)) return;
            _stack.Remove(surface);
            _stack.Add(surface);
            Note();
        }

        /// <summary>Поверхность ушла (не обязательно верхняя: модалку может
        /// закрыть хост, пока над ней стоит попап).</summary>
        public void Close(string surface)
        {
            if (string.IsNullOrEmpty(surface) || !_stack.Remove(surface)) return;
            Note();
        }

        /// <summary>Кто сейчас главный; null — на экране чистая сцена.</summary>
        public string Top => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

        /// <summary>Есть ли хоть одна поверхность поверх сцены.</summary>
        public bool AnyOpen => _stack.Count > 0;

        public bool IsOpen(string surface)
            => !string.IsNullOrEmpty(surface) && _stack.Contains(surface);

        /// <summary>Что закрывает «назад»: верхняя поверхность, а если экран
        /// чист — null (сцена сама решает, что значит назад в главе).</summary>
        public string BackTarget => Top;

        /// <summary>Забыть всё: смена главы, пересборка панели. Скрытый
        /// интерфейс и открытые поверхности прошлой сцены не переносятся.</summary>
        public void Reset()
        {
            bool had = _hidden.Count > 0 || _stack.Count > 0;
            _hidden.Clear();
            _stack.Clear();
            if (had) Note();
        }

        // ── имена причин и поверхностей ───────────────────────────────────────
        // Здесь же, по той же причине, что у дорожек Хронометриста: «peek» и
        // «panel-peek» в двух файлах — это молчаливая ошибка, которую видно
        // только по зависшему интерфейсу.

        /// <summary>Катсцена: кадр без интерфейса по команде сценария.</summary>
        public const string CutsceneReason = "cutscene";

        /// <summary>«Во весь рост»: примерка продолжается, панель убрана.</summary>
        public const string PeekReason = "peek";

        /// <summary>Долгое нажатие: игрок разглядывает арт, пока держит палец.</summary>
        public const string ArtViewReason = "art-view";

        /// <summary>Нижний лист истории (гардероб и всё, что живёт в общей раме).</summary>
        public const string StoryPanel = "story-panel";

        /// <summary>Квик-меню сцены.</summary>
        public const string QuickMenu = "quick-menu";

        /// <summary>Модальный экран оболочки (магазин, настройки, попап).</summary>
        public const string ShellModal = "shell-modal";
    }
}
