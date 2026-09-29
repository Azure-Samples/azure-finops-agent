# Persistent Multi-Session Chat — Technical Description

Current execution, approval, artifact and outcome contracts are documented in [agent reliability](agent-reliability.md). This document retains the original persistence design context; coordination requires one active application instance.

## 1. Purpose

Before this change set, the Azure FinOps Agent had a **single live conversation per browser session**. Closing the tab, redeploying the container, or being idle past the 30-minute session timeout meant the user lost their chat history and had to re-consent to Azure / Graph / Log Analytics on the next visit.

The goal of this work is to give every user — anonymous or Entra-authenticated — **multiple long-lived conversations** that survive container restarts, slot swaps, OAuth token expiry, and 24-hour absences, **without re-prompting for OAuth consent**. Entra users additionally get cross-device continuity (sign in on a new browser → see the same conversations).

## 2. Architecture at a glance

Three independent persistence layers cooperate:

| Layer                           | What it stores                                                          | Where                                                                               | Lifetime                            |
| ------------------------------- | ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------- | ----------------------------------- |
| **Conversation state**          | `session.json` (serialized Agent Framework `AgentSession` + listing metadata) and the UI transcript `events.jsonl` | `{workdir}/sessions/{sessionId}/` (Azure Files `/home`)                              | Until explicit delete or 30-day TTL |
| **Per-user workdir**            | Owner directory; a conversation is visible only under its owner's workdir | `$AGENT_HOME/users/v2/{sha256(tid,oid)}` (Entra) or `$AGENT_HOME/anon/{userId}` | Same as conversation state          |
| **`PersistentIdentity` record** | Encrypted `oid`, `tenantId`, derived `userId`, refresh token, GraphTier | Principal workdir `identity.json` (DataProtection-encrypted) + `finops_id` cookie   | 30 days, sliding                    |

The combination is what makes restart-survivable login possible: the cookie tells us _who_ the user is, the identity record gives us a fresh access token (via the persisted refresh token), and the conversation's saved `AgentSession` resumes the model context on the next prompt. The model context itself is the service-stored response chain (retained about 30 days); only its latest response ID is stored locally.

## 3. Identity & user-id derivation

`Auth/PersistentIdentity.cs` is the single source of truth for "who is this caller, and how do I prove it across restarts".

### 3.1 Deterministic `userId` from the Entra principal

```csharp
public static long DeriveUserId(string tenantId, string oid)
    => BitConverter.ToInt64(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{tenantId.ToLowerInvariant()}\n{oid.ToLowerInvariant()}")), 0);
```

The legacy `userId` was a random `long` minted per browser session. Persistence demands a stable value, while multi-tenant authorization requires the validated `tid + oid` pair because an object ID is not globally unique across tenants. Hashing the pair gives:

- **Stability** — the same tenant/object pair always produces the same `userId`, so workdirs, telemetry-keyed dictionaries, and token caches line up after a restart.
- **Cross-tenant safety** — the same OID in two tenants produces different owner IDs and workdirs.
- **Backward compatibility** — anonymous users keep the old random `long`, so nothing about their code path changed.

### 3.2 Encrypted identity file + signed cookie

- The record is serialized to JSON, encrypted with ASP.NET Core **DataProtection** (`protector scope = "FinOps.Identity.v1"`), and written atomically to `identity.json`.
- A companion `finops_id` cookie (HttpOnly, Secure, SameSite=Lax, 30-day) holds the encrypted versioned `tid + oid` pointer, never plaintext.
- An old OID-only directory is reused only when its encrypted identity record attests the same tenant/object pair. A different tenant with the same OID receives a new v2 directory and cannot list, read, select, or delete the legacy sessions.
- DataProtection keys themselves are persisted to `/home/dataprotection-keys/` (see `Program.cs`) so that a container restart doesn't invalidate every cookie in the wild.

### 3.3 Atomic, lock-protected writes

```csharp
private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
private static SemaphoreSlim LockFor(string principalKey) => _fileLocks.GetOrAdd(principalKey, _ => new(1, 1));

private static void AtomicWrite(string path, byte[] bytes)
{
    var tmp = path + ".tmp";
    File.WriteAllBytes(tmp, bytes);
    File.Move(tmp, path, overwrite: true);   // atomic on POSIX
}
```

