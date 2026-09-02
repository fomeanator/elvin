using NUnit.Framework;
using Lvn.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// ОДОЛЖЕННОЕ У СРЕДЫ — то, что тест портит и обязан вернуть.
    ///
    /// <para>Тесты идут в одном процессе и по одной среде: тема, имя игрока,
    /// файл прогресса на диске — общие. Испортил и не вернул — соседний тест
    /// падает НЕ ОТ СВОЕЙ ПРИЧИНЫ, и разбор уходит в чужой файл. Хуже другое:
    /// падает он не всегда, а по порядку запуска, и выглядит как флейк.</para>
    ///
    /// <para>Обряд возврата был расписан копиями: тему одалживали шесть файлов,
    /// файл прогресса — два, и один из шести уже написал свою форму. Тела
    /// одинаковы, а причина записана только у части — верный признак, что
    /// копии начали расходиться.</para>
    ///
    /// <para>Составом, а не наследником: тест вправе одолжить и тему, и сейф
    /// разом, а базовый класс в C# бывает только один.</para>
    /// </summary>
    internal sealed class ОдолженнаяТема
    {
        private string _была;

        /// <summary>Запомнить, под какой темой пришли.
        ///
        /// <para>ИДЕМПОТЕНТНО, и это не удобство. Один из заимствующих берёт
        /// тему ЛЕНИВО — в момент сборки оболочки, а сборок за тест бывает
        /// несколько. Второе взятие запомнило бы уже НАШУ тему, и возврат
        /// оставил бы чужим тестам её.</para></summary>
        public void Взять() { if (_была == null) _была = LvnTheme.Current.Name; }

        /// <summary>Вернуть чужим тестам их тему. Экраны выбирают тему на
        /// сборке, поэтому одолжить её может и тот, кто её не трогал.</summary>
        public void Вернуть() { if (_была != null) LvnTheme.Use(_была); _была = null; }
    }

    /// <summary>СОЗДАННОЕ ТЕСТОМ — снести за собой.
    ///
    /// <para>Тесты оболочки заводят настоящие <c>GameObject</c>, и они
    /// переживают тест: редактор не убирает их сам. Оставленный объект — не
    /// мусор в памяти, а ЖИВОЙ участник: у оболочки есть статические двери, и
    /// брошенный экран продолжает на них отвечать. Соседний тест получает
    /// чужие ответы и падает не от своей причины.</para>
    ///
    /// <para>Уборка была расписана тремя копиями, и каждая — три строки, из
    /// которых легко забыть <c>Clear()</c>: тогда следующий заход попробует
    /// снести уже снесённое.</para>
    /// </summary>
    internal sealed class Мусор
    {
        private readonly System.Collections.Generic.List<UnityEngine.GameObject> _список
            = new System.Collections.Generic.List<UnityEngine.GameObject>();

        /// <summary>Взять объект под уборку и вернуть его же — чтобы вызов
        /// вставал прямо в выражение, а не отдельной строкой, которую забудут.</summary>
        public UnityEngine.GameObject Беречь(UnityEngine.GameObject go)
        {
            if (go != null) _список.Add(go);
            return go;
        }

        public void Убрать()
        {
            foreach (var go in _список)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _список.Clear();
        }
    }

    /// <summary>Имя игрока и файл прогресса на диске: они принадлежат стенду,
    /// а не тесту. Берём на сохранение и кладём обратно, чем бы тест ни
    /// кончился.</summary>
    internal sealed class ОдолженныйСейф
    {
        private string _имяБыло;
        private string _сейфБыл;

        public static string Путь =>
            System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "lvn_progress.json");

        public void Взять()
        {
            _имяБыло = LvnPrefs.PlayerName;
            _сейфБыл = System.IO.File.Exists(Путь) ? System.IO.File.ReadAllText(Путь) : null;
        }

        public void Вернуть()
        {
            LvnPrefs.PlayerName = _имяБыло ?? "";
            if (_сейфБыл != null) System.IO.File.WriteAllText(Путь, _сейфБыл);
            else if (System.IO.File.Exists(Путь)) System.IO.File.Delete(Путь);
        }
    }
}
