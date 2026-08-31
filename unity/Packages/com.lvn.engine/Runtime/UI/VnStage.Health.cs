using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ЗДОРОВЬЕ СЦЕНЫ — Лекарь, сверка кадра с экраном и диагностика
    /// «белого прямоугольника».
    ///
    /// <para>Отдельным домом, потому что это ОДНА тема с тремя лицами и
    /// собственной историей: недуг (полотно опустело, слои умерли), способ его
    /// заметить и способ вылечить. Пока всё это лежало в общем файле сцены
    /// вперемешку с построением панели и шрифтами, каждый новый сторож
    /// дописывался туда же — и на вопрос «что в этой игре чинилось само»
    /// ответить было нечем.</para>
    ///
    /// <para>Границы внутри дома тоже разные: ЛЕЧЕНИЕ возвращает сцене то, что
    /// у неё отобрали (выгруженную текстуру), а СВЕРКА только докладывает —
    /// починка расхождения означала бы, что кадр ведут двое, и мы уже знаем,
    /// чем это кончается.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        private float _nextDriftCheck;

        /// <summary>
        /// ЛЕКАРЬ СЦЕНЫ — один дом для всех самолечений.
        ///
        /// <para>Раньше каждый недуг заводил себе отдельного сторожа со своим
        /// таймером и своим тегом в логе, и на вопрос «что в этой игре чинилось
        /// само» ответить было нечем — а именно он и важен: каждое сработавшее
        /// лечение это след настоящего дефекта, которого игрок не увидел.</para>
        /// </summary>
        public LvnHealer Healer { get; } = new LvnHealer();

        /// <summary>Недуги СЦЕНЫ. Хост добавляет к ним свои (полотно витрины) —
        /// Лекарь один на всю сцену.</summary>
        private void HireHealer()
        {
            // ПОЛОТНО ОПУСТЕЛО. RawImage без текстуры заливает кадр своим
            // цветом (после первой постановки — белым): выгруженная из-под
            // живой картинки текстура превращается в белое пятно во весь экран,
            // а под затемнением катсцены читается как «серый экран» (Илья
            // 26–27.08). Лечится реплеем последней авторской команды — кто бы
            // ни забрал текстуру.
            // ПУСТОТА НЕ РИСУЕТСЯ. Первым делом — не «починить», а ЗАМОЛЧАТЬ:
            // Image без спрайта и RawImage без текстуры заливают свой
            // прямоугольник сплошным цветом (белым в кадре, серым под вуалью
            // перехода — «серый спрайт при выходе из новеллы», Илья 28.08).
            // Пересборка облика займёт кадры, а пятно видно уже сейчас. Это не
            // сокрытие: тот, кому нечем рисовать, ПУСТ, и честно нарисовать
            // пустоту значит не рисовать ничего, — а сама поломка лечится ниже
            // и попадает в журнал.
            Healer.Watch("пустые поверхности",
                () => _renderer is CanvasSceneRenderer c
                      && (c.BackdropBlankWhite || DeadActorsWithMemory() != null),
                () =>
                {
                    if (!(_renderer is CanvasSceneRenderer c)) return;
                    int hushed = c.HushBlankSurfaces();
                    if (hushed > 0)
                        Debug.LogWarning($"[lvn-white] нечем рисовать — погашено поверхностей: {hushed}");
                }, period: 0.2f);

            Healer.Watch("полотно",
                () => _renderer is CanvasSceneRenderer c && c.BackdropBlankWhite && _lastBgCmd != null,
                () =>
                {
                    var again = (JObject)_lastBgCmd.DeepClone();
                    _lastBgCmd = null;          // иначе повтор сочтут no-op
                    Debug.LogWarning("[lvn-bg] полотно опустело под живым кадром — ставим фон заново");
                    ApplyStage(again, LvnSender.Guard);
                }, period: 0.5f);

            // СЛОИ ФИГУРЫ УМЕРЛИ. Спрайт может умереть уже ПОСЛЕ того, как его
            // поставили: выгрузка забирает текстуру из-под живого актёра, и
            // Image без спрайта заливает свой прямоугольник сплошным цветом —
            // «после выхода из главы героиня пропадает, остаётся белый
            // прямоугольник» (Илья 26.08). Раньше это чинил только игрок,
            // руками перевыбрав вещь в гардеробе.
            Healer.Watch("фигуры",
                () => DeadActorsWithMemory() != null,
                () =>
                {
                    var dead = DeadActorsWithMemory();
                    if (dead == null) return;
                    foreach (var id in dead)
                    {
                        Debug.LogWarning($"[lvn-actor] {id}: слои остались без спрайтов "
                                         + "(их выгрузили из-под живой куклы) — пересобираем облик");
                        // Лечение обязано быть НАСТОЯЩЕЙ пересборкой: правило
                        // «тот же облик — показать как есть» здесь и так не
                        // сработает (фигура не цела), но полагаться на это не
                        // станем — забываем надетое явно.
                        _memory.DropLook(id);
                        RefreshWardrobeActor(id, null);
                    }
                }, period: 0.5f);
        }

        /// <summary>Кто рисует сплошные прямоугольники вместо арта И кого сцена
        /// помнит чем пересобрать. Без памяти команды лечить нечем.</summary>
        private List<string> DeadActorsWithMemory()
        {
            if (!(_renderer is CanvasSceneRenderer csr)) return null;
            var dead = csr.ActorsWithDeadLayers();
            if (dead == null) return null;
            List<string> mine = null;
            foreach (var id in dead)
                if (_memory.Knows(id)) (mine ??= new List<string>()).Add(id);
            return mine;
        }

        /// <summary>
        /// СВЕРКА КАДРА С ЭКРАНОМ — расходится ли то, что построила история, с
        /// тем, что видит игрок.
        ///
        /// <para>Пока состояние сцены было следом от команд, такой вопрос было
        /// некому задать: «правильный» кадр нигде не записан, сравнивать не с
        /// чем. Теперь он записан, и расхождение — это факт, а не ощущение:
        /// лишний человек в кадре или пропавший из него виден СРАЗУ, в момент
        /// появления, а не через день по скриншоту.</para>
        ///
        /// <para>Сверка ТОЛЬКО ДОКЛАДЫВАЕТ и ничего не чинит. Пока идёт
        /// катсцена или открыт гардероб, расхождение законно — кадр принадлежит
        /// им; а вне наложений автоправка означала бы, что сцену ведут двое, и
        /// мы уже знаем, чем это кончается.</para>
        /// </summary>
        private void CompareFrameToScreen()
        {
            if (SoloActive || _player == null) return;
            if (Commands.HolderOf("say") != null) return;   // кадром распоряжается наложение
            // ВИТРИНА И ГАРДЕРОБ — ТОЖЕ НАЛОЖЕНИЯ. Пока открыт их слой, кадр
            // принадлежит им, и «лишняя» героиня в меню — не расхождение, а
            // ровно то, чего витрина и добивалась. Без этой оговорки сверка
            // ругалась на каждый вход в меню («ЛИШНИЕ [victoria]», живой лог
            // Ильи 28.08), а ложная тревога в отчёте убивает доверие ко всему
            // отчёту.
            if (Score.HasLayer(LvnSender.Menu) || Score.HasLayer(LvnSender.Wardrobe)) return;

            string extra = null, missing = null;
            foreach (var id in ActorsInFrame())
                if (!StoryFrame.Actors.TryGetValue(id, out var w) || !w.Visible)
                    extra = extra == null ? id : extra + ", " + id;
            foreach (var kv in StoryFrame.Actors)
                if (kv.Value.Visible && !ActorVisibleOrPending(kv.Key))
                    missing = missing == null ? kv.Key : missing + ", " + kv.Key;

            if (extra == null && missing == null) { _frameDriftReported = false; return; }
            if (_frameDriftReported) return;      // один доклад на расхождение, а не каждые полсекунды
            _frameDriftReported = true;
            Debug.LogWarning($"[lvn-frame] кадр разошёлся с историей:"
                           + (extra != null ? $" ЛИШНИЕ [{extra}]" : "")
                           + (missing != null ? $" ПРОПАЛИ [{missing}]" : ""));
        }

        private bool _frameDriftReported;

        private bool _bgWasBlankWhite;

        /// <summary>Картинка на полотне ЕСТЬ (факт, не флаг). Хост держит этим
        /// инвариант «в меню всегда есть фон».</summary>
        public bool BackdropHasArt => (_renderer as CanvasSceneRenderer)?.BackdropHasArt ?? false;

        /// <summary>Диагностика «белого прямоугольника» ПО СЦЕНЕ: дерево
        /// оболочки уже показало, что светлого в нём нет, значит пятно рисует
        /// UGUI. Печатаем каждую видимую поверхность, которая рисует СПЛОШНОЙ
        /// светлый цвет без картинки — Image без спрайта и RawImage без
        /// текстуры выглядят именно так.</summary>
        public void DumpOpaqueGraphics()
        {
            var root = (_renderer as CanvasSceneRenderer)?.Root;
            if (root == null) { LvnLog.Trace("[lvn-white] сцена: корня нет"); return; }
            var sb = new StringBuilder("[lvn-white] сплошные светлые поверхности СЦЕНЫ:\n");
            int found = 0;
            foreach (var g in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
            {
                if (g == null || !g.isActiveAndEnabled) continue;
                var c = g.color;
                if (c.a < 0.35f) continue;
                bool hasArt = (g is UnityEngine.UI.Image im && im.sprite != null)
                              || (g is UnityEngine.UI.RawImage ri && ri.texture != null);
                if (hasArt) continue;                       // с картинкой — не наш случай
                if ((c.r + c.g + c.b) / 3f < 0.55f) continue; // тёмное пятно не заметить
                var rt = g.rectTransform;
                var size = rt.rect.size;
                if (size.x < 80f || size.y < 80f) continue;
                found++;
                sb.AppendLine($"  {g.GetType().Name} '{HierarchyPath(g.transform)}' "
                              + $"цвет=#{ColorUtility.ToHtmlStringRGBA(c)} размер={size.x:0}x{size.y:0}");
            }
            if (found == 0) sb.AppendLine("  — сплошных светлых поверхностей нет");
            Debug.Log(sb.ToString());
        }

        private static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
