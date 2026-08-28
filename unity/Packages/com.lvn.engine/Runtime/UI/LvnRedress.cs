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
            int dressed = 0;
            var pending = new Stack<VisualElement>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var el = pending.Pop();
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
            LvnLog.Info($"[lvn-redress] переодето экранов: {dressed}");
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
