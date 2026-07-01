---
agent: agent
description: "Analyze changes since the last tag, pick the right semver bump, update CHANGELOG, tag, push, and publish a GitHub release."
---

# Cut a release

Goal: produce a clean, user-facing GitHub release for whatever has shipped to `main` since the last tag. The agent decides the version number, writes the notes, and ships the tag + release end-to-end.

---

## Phase 1 — Discover the last release

```pwsh
gh release view --json tagName,name,publishedAt,url
```

Capture `tagName` (e.g. `v0.2.0`). If there is no release yet, treat the baseline as `v0.0.0` and start at `v0.1.0`.

---

## Phase 2 — Inventory the changes

Run in parallel:

```pwsh
git log <lastTag>..HEAD --oneline
git diff <lastTag>..HEAD --stat | Select-Object -Last 80
```

Skim the commit subjects + the file-change summary. Pull in `read_file` only when a commit subject is too cryptic to classify.

---

## Phase 3 — Pick the semver bump

Apply [SemVer 2.0.0](https://semver.org/spec/v2.0.0.html) **from the user's perspective**:

| Bump      | When                                                                                                                                                       |
| --------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **MAJOR** | Breaking change a deployer/integrator would feel: removed/renamed public endpoint, incompatible config schema, dropped OAuth scope, removed/renamed tool, framework swap (e.g. SDK contract change). |
| **MINOR** | New feature, new tool, new endpoint, new UI surface, notable behaviour change that is backward-compatible.                                                 |
| **PATCH** | Bug fixes, doc tweaks, dependency bumps, internal refactors, perf wins with no behaviour change.                                                           |

Rules:

- Pre-1.0 (`0.x.y`): treat **MINOR** as the "new features" lane; reserve a `0.x → 0.(x+1)` bump for anything a consumer would notice. Patch (`0.x.y → 0.x.(y+1)`) is for fix-only batches.
- CI/CD changes, folder renames, dependabot bumps, lockfile churn, internal test changes → **never** drive the bump on their own. They are PATCH at most.
- If unsure between two levels, pick the higher one.

State the chosen version + one-sentence justification before editing anything.

---

## Phase 4 — Draft user-facing notes

Group commits into these sections (omit any that are empty):

- **Highlights** — 3-6 bullets, lead with the most user-visible wins.
- **Added** — new features, new tools, new endpoints, new UI.
- **Changed** — behaviour or UX changes that are not bugs.
- **Fixed** — bug fixes the user would notice.
- **Removed** — anything taken out.
- **Security** — auth, permissions, CSP, headers, hardening.

**Exclude** from the user-facing notes:

- CI/CD pipeline edits, GitHub Actions workflow renames, badge tweaks.
- Dependabot version bumps unless they fix a CVE or change runtime behaviour.
- Lockfile-only changes, `.gitignore`, formatter runs.
- Pure folder renames / refactors with no observable change.
- Pitch deck / slide content edits.
- Anything tagged `chore(deps)`, `chore(ci)`, `ci:`, `perf(ci)`, `docs(contrib)` unless it changes how a consumer uses the repo.

Style:

- Backtick filenames, tool names, endpoints, and config keys.
- No emoji-only bullets — short emoji prefix per section heading is fine (`✨`, `🔐`, `💬`, `🧰`, `🛠`, `📚`).
- Never invent features that are not in the diff. If a commit subject is ambiguous, read the diff before claiming it.

End the notes with:

```
**Full changelog**: https://github.com/Azure-Samples/azure-finops-agent/compare/<lastTag>...<newTag>
```

---

## Phase 5 — Update `CHANGELOG.md`

- Replace the `[Unreleased]` block with `[<newVersion>] - <yyyy-mm-dd>` followed by Added / Changed / Fixed / Removed / Security sections (Keep-a-Changelog format — same shape as the existing `[0.1.0]` and `[0.2.0]` entries).
- Insert a fresh empty `## [Unreleased]` header above it.
- Update the link references at the bottom:
  ```
  [Unreleased]: https://github.com/Azure-Samples/azure-finops-agent/compare/<newTag>...HEAD
  [<newVersion>]: https://github.com/Azure-Samples/azure-finops-agent/compare/<lastTag>...<newTag>
  ```
- Keep all prior version entries and link references intact.

---

## Phase 6 — Show the user the plan and confirm

Post a single message containing:

1. Chosen version + justification.
2. The full release notes draft (markdown, ready to paste).
3. The exact commands you are about to run (Phase 7).

Then ask: **"Ship it?"** Wait for explicit approval. Do not proceed on silence or ambiguous replies.

---

## Phase 7 — Ship it

Once approved, run sequentially (stop on first non-zero exit):

```pwsh
git add CHANGELOG.md
git commit -m "docs: changelog for <newVersion>"
git push
git tag -a <newTag> -m "<newTag>"
git push origin <newTag>
```

Then create the GitHub release. Write the notes to a temp file so multi-line markdown survives PowerShell quoting:

```pwsh
$notes = @'
<paste the full release notes body here>
'@
$notesPath = New-TemporaryFile
Set-Content -Path $notesPath -Value $notes -Encoding utf8
gh release create <newTag> --title "<newTag> — <one-line headline>" --notes-file $notesPath
Remove-Item $notesPath
```

Verify:

```pwsh
gh release view <newTag> --json tagName,name,url
```

Report the live release URL.

---

## Guardrails

- Do **not** push to `main` if the working tree has unrelated uncommitted changes — show `git status`, ask the user how to proceed.
- Do **not** force-push, do **not** delete or move existing tags.
- Do **not** trigger a deploy. Releases are docs + tag only; deployment is `deploy.prompt.md`.
- If `gh` is not authenticated, stop and tell the user to run `gh auth login`.
