# Offline play contract

Gameplay reads and writes local state without awaiting HTTP. Download content
before travelling: an unbundled, never-downloaded file cannot appear offline.
The download indicator checks actual cached files, not merely completed requests.

## Progress

`HttpStateStore.LoadVarsAsync` returns local variables immediately and schedules
a refresh. `SaveVarsAsync` persists a new pending revision before returning.
The owner-scoped index and revision survive process restart. The Unity worker
retries with bounded backoff (up to 30 seconds), wakes on reconnect/resume, and
also probes independently of the global offline flag. `ForceOffline` prevents
delivery until released.

Tools that require a completed delivery pass explicitly await `FlushAsync`;
cloud restore uses `RefreshVarsAsync`. A finished pass does not imply success:
`PendingCount` remains nonzero after a failed transfer. These waits do not belong
on the story path.

Delivery GETs the server version, then uses versioned PUT with the existing
scope merge policy. An acknowledged old revision cannot erase a newer local
save, recreate forgotten data, or write into a different account. A server
snapshot already equal to the intended result acknowledges a lost response
without another PUT. Explicit empty writes still clear variables.

Cloud results update local storage, not the running scene. The idle hub may
refresh its progress indicators. `NovelApp.LiveChapterUpdates` defaults to false:
content updates wait until the player leaves the chapter. Authoring hosts may
explicitly opt into live edits.

## Wallet

Story earns/spends update a single persisted mirror-plus-queue journal immediately.
Spends require sufficient local funds; invalid/overflowing amounts are refused.
FIFO delivery preserves each `op_id`. Acknowledgements rebase the still-pending
tail, so a response cannot erase later local operations. Transport errors,
408/425/429, 5xx and auth failures retain the queue. Permanent rejections are
reconciled against the server's wallet before dropping an operation.

The server remains authoritative: a local success is provisional, and conflicting
spends from multiple devices can be rejected at sync. IAP receipts and ad rewards
remain online-only. Account switching retains the existing wallet policy: discard
the previous account's local mirror/queue, never replay it as another account.

Server limitation: wallet deduplication currently remembers only the last 200
operation ids (`server/wallet.go`, `appliedOpsWindow`). The client cannot guarantee
exactly-once replay if a lost acknowledgement is retried after that window has
been displaced by another device. Extending this guarantee requires a server
change; this client package does not alter the server.

## Downloaded content

Explicit chapter/all downloads pin exact version cache keys on disk before the
transfer. Automatic quota/old-version sweeps preserve them, including after
restart. Changing art quality does not purge pinned originals. The explicit
“delete downloaded content” action clears pins and bytes.

Downloaded scripts and published localization sidecars can be read directly
from the byte cache, even if the chapter was never opened. Download planning and
availability include sidecars declared by the version index. Missing optional
catalogs are not cached as permanent failures.

Pins protect downloaded bytes; they do not manufacture missing assets, guarantee
free disk space, or freeze all future content releases as an immutable package.
After a content/quality update the indicator may require a fresh download.
Interrupted downloads retain the existing Download Center retry UI.

## Regression coverage

- `StateBackgroundSyncTests`: local-only completion, durable retries, lost/stale
  acknowledgements, ownership, deletion and merge semantics.
- `WalletBackgroundSyncTests`: transient/auth failures, stable ids after reload,
  pending-tail rebasing, stale account replies, malformed success and amounts.
- `OfflinePinTests`: restart, quota, backup recovery, explicit clearing, and
  first offline opening of a downloaded script.
- `OfflineReconnectTests` (PlayMode): real HTTP 503, simulated client restart,
  autonomous recovery without UI/manual flush, and a committed state update whose
  gateway acknowledgement fails.

Run both Unity platforms with `qa/run-all.sh`.
