---
mode: agent
description: Compact security audit — how secure is this solution?
---

# Security Audit

Audit this solution end-to-end and report **how secure it is**. Be terse. List findings as `[SEV] file:line — issue → fix`. Severity: **C** / **H** / **M** / **L** / **I**.

Cover, in order:

1. **Session & identity** — `finops_id` cookie (HttpOnly/Secure/SameSite, DataProtection-signed, no plaintext OID), DataProtection keys persisted to `/home`, `identity.json` atomic + per-OID lock, refresh token at rest encrypted + rotated, logout fully clears server + cookie + disk, anon→Entra migration race-free.
2. **Multi-session IDOR** — every `/api/sessions/*` and `/api/chat` `sessionId` path goes through `UserOwnsSessionAsync` (Cwd `Ordinal` prefix match, trailing-slash safe) before read/write/delete/resume/transcript; janitor scoped to `users/`+`anon/` only.
3. **OAuth / Entra** — `state` random + single-use + session-bound, `nonce` checked against `id_token`, JWKS signature + `iss`/`aud`/`exp`, exact `redirect_uri` allowlist (no open redirect), incremental consent uses explicit scopes (no `.default`).
4. **Tool surface** — `AzureQueryTools` blocks DELETE + mutating POST allowlist enforced, `GraphQueryTools` GET-only, `LogAnalyticsQueryTools` query-only, `UploadedFileTools` path-sandboxed (no `..` / symlink escape, size + type checked), no SSRF (block `169.254.169.254`, loopback, RFC1918), `HttpHelper` never logs Authorization or token bodies.
5. **Transport & headers** — HSTS, CSP (no `unsafe-inline` / `unsafe-eval` unless justified), `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, anti-CSRF on state-changing routes.
6. **Secrets** — none in committed config / logs / traces / errors; `client_secret` only in fallback path; App Insights conn-string handled appropriately.
7. **OWASP Top 10 sweep** — note any A01–A10 hits.

## Output

```
## Findings (n)
[C] ...
[H] ...
...

## Clean
- ...
```

End with one line: **Verdict: READY / READY-WITH-FIXES / BLOCK-DEPLOY**.
