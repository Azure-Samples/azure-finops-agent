# Persistent Multi-Session Chat — Technical Description

## 1. Purpose

Before this change set, the Azure FinOps Agent had a **single live conversation per browser session**. Closing the tab, redeploying the container, or being idle past the 30-minute SDK timeout meant the user lost their chat history and had to re-consent to Azure / Graph / Log Analytics on the next visit.

The goal of this work is to give every user — anonymous or Entra-authenticated — **multiple long-lived conversations** that survive container restarts, slot swaps, OAuth token expiry, and 24-hour absences, **without re-prompting for OAuth consent**. Entra users additionally get cross-device continuity (sign in on a new browser → see the same conversations).

## 2. Architecture at a glance

Three independent persistence layers cooperate:

| Layer                           | What it stores                                                          | Where                                                                                     | Lifetime                            |
| ------------------------------- | ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- | ----------------------------------- |
| **Copilot SDK session state**   | Chat history, tool calls, model output                                  | `$COPILOT_HOME/.copilot/session-state/{sessionId}/` (Azure Files `/home`)                 | Until explicit delete or 30-day TTL |
| **Per-user workdir**            | SDK working directory used as the _ownership marker_                    | `$COPILOT_HOME/users/{oid}` (Entra) or `$COPILOT_HOME/anon/{userId}`                      | Same as session state               |
| **`PersistentIdentity` record** | Encrypted `oid`, `tenantId`, derived `userId`, refresh token, GraphTier | `$COPILOT_HOME/users/{oid}/identity.json` (DataProtection-encrypted) + `finops_id` cookie | 30 days, sliding                    |

The combination is what makes restart-survivable login possible: the cookie tells us _who_ the user is, the identity record gives us a fresh access token (via the persisted refresh token), and the SDK rehydrates the conversation from disk on the next prompt.

## 3. Identity & user-id derivation

`Auth/PersistentIdentity.cs` (new file) is the single source of truth for "who is this caller, and how do I prove it across restarts".

### 3.1 Deterministic `userId` from Entra OID

```csharp
public static long DeriveUserId(string oid)
    => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(oid)), 0);
```

The legacy `userId` was a random `long` minted per browser session. That was fine while everything lived in memory, but persistence demands a value that's the same every time the same human comes back. Hashing the Entra OID gives:

- **Stability** — same `oid` always produces the same `userId`, so workdirs, telemetry-keyed dictionaries, and token caches all line up after a restart.
- **Cross-tenant safety** — Entra OIDs are globally unique GUIDs; SHA-256 of a GUID has negligible collision probability across the population the app will ever serve.
- **Backward compatibility** — anonymous users keep the old random `long`, so nothing about their code path changed.

### 3.2 Encrypted identity file + signed cookie

- The record is serialized to JSON, encrypted with ASP.NET Core **DataProtection** (`protector scope = "FinOps.Identity.v1"`), and written atomically to `identity.json`.
- A companion `finops_id` cookie (HttpOnly, Secure, SameSite=Lax, 30-day) holds the _encrypted_ OID — never plaintext. On the next request, decrypting the cookie tells us which `identity.json` to load.
- DataProtection keys themselves are persisted to `/home/dataprotection-keys/` (Program.cs ~line 37) so that a container restart doesn't invalidate every cookie in the wild.

### 3.3 Atomic, lock-protected writes

```csharp
private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
private static SemaphoreSlim LockFor(string oid) => _fileLocks.GetOrAdd(oid, _ => new(1, 1));

private static void AtomicWrite(string path, byte[] bytes)
{
    var tmp = path + ".tmp";
    File.WriteAllBytes(tmp, bytes);
    File.Move(tmp, path, overwrite: true);   // atomic on POSIX
}
```

- A per-OID `SemaphoreSlim` serializes concurrent `SaveIdentity` / `UpdateRefreshToken` / `UpdateGraphTier` calls (otherwise a refresh-token rotation racing with a tier-consent could corrupt the file).
- All mutations write `identity.json.tmp` first, then `File.Move(..., overwrite: true)`, which is atomic on the Linux filesystem App Service mounts. A crash mid-write leaves either the old file or a discardable `.tmp` — never a half-written `identity.json`.

### 3.4 The `UpdateRecord(oid, mutate)` helper

`UpdateRefreshToken` and `UpdateGraphTier` both go through:

```csharp
private void UpdateRecord(string oid, Action<IdentityRecord> mutate) { /* lock + load + mutate + AtomicWrite */ }
```

