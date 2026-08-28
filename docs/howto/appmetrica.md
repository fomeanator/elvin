# AppMetrica в игре на LVN (TR-35)

Движок не тянет чужие SDK: их платформенные зависимости не нужны библиотеке, а
игра, которой аналитика не нужна, не должна за неё платить. Поэтому в движке —
шов, а SDK ставится в проект игры.

## 1. Поставить пакет

Unity Package Manager → Add package from git URL:
`https://github.com/appmetrica/appmetrica-unity-plugin.git`

## 2. Включить при старте (один раз)

```csharp
// В сцене загрузки, рядом с NovelApp:
using Io.AppMetrica;

void Awake()
{
    AppMetrica.Activate(new AppMetricaConfig("fe3f8a9e-8311-4880-afc8-064852923364")
    {
        FirstActivationAsUpdate = !IsFirstLaunch(),
        SessionTimeout = 120,
        LogsEnabled = Debug.isDebugBuild,
    });

    // Зеркало событий движка: те же имена и поля, что уходят на наш сервер —
    // тогда отчёты сходятся, а не спорят.
    Lvn.Services.LvnAnalytics.Mirror = (name, props) =>
        AppMetrica.ReportEvent(name, props?.ToString(Newtonsoft.Json.Formatting.None));
}
```

## 3. Что поедет

Все события движка (`LvnEvents`): старт/финал главы, выборы, покупки, реклама,
вход, ошибки загрузки. К каждому уже приложены `sid` (сессия), `title`, `chapter`
и группы A/B — в AppMetrica они станут параметрами события.

## 4. Проверка

`LogsEnabled` в отладочной сборке печатает отправку в logcat; на панели
AppMetrica события появляются в «Отчётах → События» в течение нескольких минут.
