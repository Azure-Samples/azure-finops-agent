# Organizational Knowledge Base

> Persistent, per-user organizational context the Azure FinOps Agent applies
> automatically across every conversation.

This document is the **technical reference** for the feature (architecture, API,
security, token model, operations). If you just want to know **what to write**
in your knowledge base, read the
[Knowledge Authoring Guide](knowledge-authoring-guide.md) instead.

---

## 1. What problem it solves

Out of the box the agent knows Azure, but not _your_ Azure. Every conversation
started from zero: which subscription is "Production"? Who owns the `mkt-*`
resource groups? What's the RTO for the payments app? What does your fiscal year
look like? Users re-typed the same context over and over.

The Knowledge Base lets a user record that context **once**. From then on the
agent treats it as ground truth and applies it automatically — resolving
app/team names to subscriptions, honoring tagging and cost-center conventions,
referencing SLA/RTO/RPO targets, and following the user's analysis instructions
— in **every** new conversation, and it survives restarts and redeploys.

---

## 2. Architecture overview

```
┌──────────────┐   CRUD/import    ┌──────────────────────┐
│  ChatView.vue │ ───────────────▶ │ KnowledgeEndpoints.cs │
│ (sidebar UI)  │  /api/knowledge  │  (Entra-gated REST)   │
└──────────────┘                  └──────────┬───────────┘
                                             │
                                     ┌────────▼─────────┐
                                     │ KnowledgeStore.cs │  per-user JSON file
                                     │  (service layer)  │  on /home (durable)
                                     └────────┬─────────┘
                                              │
         ┌────────────────────────────────────┼───────────────────────────────┐
         │ inject                              │ lazy-pull                      │
┌────────▼─────────┐                 ┌─────────▼──────────┐                     │
│ ChatEndpoints.cs │  prepends the   │ KnowledgeTools.cs  │  QueryKnowledge     │
│ (per-turn build) │  context block  │ (read-only tool)   │  (list/search/get)  │
└──────────────────┘                 └────────────────────┘                     │
         │                                                                       │
         └──────────────────────────▶ Copilot CLI / Azure OpenAI ◀──────────────┘
```

| Component | File | Responsibility |
| --- | --- | --- |
| Service / store | `src/Dashboard/Services/KnowledgeStore.cs` | Per-user file CRUD, validation, prompt-block builder, injection de-dup |
| REST API | `src/Dashboard/Endpoints/KnowledgeEndpoints.cs` | `/api/knowledge` CRUD + `/api/knowledge/import`, Entra gating |
| Prompt injection | `src/Dashboard/AI/ChatEndpoints.cs` | Prepends the knowledge block to each turn |
| Lazy-pull tool | `src/Dashboard/AI/Tools/KnowledgeTools.cs` | Read-only `QueryKnowledge` (list/search/get) |
| Tool registration | `src/Dashboard/AI/CopilotSessionFactory.cs` | Registers `QueryKnowledge` per-user; system-prompt guidance |
| UI | `src/Dashboard/frontend/src/components/ChatView.vue` | Sidebar section, editor modal, file import |

---

## 3. Data model

A knowledge **article** (`KnowledgeArticle`):

| Field | Notes |
| --- | --- |
| `Id` | Server-generated 8-char lowercase hex (`^[a-f0-9]{8}$`) |
| `Title` | ≤ 120 chars |
| `Category` | One of `subscriptions`, `cost_centers`, `instructions`, `architecture`, `sla`, `custom` |
| `Content` | Plain text / markdown / CSV / JSON, ≤ 10,000 chars |
| `UserId` | Deterministic id derived from the Entra OID (see below) |
| `CreatedUtc` / `UpdatedUtc` | Timestamps |
| `Active` | Soft toggle — inactive articles are excluded from injection |

**Limits** (enforced in `KnowledgeStore`):

- `MaxArticlesPerUser = 20`
- `MaxArticleChars = 10,000`
- `MaxTotalChars = 50,000`
- `MaxTitleChars = 120`

### Storage

Each user's articles live in a single JSON array at:

```
$COPILOT_HOME/knowledge/{userId}/knowledge.json
```

On App Service `COPILOT_HOME` is `/home` (Azure Files), which is **encrypted at
rest** and survives container restarts, redeploys, scale operations, and slot
swaps. Writes are **atomic** (temp file + `File.Move(overwrite: true)`) and
**serialized per user** via a lock, so concurrent requests can't corrupt the
file.

### User identity

`userId` is the deterministic id derived from the user's Entra object id
(`PersistentIdentity.DeriveUserId(oid)` = first 8 bytes of `SHA256(oid)` as a
`long`). The same Entra user always maps to the same `userId`, so knowledge
follows them across sessions and devices. **Anonymous** users get a random,
ephemeral id and are intentionally **excluded** — see security below.

---

## 4. REST API

All routes are under `/api/knowledge` and require an authenticated **Entra**
session. The `userId` is always derived server-side from the session — it is
**never** read from the request.

| Method | Route | Body | Returns |
| --- | --- | --- | --- |
| `GET` | `/api/knowledge` | — | Article metadata (no content) + categories + limits |
| `GET` | `/api/knowledge/{id}` | — | Full article (incl. content) |
| `POST` | `/api/knowledge` | `{ title, category, content }` | Created article metadata |
| `PUT` | `/api/knowledge/{id}` | `{ title?, category?, content?, active? }` | Updated article metadata |
| `DELETE` | `/api/knowledge/{id}` | — | `{ deleted: true }` |
| `POST` | `/api/knowledge/import` | multipart file (`file`) + optional `category` | Created article metadata |

