# Product services — auth, wallet/IAP, analytics

The app-layer backend a live narrative game needs, shipped with the content
server. Modular by design: each service owns its store under
`<content>/services/` and registers its own routes (`NewXxxService(dir)` +
`Routes(mux)`), so promoting one to a separate process later is a file move
plus a reverse-proxy rule — the HTTP contract already IS the boundary.

Everything is optional. A game that never calls these plays fully offline,
exactly as before.

## Auth — anonymous device accounts

The client mints a random `device_id` once and keeps it secret (it IS the
recovery credential). Registration is idempotent: the same device always gets
the same account back; each register rotates the session token (old one dies).

| Route | Body | Result |
|---|---|---|
| `POST /v1/auth/register` | `{device_id}` (≥16 chars) | `{user_id, token}` — token is `user.secret`, only its hash is stored |
| `GET /v1/auth/me` | — (Bearer) | `{user_id, created}` |

## Wallet — server-authoritative economy

One JSON doc per user (balances, inventory, last-100 audit history), every
mutation under a lock, versions bumped per write.

| Route | Body | Result |
|---|---|---|
| `GET /v1/wallet` | — (Bearer) | the whole doc |
| `POST /v1/wallet/earn` | `{currency, amount>0, reason}` | updated doc |
| `POST /v1/wallet/spend` | `{currency, amount>0, reason, sku?}` | updated doc; **409 `insufficient_funds`** when short; `sku` lands in inventory atomically |
| `POST /v1/iap/verify` | `{platform, sku, receipt}` | grants from `<content>/iap-catalog.json` (`{sku: {currency, amount}}`). Dev builds: run the server with `-iap-dev`. Without it the endpoint answers **501** until real store credentials are configured — an honest refusal beats a fake verification. |

## Daily bonus — the retention classic

One claim per UTC day; consecutive days grow the streak, a gap resets it.
Rewards per streak day come from `<content>/daily-rewards.json` (an array of
`{currency, amount}`; the last entry repeats forever). Grants go through the
wallet — the audit history shows `daily:dayN`.

| Route | Result |
|---|---|
| `GET /v1/daily` | `{streak, claimed_today, next_streak, next_reward}` |
| `POST /v1/daily/claim` | `{streak, reward}`; **409 `already_claimed`** on a repeat |

Unity: `await LvnDaily.GetAsync()` / `await LvnDaily.ClaimAsync()` (the wallet
mirror refreshes itself after a claim).

## Leaderboards

Named boards (`[a-z0-9_-]`), created on first submit. Best score wins;
ties go to the earlier claim. Top-N includes the caller's own rank.

| Route | Body | Result |
|---|---|---|
| `POST /v1/leaderboard/{board}` | `{score, name?}` (Bearer) | `{improved, rank}` — a worse re-submit never downgrades |
| `GET /v1/leaderboard/{board}?n=10` | — (Bearer optional) | `{top, total, me?: {rank, score}}` |

Unity: `await LvnLeaderboard.SubmitAsync("quiz_score", 420, "Fomin")` /
`await LvnLeaderboard.GetTopAsync("quiz_score")`.

## Analytics — append-only event log

Anonymous or authenticated batches; each day is a JSONL file under
`services/analytics/` (one event per line — `jq`/DuckDB-ready). The server
stamps the user; the client never gets to claim one.

| Route | Body | Result |
|---|---|---|
| `POST /v1/analytics/events` | `[{name, ts?, props?}]` ×1..100 | `{accepted}` |
| `GET /v1/analytics/summary?day=YYYY-MM-DD` | — (admin Bearer) | `{total, unique_users, by_name}` |

## Unity clients (`Lvn.Services`, the `com.lvn.engine.services` package)

```csharp
LvnBackend.BaseUrl = "https://api.mygame.example";
await LvnBackend.EnsureRegisteredAsync();          // every boot; offline no-op

await LvnWallet.RefreshAsync();
bool ok = await LvnWallet.SpendAsync("gold", 30, reason: "shop", sku: "sword");
if (!ok) ShowNotEnoughGold();                      // 409 → false, nothing desyncs
LvnWallet.Changed += RefreshShopUI;

LvnAnalytics.Track("chapter_start", ("ch", "ch1")); // fire-and-forget
// batches flush every 20 events / 30 s / on app pause; queue survives
// restarts and caps at 500 — analytics never blocks or bloats the game.
```

`NovelApp` wires the basics automatically: it points `LvnBackend` at its
`ServerUrl`, registers the device on boot and tracks `boot` /
`chapter_start` / `chapter_finish` — a served game gets living analytics
with zero extra code (pure-offline games skip all of it).

Everything degrades gracefully offline: registration keeps the old token,
wallet calls return false/null, analytics queues locally.

## Дома района служб — и почему их нет на карте домов

`docs/where-things-live.md` — карта домов ДВИЖКА. Район служб живёт своей
жизнью: его дома знают про сервер, аккаунт и деньги, и ядро их не видит
(границы сборок — `Lvn.Engine` не ссылается на `Lvn.Services`). Поэтому их
перечень здесь, а не там; но перечень обязан БЫТЬ — невидимый дом заводят
заново, и это причина почти каждой находки про «второй дом рядом с первым».

| Дом | Что в нём |
|---|---|
| `LvnBackend` | устройство-аккаунт: регистрация, токен, имя для показа; всё остальное ходит через него |
| `LvnWallet` | кошелёк: баланс, трата и начисление, идемпотентность (сервер — источник правды, 409 → `false`) |
| `LvnAnalytics` | отправка событий пачками; очередь переживает перезапуск и не растёт бесконечно |
| `LvnEvents` | **имена** событий — договор между игрой и отчётом: имя это стык, клиент его пишет, отчёт по нему считает, и опечатка не падает, а тихо теряет воронку |
| `LvnAttribution` | откуда пришёл игрок: ссылка, по которой он открыл игру. Без неё рекламу запускать нельзя — деньги уходят, а какой креатив их вернул, неизвестно |
| `LvnExperiments` | A/B прямо в сценарии (`если abtest("первая_сцена") == "b"`): гипотезу проверяет автор, не программист |
| `LvnFeedback` | отзыв из игры вместе со сборкой и состоянием — тестер в мессенджере не назовёт ни того, ни другого |
| `LvnLogShip` | полевая диагностика: предупреждения, ошибки и метки `[lvn-boot]`/`[lvn-perf]` уезжают на сервер; чужой телефон иначе молчит |
| `LvnPlatformAuth` | шов к платформенным входам (Google Play Games, Sign in with Apple): движок не везёт ни одного SDK, хост подставляет по делегату |
| `LvnStoryFunctions` | функции выражений, которым нужен внешний мир: кошелёк, покупки, надетое. Живут здесь, а не в ядре, по той же причине, что и весь район |
| `LvnNetRoom` | комната на двоих и больше: общий стол с именованными ящиками. Правил ни одной игры здесь нет — их считает сценарий |
| `LvnWebView` | открыть страницу ВНУТРИ приложения (инструкция оплаты, условия, промо) — шов к платформенному просмотрщику |

Вне обоих перечней остаются намеренно: `Lvn3DDemoSet` (сцена-демонстрация,
не роль) и `LvnSpineBridge` (мост к чужому трактору Spine).

## Splitting into real microservices (when needed)

Each service touches only its own directory and takes its dependencies via
its constructor. To carve one out: move its file into a `cmd/<name>/main.go`
with its own `ListenAndServe`, point the gateway (or the client's BaseUrl)
at it, and hand it the same data directory. No shared state, no shared DB —
that's why the seam holds.
