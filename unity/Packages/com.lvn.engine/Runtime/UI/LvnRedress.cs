using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ПЕРЕОДЕТЬ ЖИВОЙ ЭКРАН — когда сменились слова, шрифт или размеры.
    ///
    /// <para>Подписи ставятся один раз, при сборке экрана. Пока настройка меняла
    /// только новые экраны, это было незаметно; с переключением языка стало
    /// видно сразу: игрок переключает язык, а открытый гардероб, нижнее меню и
    /// шапка настроек остаются на прежнем — и он решает, что переключатель
    /// сломан. То же самое с гарнитурой и размером.</para>
    ///
    /// <para>Обходить дерево и «искать подписи» нельзя: подпись — это не всякий
    /// Label, а результат решения экрана (какой ключ, какое авторское поле,
    /// какое умолчание). Знает это только он сам. Поэтому экран объявляет, что
    /// умеет переодеться, а дом лишь находит всех, кто это умеет, и просит.</para>
    ///
    /// <para>Просьба идёт СВЕРХУ ВНИЗ по дереву и не заходит внутрь того, кто
    /// уже переоделся: экран пересобирает своих детей сам, и второй проход по
    /// ним стоил бы двойной сборки на каждую смену языка.</para>
    /// </summary>
    public interface ILvnRedress
    {
        /// <summary>Перечитать подписи и размеры. Экран, которого нет на
        /// экране, вправе не делать ничего — его пересоберут при открытии.</summary>
        void Redress();
    }

    public static class LvnRedress
    {
        /// <summary>
        /// ПОДПИСЬ ПОМНИТ, ОТКУДА ОНА ВЗЯЛАСЬ. Текст ставится строкой — и на
        /// этом связь со словарём кончается: экран знает, как собрать подпись,
        /// но узнать это по готовому <c>Label</c> нельзя, и при смене языка она
        /// остаётся прежней. Так и выходило «много где»: имена в ростере,
        /// подписи вкладок, кнопки внутри списков.
        ///
        /// <para>Привязка отдаёт элементу его ИСТОЧНИК: дом перечитывает
        /// поставщика и ставит новый текст сам — экрану не нужно ни помнить
        /// ссылку, ни объявлять <see cref="ILvnRedress"/>. Одна обёртка на
        /// месте создания закрывает подпись навсегда.</para>
        /// </summary>
        public static T Bind<T>(T el, System.Func<string> text) where T : TextElement
        {
            if (el == null || text == null) return el;
            el.userData = text;          // источник живёт вместе с элементом
            el.text = text();
            return el;
        }

        // ЖИВЫЕ КОРНИ. Интерфейс игры живёт не одним деревом: оболочка в своём
        // документе, сцена в своём, гардероб — внутри сцены. Обход от корня
        // оболочки до них не доходит, и получалось «главная переключилась, а
        // гардероб только после перезахода» (Илья, 28.08).
        //
        // Слабые ссылки: документы сносятся и пересоздаются (Stop→Play, смена
        // главы), и список, держащий их живыми, был бы утечкой ровно того
        // размера, что и сама игра.
        private static readonly List<System.WeakReference<VisualElement>> _roots
            = new List<System.WeakReference<VisualElement>>();

        /// <summary>Объявить корень дерева: его будут переодевать вместе со
        /// всеми. Повторная регистрация того же корня безвредна.</summary>
        public static void Register(VisualElement root)
        {
            if (root == null) return;
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                if (!_roots[i].TryGetTarget(out var live)) { _roots.RemoveAt(i); continue; }
                if (ReferenceEquals(live, root)) return;
            }
            _roots.Add(new System.WeakReference<VisualElement>(root));
        }

        /// <summary>ГЛОБАЛЬНАЯ ПЕРЕРИСОВКА: переодеть всё, что живо. Зовётся
        /// сама на смену слов, гарнитуры и размеров — экранам подписываться не
        /// нужно.</summary>
        public static void All()
        {
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                if (!_roots[i].TryGetTarget(out var root) || root.panel == null && root.parent == null)
                { _roots.RemoveAt(i); continue; }
                All(root);
            }
        }

        /// <summary>Попросить переодеться всех, кто это умеет, под этим
        /// корнем.</summary>
        public static void All(VisualElement root)
        {
            if (root == null)
            {
                // Переодевать некого — а игрок ждёт, что интерфейс сменит язык.
                // Молчать тут нельзя: «ничего не произошло» и «нечего было
                // делать» с экрана выглядят одинаково.
                Debug.LogWarning("[lvn-redress] корня нет — переодевать некого");
                return;
            }
            int dressed = 0, rebound = 0;
            var pending = new Stack<VisualElement>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var el = pending.Pop();
                // Привязанная подпись перечитывается всегда: она знает свой
                // источник, и экрану для этого ничего делать не нужно.
                if (el is TextElement bound && bound.userData is System.Func<string> src)
                {
                    try
                    {
                        var now = src();
                        if (now != bound.text)
                        {
                            bound.text = now;
                            LvnMotion.FadeIn(bound, delayMs: 0, ms: LvnMotion.Quick);
                        }
                        rebound++;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[lvn-redress] подпись не перечиталась: {e.Message}");
                    }
                }
                if (el is ILvnRedress r)
                {
                    try
                    {
                        r.Redress();
                        dressed++;
                        // ПРОСТУПАЕТ ТЕКСТ, А НЕ ЭКРАН. Проявлять панель целиком
                        // значит мигать фоном, плашками и картинками — со стороны
                        // это белая вспышка, а не смена языка. Меняются ТОЛЬКО
                        // подписи, они и проступают; всё остальное стоит на месте.
                        FadeTexts(el);
                    }
                    catch (System.Exception e)
                    {
                        // Один упавший экран не имеет права оставить остальные
                        // на прежнем языке.
                        UnityEngine.Debug.LogWarning($"[lvn-redress] {el.GetType().Name}: {e.Message}");
                    }
                    continue;   // он пересобрал детей сам
                }
                for (int i = 0; i < el.childCount; i++) pending.Push(el[i]);
            }
            // Штамп: сколько экранов ответило. Ноль означает, что переодеваться
            // некому — и это ровно тот случай, когда игрок говорит «никакой
            // реакции», а в логе иначе не было бы ни строчки.
            LvnLog.Info($"[lvn-redress] переодето экранов: {dressed}, подписей: {rebound}");
        }
        // Текстовые узлы переодетого экрана. Дерево обходим один раз и с
        // потолком: на длинном списке проявлять каждую строку по отдельности
        // дороже, чем сама смена языка, а разницы на глаз уже нет.
        private static void FadeTexts(VisualElement root)
        {
            const int Cap = 120;
            int n = 0;
            var pending = new Stack<VisualElement>();
            pending.Push(root);
            while (pending.Count > 0 && n < Cap)
            {
                var el = pending.Pop();
                if (el is TextElement t && !string.IsNullOrEmpty(t.text))
                {
                    LvnMotion.FadeIn(t, delayMs: 0, ms: LvnMotion.Quick);
                    n++;
                }
                for (int i = 0; i < el.childCount; i++) pending.Push(el[i]);
            }
        }
    }
}
