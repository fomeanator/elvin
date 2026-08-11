using UnityEditor;

/// <summary>
/// Старое имя точки входа для установки иконок: сама логика переехала в пакет
/// движка (<see cref="Lvn.EditorTools.AppIcon"/>), потому что нужна не только
/// песочнице — экспортированный проект собирался с кубиком Unity ровно из-за
/// того, что скрипт жил здесь. Здесь остался вызов, чтобы не рвать команды в
/// чужих скриптах сборки.
///
/// Unity -batchmode -quit -executeMethod SetAppIcon.Apply
/// </summary>
public static class SetAppIcon
{
    public static void Apply() => Lvn.EditorTools.AppIcon.Apply();
}