- Validation failures return **HTTP 400** with `{ error: "<message>" }`.
- Anonymous / non-Entra sessions return **HTTP 401**.
- Import accepts text files only (`.csv`, `.tsv`, `.txt`, `.json`, `.md`,
  `.log`); content is truncated to `MaxArticleChars`; the title defaults to the
  filename. Binary formats are rejected.

---

## 5. Prompt injection & the token model

This is the part reviewers should scrutinize, because it directly drives cost.

**Key fact:** Azure OpenAI only caches the **stable history prefix** of a
conversation. Re-sending the same content inside _every new user message_ is
**full price every turn**. So the goal is: get the knowledge into the model
**once**, then rely on cached history — and only re-send when it actually
changed.

### Tier 1 — inject once per session (always on)

`KnowledgeStore.BuildContextBlock(userId, sessionId)` is called by
`ChatEndpoints` on every turn and decides whether to emit the block:

- **Zero articles / no file** → returns `""` with **zero file I/O** (a
  `File.Exists` short-circuit). The feature is completely free until a user
  actually saves something.
- **First turn of a session** → emit the full block.
- **Subsequent turns** → emit `""` (the model already has it in cached history),
  **unless** the content hash changed or `ReinjectEveryTurns (= 10)` turns have
  passed since the last injection (a safety re-inject in case the SDK
  summarizes/truncates long histories).

The per-session state (`sessionId → (contentHash, turnsSince)`) lives in an
in-memory `ConcurrentDictionary` and is pruned opportunistically.

### Tier 2 — index + lazy-pull (large knowledge bases)

When a user's **active** content exceeds `FullInjectionCharBudget (= 4,000
chars)`, injecting everything every session would be wasteful. Instead
`BuildContextBlock` emits a compact **index**:

```
[ORGANIZATIONAL KNOWLEDGE INDEX — … call the QueryKnowledge tool to read … ]
- a1b2c3d4 · Production subscription map (subscriptions, 1,820 chars)
- e5f6a7b8 · Cost center owners (cost_centers, 2,450 chars)
…
```

The model then calls the read-only **`QueryKnowledge`** tool to pull only the
articles relevant to the current question:

- `mode=list` — the index again
- `mode=search param=<keywords>` — full text of matching articles
- `mode=get param=<id>` — one article's full text

This caps per-turn tokens at the small index plus only the articles actually
needed, instead of the entire (up to 50K-char) knowledge base.

### Why this is the simplest correct design

- No vector DB / embeddings to operate — knowledge bases are small (≤ 50K
  chars, ≤ 20 articles) and the model reads plain text well.
- No background re-indexing — the hash is computed from `id:UpdatedUtc.Ticks`,
  so edits are detected instantly and cheaply.
- The free-when-empty fast path means zero overhead for users who never use it.

---

## 6. Security & privacy

| Concern | Mitigation |
| --- | --- |
| **Authorization** | Entra-only. `userId` always derived from the session, never from the request. Anonymous sessions rejected (HTTP 401) so ephemeral ids can't strand org data. |
| **Path traversal** | `id` validated against `^[a-f0-9]{8}$` before any path use. |
| **Resource exhaustion** | Hard limits (20 articles / 10K per article / 50K total / 120-char title) enforced in the store. Import truncates oversized files. |
| **Input validation** | Category allowlist; title/content required and length-checked; invalid input → HTTP 400. |
| **Cross-tenant isolation** | Each user's data is a separate file keyed by their deterministic id. No sharing between users or tenants. |
| **Encryption at rest** | Stored on the Azure Files `/home` mount (encrypted at rest). |
| **Action safety** | Knowledge is reference text only — it cannot grant new permissions. The existing security model still bounds everything: DELETE is blocked in `HttpHelper`, and the user's RBAC is the ultimate boundary. The system prompt explicitly tells the model knowledge must never override the security model. |
| **CSRF** | Mutating routes are same-origin and protected by the SameSite session cookie. |
| **Telemetry** | Knowledge text may appear in prompt/telemetry spans, which land in the **customer's own** Application Insights (the customer owns the data end-to-end). |

### Retention

Knowledge is meant to **persist** for returning Entra users — it is **not**
auto-purged. Notably:

- **Logout ≠ account deletion.** `/auth/logout` forgets the session identity but
  does **not** delete knowledge; the same Entra user gets it back on next login.
- Users delete their own articles via the `DELETE` endpoint / sidebar UI.
- `KnowledgeStore.DeleteAllForUser(userId)` exists for a future
  account-deletion flow (hard-deletes the user's entire knowledge directory).

---

## 7. Operations

- **Where it lives in prod:** `/home/knowledge/{userId}/knowledge.json`.
- **Inspect a user's file:** the `userId` is a `long`; the file is plain JSON.
- **Backup:** covered by Azure Files backup of the `/home` mount.
- **Disable per session:** users can toggle individual articles `Active=false`
  without deleting them.
- **No new infra:** no database, queue, or external service is introduced.

---

## 8. Related docs

- [Knowledge Authoring Guide](knowledge-authoring-guide.md) — user-facing how-to.
- [`docs/knowledge-examples/`](knowledge-examples/) — copy-paste starter
  articles for each category.
- [architecture-and-security.md](architecture-and-security.md) — overall system
  architecture and security model.
