# Elvin — Product Services

Offline-first clients for the product backend that ships with the
[Elvin engine](../com.lvn.engine)'s Go content server:

| Client | What it does |
|---|---|
| `LvnBackend` | Device auth (anonymous, idempotent register) + the request plumbing |
| `LvnWallet` | Server-authoritative currencies with an offline mirror and a kill-safe replay queue |
| `LvnPlatformAuth` | Google / Apple sign-in → account link/login (cross-device recovery) |
| `LvnAds` | Rewarded ads, server-defined amounts + daily caps |
| `LvnAnalytics` | Append-only event batches |
| `LvnDaily` | Daily-bonus streaks |
| `LvnLeaderboard` | Named boards, best-score-wins |
| `LvnWebView` | A seam for an in-app web view (see the engine's gree sample) |
| `LvnServiceOps` | Registers the script-facing `ext` ops: `wallet_earn` / `wallet_spend`, `leaderboard_submit`, `daily_claim`, `ad_reward`, `track` |

Everything is optional and degrades gracefully offline — a game that never
configures a server still plays. The novel-shell package
(`com.lvn.engine.shell`) consumes these for its store/wardrobe/profile
screens; a custom game can use them headless — call the statics, or author
the economy straight from `.lvns` via the `ext` ops (declare them in your
`ext-grammar.json` to keep the zero-warnings gate).

## Install

`https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine.services`

Server setup: `docs/services.md` in the repository root.
