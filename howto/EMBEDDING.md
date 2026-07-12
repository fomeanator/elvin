# Встраивание движка в свою игру

Движок — библиотека (`com.lvn.engine`, UPM). Экспортируемый шаблон — просто
самый тонкий хост. Полный справочник швов: `docs/embedding.md` (EN).

## Три уровня входа

1. **Только сценарий** — `LvnPlayer` + свой `ILvnStage` (4 метода): рисуешь
   диалоги чем угодно, движок ведёт сюжет/переменные/сейвы.
2. **Сцена** — `VnStage` на своём GameObject: весь показ (тайпрайтер, выборы,
   актёры, кости, Spine, меню, сейвы). Свои ассеты — через `ILvnAssets`.
3. **Весь шелл** — `NovelApp` (пакет `com.lvn.engine.shell`, тянет
   `com.lvn.engine.services`): карусель/хаб, главы, резюм, магазины, настройки.

## «Движку не хватает X» — клапаны

### Свои команды сценария — `LvnOps`

```csharp
LvnOps.Register("minigame", (cmd, ctx) => {
    ctx.Hold();                              // сюжет ждёт
    MyMinigame.Run((string)cmd["kind"], won => {
        ctx.Vars["won"] = won;               // тот же стор, что у set/if
        if (!won) ctx.GoTo("failed");
        ctx.Resume();                        // сюжет продолжается
    });
});
```

В `.lvns` автор пишет: `ext minigame kind="lockpick"`. Без `Hold()` —
выстрелил-и-забыл. Валидатор на неизвестный оп даёт предупреждение (не
ошибку): возможно, он твой.

### Свои пункты меню

```csharp
StageMenu.AddMenuItem("Достижения", stage => MyAchievements.Show());
```

### События

- `LvnPlayer.OnSay` — каждая реплика;
- `VnStage.Saved` — после каждого сейва (клауд-синк, ачивки);
- `NovelApp.ChapterStarted / ChapterFinished` — жизненный цикл глав;
- `VnStage.ExitRequested`, `ChromeHiddenChanged`.

### Рисовать поверх

Свой `UIDocument` с большим `sortingOrder` — поверх движка; на время своего
экрана ставь `stage.InputBlocked = true`.

### Веб-вью (in-app browser)

Движок **не линкует** веб-вью-либу — только шов `Lvn.Services.LvnWebView`:
движок зовёт `LvnWebView.Open(url)` (баннер «как оплатить из РФ» в магазине,
ToS/Policy), хост подключает реализацию через переменную-хук.

```csharp
LvnWebView.Opener = url => { web.LoadURL(url); web.SetVisibility(true); return true; };
```

Без хука `Open` открывает системный браузер (`Application.OpenURL`) — безопасный
дефолт, ноль зависимостей. Готовая обвязка под gree/unity-webview — сэмпл
**Web view (gree adapter)** (Package Manager ▸ LVN Engine ▸ Samples):
ставишь плагин в `Assets/`, импортируешь сэмпл — он сам регистрирует шов.

### Опциональные модули

Тяжёлые интеграции — отдельной сборкой с version define (образцы:
`com.lvn.engine.spine`, `com.lvn.engine.addressables`): свой пакет есть —
модуль компилируется, нет — движок чист. Шелл и сервисы — тоже отдельные
пакеты (`com.lvn.engine.shell`, `com.lvn.engine.services`): встраиваемой
игре со своим UI они не нужны вовсе.

## Контракт

Всё перечисленное — поддерживаемая поверхность: внутри мажорной версии только
растёт (см. `docs/releasing.md`). `internal` и неупомянутое — может меняться.
