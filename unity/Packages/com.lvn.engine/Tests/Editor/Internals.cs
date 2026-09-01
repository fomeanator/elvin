using System;
using System.Reflection;
using NUnit.Framework;
using Lvn.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// ЗАГЛЯНУТЬ ВНУТРЬ — поле, свойство и способ чужого класса по имени.
    ///
    /// <para>Сцена почти вся приватна, и правильно: её состояние — не
    /// интерфейс. Но проверять правила про ввод, барьеры и расстановку иначе
    /// нечем, поэтому тесты берут поля отражением.</para>
    ///
    /// <para>Приём был расписан дословно в ЧЕТЫРЁХ файлах — двух про сцену и
    /// двух про оболочку, — а отражением в тесты ходят одиннадцать. Важен
    /// здесь не сам вызов, а ДВЕ детали, о которых легко не подумать с
    /// третьего раза: обход по базовым типам (сцена — partial-класс с
    /// наследниками, и поле может лежать выше) и внятная жалоба вместо
    /// <c>NullReferenceException</c>. Переименуют поле — тест обязан сказать
    /// «якорь протух», а не упасть в пустоту.</para>
    ///
    /// <para>Тип приходит доводом: вопрос «дай поле по имени» один и тот же у
    /// сцены и у оболочки, и разводить его по двум домам значило бы завести
    /// второе место, где легко забыть про базовые типы.</para>
    /// </summary>
    internal static class Внутренности
    {
        private const BindingFlags Любое =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>Поле сцены по имени. Ищет и у базовых типов.</summary>
        public static FieldInfo Поле(string имя) => Поле(typeof(VnStage), имя);

        /// <summary>Поле любого класса по имени. Ищет и у базовых типов.</summary>
        public static FieldInfo Поле(Type тип, string имя)
        {
            for (Type t = тип; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(имя, Любое);
                if (f != null) return f;
            }
            Assert.Fail($"поле {имя} у {тип.Name} пропало — поправь якорь теста");
            return null;
        }

        /// <summary>Свойство сцены по имени.</summary>
        public static PropertyInfo Свойство(string имя)
        {
            PropertyInfo p = typeof(VnStage).GetProperty(имя, Любое);
            if (p == null) Assert.Fail($"свойство {имя} у VnStage пропало — поправь якорь теста");
            return p;
        }

        /// <summary>Значение поля у живого объекта, приведённое к типу. Пустой
        /// результат — тоже жалоба: тест, молча получивший null, проверяет
        /// потом не то.</summary>
        public static T Достать<T>(object объект, Type тип, string имя, string зачем) where T : class
        {
            var v = Поле(тип, имя).GetValue(объект) as T;
            Assert.NotNull(v, зачем);
            return v;
        }

        /// <summary>Способ класса по имени — без доводов.</summary>
        public static MethodInfo Способ(Type тип, string имя)
        {
            var m = тип.GetMethod(имя, BindingFlags.Instance | BindingFlags.NonPublic,
                                  null, Type.EmptyTypes, null);
            if (m == null) Assert.Fail($"способ {имя} у {тип.Name} пропал — поправь якорь теста");
            return m;
        }

        /// <summary>Прочитать поле у живой сцены.</summary>
        public static object Достать(object сцена, string имя) => Поле(имя).GetValue(сцена);

        /// <summary>Вложить значение в поле живой сцены.</summary>
        public static void Вложить(object сцена, string имя, object значение) =>
            Поле(имя).SetValue(сцена, значение);
    }
}