- A per-principal `SemaphoreSlim` serializes concurrent `SaveIdentity` / `UpdateRefreshToken` / `UpdateGraphTier` calls (otherwise a refresh-token rotation racing with a tier-consent could corrupt the file).
- All mutations write `identity.json.tmp` first, then `File.Move(..., overwrite: true)`, which is atomic on the Linux filesystem App Service mounts. A crash mid-write leaves either the old file or a discardable `.tmp` — never a half-written `identity.json`.

### 3.4 The `UpdateRecord(tid, oid, mutate)` helper

`UpdateRefreshToken` and `UpdateGraphTier` both go through:

```csharp
private void UpdateRecord(string tenantId, string oid, Action<IdentityRecord> mutate) { /* lock + load + mutate + AtomicWrite */ }
```

which keeps the lock + atomic-write logic in exactly one place and guarantees we never overwrite the file with stale fields when only one column changed.

## 4. The hydration middleware

In `Program.cs`, before any user-bootstrap logic runs, a middleware tries `persistentIdentity.Load(ctx)`:

- **Hit** — the cookie decrypted to a known tenant/object pair and the matching identity file exists. We restore the four session blobs the rest of the pipeline expects (`user`, `azure_user`, `azure_refresh_token`, `graph_tier`).
- **Miss** — fall back to the legacy random anon id. New anonymous user, no surprises.

This is the _only_ place that touches the identity file on the read path; the rest of the app reads tokens out of the ASP.NET session as it always did.

## 5. The OAuth callback: identity migration & GraphTier persistence

`Auth/MicrosoftAuthEndpoints.cs` was extended so each Entra callback does three things in addition to its existing token exchange:

1. **Establish the principal owner**. After the `id_token` is validated, derive `newUserId = DeriveUserId(tid, oid)`. Account switching compares both claims, including the same OID appearing under a different tenant. Anonymous session pointers and tool bindings are not reassigned across owners; the connected principal starts in its own workdir.
2. **Persist the rotating refresh token + the current GraphTier**:
   ```csharp
   if (!string.IsNullOrEmpty(refreshToken))
       persistentIdentity.SaveIdentity(ctx, new IdentityRecord { Oid = oid, ..., RefreshToken = refreshToken, GraphTier = ctx.Session.GetString("graph_tier") });
   else
    persistentIdentity.UpdateGraphTier(tid, oid, ctx.Session.GetString("graph_tier"));
   ```
   The `else` branch covers the re-consent edge case where Entra returns no fresh refresh token but the user just added a new add-on — without it, post-restart hydration would forget the new add-on consent.
3. **Logout** clears the cookie and exact principal record via `persistentIdentity.Clear(ctx, tid, oid)`.

## 6. Token store

`Auth/SessionTokenStore.ExchangeRefreshTokenForResource` returns `(Token, Expiry, RotatedRefreshToken?)`. On rotation it updates the exact tenant/object record, so another tenant sharing an OID can never receive that refresh token.

## 7. Multi-conversation runtime

`AI/AgentSessionFactory.cs` owns the singleton Agent Framework agent and the per-user conversation index; `AI/Runtime/AgentConversation.cs` runs one conversation's turns. Key invariants:

### 7.1 Workdir as ownership marker

Every `AgentConversation` lives under the principal-owned Entra workdir or `…/anon/{userId}`, so listing conversations means listing only the caller's authorized directory. Existing OID-only workdirs remain available solely to the pair attested by their encrypted identity record.

### 7.2 Live-vs-disk distinction (`AiTelemetry.LiveSessions`)

`AiTelemetry.LiveSessions` is a `ConcurrentDictionary<sessionId, LiveSessionInfo>` containing only sessions currently held open in memory. `LiveSessionInfo` carries the `AgentConversation` instance and the `UserId` (init-only). The janitor evicts idle users' entries; the next prompt reopens the conversation from disk.

### 7.3 Model credentials and chain recovery

The project client takes a `TokenCredential` (managed identity in Azure), which refreshes tokens on demand; no conversation holds a model token. The response chain advances only after a successful turn, so a stopped or failed turn leaves the previous response ID and no unanswered tool calls. An expired chain resets to a fresh model context while the visible transcript remains. Genuinely unavailable history returns `history_unavailable` rather than a fabricated empty transcript.

### 7.4 IDOR guard with graceful fallback