which keeps the lock + atomic-write logic in exactly one place and guarantees we never overwrite the file with stale fields when only one column changed.

## 4. The hydration middleware

In `Program.cs`, before any user-bootstrap logic runs, a middleware tries `persistentIdentity.Load(ctx)`:

- **Hit** — the cookie decrypted to a known `oid` and the identity file exists. We restore the four session blobs the rest of the pipeline expects (`user`, `azure_user`, `azure_refresh_token`, `graph_tier`). The downstream `SessionTokenStore` will mint fresh access tokens from the refresh token on first use.
- **Miss** — fall back to the legacy random anon id. New anonymous user, no surprises.

This is the _only_ place that touches the identity file on the read path; the rest of the app reads tokens out of the ASP.NET session as it always did.

## 5. The OAuth callback: identity migration & GraphTier persistence

`Auth/MicrosoftAuthEndpoints.cs` was extended so each Entra callback does three things in addition to its existing token exchange:

1. **Migrate the in-memory user**. After the `id_token` is validated, derive `newUserId = DeriveUserId(oid)` and copy `telemetry.UserTokens`, `telemetry.UserTools`, and `telemetry.CurrentSessionId` from the random anon id to the deterministic OID-derived id. This is a no-op on subsequent logins but seamlessly converts a fresh visitor's anon session into their Entra session without losing the conversation they may have already started.
2. **Persist the rotating refresh token + the current GraphTier**:
   ```csharp
   if (!string.IsNullOrEmpty(refreshToken))
       persistentIdentity.SaveIdentity(ctx, new IdentityRecord { Oid = oid, ..., RefreshToken = refreshToken, GraphTier = ctx.Session.GetString("graph_tier") });
   else
       persistentIdentity.UpdateGraphTier(oid, ctx.Session.GetString("graph_tier"));
   ```
   The `else` branch covers the re-consent edge case where Entra returns no fresh refresh token but the user just added a new add-on — without it, post-restart hydration would forget the new add-on consent.
3. **Logout** clears the cookie and the on-disk file via `persistentIdentity.Clear(ctx, oid)`.

## 6. Token store

`Auth/SessionTokenStore.ExchangeRefreshTokenForResource` now returns `(Token, Expiry, RotatedRefreshToken?)`. On any rotation we call `_identity.UpdateRefreshToken(oid, rotated)` so the `/home` record always has the freshest token. This is what makes "redeploy → user keeps their Azure consent" actually work: even if the access token in the in-memory session is dead, the refresh token on disk mints a new one transparently on the next request.

## 7. Multi-session SDK glue

`AI/CopilotSessionFactory.cs` is where the per-user multi-conversation behavior lives. Key invariants:

### 7.1 Workdir as ownership marker

Every `CopilotSession` is created with `WorkingDirectory = $COPILOT_HOME/users/{oid}` (Entra) or `…/anon/{userId}`. The SDK persists `metadata.Context.WorkingDirectory` for each session, so listing the user's conversations is just _list all sessions whose `WorkingDirectory` matches my workdir_. We never store our own session-id index — the SDK's filesystem layout is the index.

### 7.2 Live-vs-disk distinction (`AiTelemetry.LiveSessions`)

`AiTelemetry.LiveSessions` is a `ConcurrentDictionary<sessionId, LiveSessionInfo>` containing only sessions currently held open in memory. `LiveSessionInfo` carries the `CopilotSession` instance, the `UserId` (init-only), and `BearerExpiry`. The SDK auto-disconnects after `SessionIdleTimeoutSeconds = 1800`, so this dict naturally trims itself; on the next prompt we transparently `ResumeSessionAsync` from the disk state with a fresh bearer.

### 7.3 Proactive bearer recycle (BYOK gotcha)

`ProviderConfig.BearerToken` is a _static string baked into the CLI subprocess at session creation_ — there is no callback for refreshing it. Azure OpenAI tokens expire after ~1 h, so any `CopilotSession` older than its token would 401 on every prompt. `GetOrCreateSessionAsync` checks `expiry - now < 10 min` and proactively `ResumeSessionAsync(sessionId, freshBearer)` — disk state preserved, history preserved, only the live wrapper is replaced. This must not be removed if the BYOK provider is ever swapped.

### 7.4 IDOR guard with graceful fallback

`UserOwnsSessionAsync(userId, oid, sessionId)` checks ownership by comparing the session's persisted `Cwd` against the caller's expected workdir. ChatEndpoints uses this to guard _resume / select / delete / replay_. If a stale `localStorage` value points to a session the user no longer owns, we silently fall through to `GetCurrentOrCreateAsync` instead of returning 404 — better UX, same security posture.

