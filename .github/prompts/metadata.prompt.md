---
agent: agent
description: "Audit and apply GitHub repo metadata, settings, security toggles, and standard files for popular-OSS readiness."
---

# Refresh repo metadata

Goal: bring the GitHub repo settings, surface metadata, and standard files up to the bar of a polished public Azure-Samples / OSS project. Idempotent — safe to re-run any time.

---

## Phase 1 — Inventory current state

```pwsh
gh repo view --json nameWithOwner,description,homepageUrl,repositoryTopics,hasIssuesEnabled,hasDiscussionsEnabled,hasWikiEnabled,hasProjectsEnabled,licenseInfo,latestRelease,visibility
gh api repos/{owner}/{repo} --jq '{delete_branch_on_merge, allow_squash_merge, allow_merge_commit, allow_rebase_merge, allow_auto_merge, security_and_analysis}'
```

Substitute `{owner}/{repo}` from `git remote get-url origin`.

---

## Phase 2 — Compare against the checklist

Score every row. Mark ✅ already good, ❌ missing, ⚠️ stale.

### Repo surface (GitHub API)

| Item | Target |
|---|---|
| Description | One-liner with keywords; not just the repo name |
| Homepage URL | Live demo / docs site if one exists |
| Topics | 8–15 relevant tags (stack, domain, org, framework) |
| Discussions | Enabled |
| Wiki | Disabled when `/docs` is the source of truth |
| Projects | Disabled unless actively used |
| Latest release | Exists and matches `CHANGELOG.md` head |
| Social preview image | Set (1280×640 PNG/JPG; cannot be done via API — web UI only) |

### Merge & branch hygiene

| Item | Target |
|---|---|
| `delete_branch_on_merge` | `true` |
| `allow_squash_merge` | `true` |
| Branch protection on `main` | Optional — enable only if the team is ready for PR-only workflow |

### Security & analysis

| Item | Target |
|---|---|
| Dependabot security updates | enabled |
| Secret scanning | enabled |
| Secret scanning push protection | enabled |
| Secret scanning AI detection | enabled |
| Secret scanning non-provider patterns | enabled |
| Secret scanning validity checks | enabled |
| CodeQL workflow (`.github/workflows/codeql.yml`) | exists + green |

### Standard files (root)

| File | Notes |
|---|---|
| `README.md` | Badges (License, Latest release, language/runtime, demo, Open issues, Last commit), one-screen value prop, "Try without signing in", "How it works", architecture link |
| `LICENSE` | MIT (or correct license) |
| `CHANGELOG.md` | Keep-a-Changelog, current `[Unreleased]` block at top |
| `CONTRIBUTING.md` | Up-to-date paths, branch convention, dev setup that actually works |
| `CODE_OF_CONDUCT.md` | Microsoft OSS CoC for org repos |
| `SECURITY.md` | Microsoft `SECURITY.md V0.0.9` block + MSRC link |
| `SUPPORT.md` | Issue / discussion link, scope of support |
| `.editorconfig` | Indentation + EOL discipline |
| `.gitattributes` | Line-ending normalization, binary marking, generated-file flagging |
| `global.json` (if .NET) | Pin SDK version + `rollForward` policy |
| `.github/CODEOWNERS` | Owners for review routing |
| `.github/PULL_REQUEST_TEMPLATE.md` | Default PR template |
| `.github/ISSUE_TEMPLATE/` | Bug + feature templates |
| `.github/dependabot.yml` | Updates for npm + nuget + GH actions + docker |
| `.github/workflows/` | Build/test on PRs at minimum |

---

## Phase 3 — Apply the fixes

Show the diff plan first (one bullet per change). Get user approval if any change is destructive (e.g. disabling Wiki when content might exist there, or turning on branch protection).

### Repo metadata via `gh repo edit`

```pwsh
gh repo edit Azure-Samples/azure-finops-agent `
  --description "<one-liner>" `
  --enable-discussions `
  --enable-wiki=false `
  --enable-projects=false `
  --add-topic <topic1> --add-topic <topic2> ...
```

### Merge hygiene + security toggles via `gh api`

```pwsh
gh api -X PATCH repos/{owner}/{repo} `
  -F delete_branch_on_merge=true `
  -F security_and_analysis[secret_scanning_ai_detection][status]=enabled `
  -F security_and_analysis[secret_scanning_non_provider_patterns][status]=enabled `
  -F security_and_analysis[secret_scanning_validity_checks][status]=enabled
```

> Note: `gh repo edit` does not currently expose secret scanning sub-toggles — fall back to `gh api -X PATCH` with the `security_and_analysis` object.

### Standard files

For each missing or stale file, edit/create with the canonical content. Common gotchas:

- `CONTRIBUTING.md` and `README.md` drift fastest — grep both for stale folder names (e.g. `client/` vs `frontend/`, `Web/` vs `Endpoints/`) and stale config paths (`appsettings.Local.json` vs `dotnet user-secrets`).
- `.gitattributes` may already have project-specific rules (e.g. `merge=union` lines for state files) — append, do not overwrite.
- README badges: avoid more than 7 — they go stale and look spammy. Pick License, Latest release, runtime, demo, Open issues, Last commit.

### CodeQL workflow

If `.github/workflows/codeql.yml` is missing, generate from the [GitHub-recommended template](https://github.com/github/codeql-action) — pick the languages that match the repo (e.g. `csharp`, `javascript-typescript`).

---

## Phase 4 — Verify

```pwsh
gh repo view --json description,repositoryTopics,hasDiscussionsEnabled,hasWikiEnabled
gh api repos/{owner}/{repo} --jq '{delete_branch_on_merge, security_and_analysis}'
```

Confirm every row from Phase 2 now shows the target value. Flag anything that still requires the web UI (currently: **social preview image upload** is the only repo-settings item with no API surface).

---

## Phase 5 — Commit + report

Commit any file changes with a single message, e.g.:

```
chore(meta): refresh repo metadata + standard files

- enable Discussions, disable Wiki + Projects, set delete_branch_on_merge
- expand secret scanning (AI detection, non-provider patterns, validity checks)
- add global.json + .editorconfig
- refresh README badges; fix stale paths in CONTRIBUTING
- add CodeQL workflow
```

Push to `main` (no tag — this is metadata only, not a release).

If the social preview image is still unset, end the run by telling the user: *"Upload a 1280×640 PNG at https://github.com/{owner}/{repo}/settings — there is no API for it."*

---

## Guardrails

- **Admin permission required** for repo settings edits. If `gh api -X PATCH` returns `404 Not Found`, your role is `WRITE`, not `ADMIN` — stop and ask the user to elevate JIT admin via the Microsoft Open Source Management portal.
- **Do not** turn on branch protection on `main` without explicit user opt-in — it changes their daily workflow.
- **Do not** disable Wiki if it has content (`gh api repos/{owner}/{repo}/pages` won't tell you, but you can check the GraphQL `wikiUrl` for content). Ask first.
- **Do not** rewrite Microsoft templated files (`SECURITY.md` MSRC block, `CODE_OF_CONDUCT.md`) — they are deliberately boilerplate.