`UserOwnsSessionAsync(userId, tid, oid, sessionId)` checks ownership against the caller's principal-owned workdir. Chat, select, delete, replay, stop, activity/outcome probes, and write approvals all use this guard and return the same not-found result for foreign and missing sessions.

### 7.5 Listing & path comparison

`ListAllManagedSessions` (used by the janitor) enumerates only `$AGENT_HOME/users` and `…/anon`. Anything outside those two roots is some other component's state and we leave it alone.

### 7.6 Title generation

On the first user/assistant exchange a separate low-effort title agent returns a short title.

## 8. The `/api/sessions` REST surface

`Endpoints/SessionEndpoints.cs` exposes:

| Method   | Path                          | Purpose                                                                                                                                                                                                                          |
| -------- | ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GET`    | `/api/sessions`               | List the caller's conversations (from the caller's workdir). **Anon users get an empty list** — their `userId` is randomized per browser session, so they could never re-find old chats anyway; the sidebar is intentionally Entra-only. |
| `POST`   | `/api/sessions/new`           | Force-create a new conversation and make it current.                                                                                                                                                                             |
| `POST`   | `/api/sessions/{id}/select`   | Switch the user's "current" pointer (with IDOR check).                                                                                                                                                                           |
| `GET`    | `/api/sessions/{id}/messages` | Owner-checked history fetch with an explicit unavailable-history response.                                                                                                                                                     |
| `GET`    | `/api/sessions/{id}/outcomes` | Owner-checked durable execution outcomes; normal chat fulfillment remains unevaluated.                                                                                                                                           |
| `DELETE` | `/api/sessions/{id}`          | Rejects an active turn, deletes the local conversation, and only then clears host state. Failures propagate instead of returning false success.                                                                                  |

The chat SSE endpoint (`AI/ChatEndpoints.cs`) accepts an optional `sessionId` to resume a specific conversation, threads it through the IDOR check, and sends a `session` SSE event so the frontend can retain the active id in `sessionStorage`. Stop cancels the run's token, and the turn gate stays held until the run reports a terminal state. A browser disconnect alone does not cancel the turn.

## 9. TTL janitor

`Auth/UserStateJanitor.cs` is a `BackgroundService` that evicts idle users' in-memory state every 10 minutes and, every 6 hours, uses `ListAllManagedSessions` to find conversations not modified for 30 days, calling `DeleteSessionAsync` to remove them. The scope is deliberately narrow — only `users/` and `anon/` — so the janitor can never accidentally delete state from a co-located component sharing the Azure Files mount.

## 10. Frontend (`ChatView.vue`)

The Vue chat UI lists the user's conversations beside the chat. Deletion is a single click without a confirmation step and keeps the row stable while the server responds. A failed delete remains visible with an error; only a confirmed `204`/`404` removes the row and current transcript state. Running conversations must be stopped first.

## 11. End-to-end flow after these changes

1. **First visit, anon** — middleware finds no cookie, mints a random anon `userId`, the user chats; session state is written to `$AGENT_HOME/anon/{userId}/`.
2. **Click "Connect Azure"** — OAuth callback derives `userId` from `tid + oid`, migrates in-memory state, writes `identity.json`, and sets a pair-bound `finops_id` cookie. Future sessions use the principal-owned workdir.
3. **Container restart / new browser on another device** — cookie arrives → hydration middleware decrypts it, loads `identity.json`, restores session blobs. Sidebar fetches `/api/sessions`, shows all the user's past chats. Picking one reopens it from disk and resumes its saved model context.
4. **Token expiry mid-conversation** - the credential obtains a fresh token for the next model request without replacing the conversation.
5. **30-day idle** — janitor sweeps the on-disk state away.

## 12. Why this design and not SQLite / Cosmos

- **Zero new infrastructure.** App Service `/home` is already an Azure Files mount that survives restarts, scale-up, and slot swaps. No new dependency, no new RBAC, no new failure mode.
- **No locking surprises.** SQLite over SMB is famously bad. Plain JSON files + per-principal `SemaphoreSlim` + atomic `File.Move` give us the same correctness without WAL pitfalls.
- **The filesystem is the index.** Each conversation is a directory under its owner's workdir, so ownership, listing and deletion need no separate database.
- **One surface to clean up.** Delete a user → delete their workdir → all their sessions and their identity record go with it.