### 7.5 Listing & path comparison

`ListAllManagedSessionsAsync` (used by the janitor) restricts to sessions whose `Cwd` starts with `$COPILOT_HOME/users` or `…/anon`. The compare is `StringComparison.Ordinal` because Linux filesystems are case-sensitive and our roots are constructed from a constant. Anything outside those two roots is some other component's state and we leave it alone.

### 7.6 Title generation

On the first user/assistant exchange we ask the model for a 5-word title via a tiny chat-completions call. The `max_completion_tokens = 24` parameter is GPT-5 / o-series specific — there's an inline comment so a future GPT-4 swap doesn't silently 400.

## 8. The `/api/sessions` REST surface

`Endpoints/SessionEndpoints.cs` (new file, ~330 LOC) exposes:

| Method   | Path                            | Purpose                                                                                                                                                                                                                          |
| -------- | ------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GET`    | `/api/sessions`                 | List the caller's conversations (filtered by `Cwd`). **Anon users get an empty list** — their `userId` is randomized per browser session, so they could never re-find old chats anyway; the sidebar is intentionally Entra-only. |
| `POST`   | `/api/sessions/new`             | Force-create a new conversation and make it current.                                                                                                                                                                             |
| `POST`   | `/api/sessions/{id}/select`     | Switch the user's "current" pointer (with IDOR check).                                                                                                                                                                           |
| `GET`    | `/api/sessions/{id}/transcript` | Read-only history fetch via `LoadTranscriptAsync` — does **not** disturb `CurrentSessionId` or the live-session gauge.                                                                                                           |
| `DELETE` | `/api/sessions/{id}`            | Tears down live wrapper, removes title, removes current-session pointer if it matches, then `_copilotClient.DeleteSessionAsync` which deletes the on-disk session-state directory.                                               |

The chat SSE endpoint (`AI/ChatEndpoints.cs`) accepts an optional `sessionId` to resume a specific conversation, threads it through the IDOR check, and sends a `session_id` SSE event so the frontend can sync `localStorage`.

## 9. TTL janitor

`Auth/UserStateJanitor.cs` is a `BackgroundService` that wakes hourly and uses `ListAllManagedSessionsAsync` to find sessions whose `LastUpdated` is older than 30 days, calling `DeleteSessionByIdAsync` to remove them. The scope is deliberately narrow — only `users/` and `anon/` — so the janitor can never accidentally delete state from a co-located component sharing the Azure Files mount.

## 10. Frontend (`ChatView.vue`)

The Vue chat UI now has a vertical-split right sidebar: tool calls on top, Conversations list on bottom. On load it `GET`s `/api/sessions`, renders titles with relative timestamps, highlights the active one, and supports click-to-switch and trash-to-delete (single-click delete that removes from disk via the chain above). Anon users simply don't get this panel because the API returns an empty list.

## 11. End-to-end flow after these changes

1. **First visit, anon** — middleware finds no cookie, mints a random anon `userId`, the user chats; session state is written to `$COPILOT_HOME/anon/{userId}/`.
2. **Click "Connect Azure"** — OAuth callback derives `userId` from `oid`, migrates in-memory state, writes `identity.json` + sets `finops_id` cookie. The conversation already in flight keeps its sessionId; future sessions go under `…/users/{oid}/`.
3. **Container restart / new browser on another device** — cookie arrives → hydration middleware decrypts it, loads `identity.json`, restores session blobs. Sidebar fetches `/api/sessions`, shows all the user's past chats. Picking one rehydrates via `ResumeSessionAsync` with a freshly minted bearer.
4. **Token expiry mid-conversation** — proactive recycle kicks in 10 min before expiry, swaps the live wrapper for one with a new bearer; user sees nothing.
5. **30-day idle** — janitor sweeps the on-disk state away.

## 12. Why this design and not SQLite / Cosmos

- **Zero new infrastructure.** App Service `/home` is already an Azure Files mount that survives restarts, scale-up, and slot swaps. No new dependency, no new RBAC, no new failure mode.
- **No locking surprises.** SQLite over SMB is famously bad. Plain JSON files + per-OID `SemaphoreSlim` + atomic `File.Move` give us the same correctness without WAL pitfalls.
- **The SDK already persists conversations.** Adding our own DB just to track which sessions belong to which user would duplicate state the SDK already keeps on disk — the `Cwd` convention turns the filesystem itself into our index.
- **One surface to clean up.** Delete a user → delete their workdir → all their sessions and their identity record go with it.
