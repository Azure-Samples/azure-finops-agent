Safely sync local main with upstream/main while preserving local changes (both committed and uncommitted).

## Primary Rules (Safety — Non-negotiable)

1. **Never destroy local changes** — Always preserve uncommitted or unpushed work
2. **Always preview before executing** — Show diffs/logs before running commands
3. **Fail safely** — If uncertain, ask the user rather than proceeding

## Secondary Rules (Workflow — Follow when possible)

1. Prefer `git fetch` + `git merge` over `git pull`
2. Use `--no-ff` to preserve branch history
3. Stash untracked files only with explicit user confirmation - require the user to type 'yes', no ther ways are accepted.

## Tertiary Rules (Optimization — Apply if no conflicts with above)

1. Automate repetitive steps
2. Suggest cleanup of merge artifacts
3. Provide rollback commands

## Conflict Resolution

If rules from different tiers conflict, use the following guidelines to prioritize:
- **Always prefer Primary over Secondary or Tertiary**
- When two Secondary rules conflict, ask the user to decide
- Never skip a Primary rule to optimize for Secondary/Tertiary

## Edge Cases (Decision Tree)

| Scenario | Primary Rule | Action |
|----------|--------------|--------|
| Uncommitted changes + upstream conflicts | Preserve local | Stash, sync, pop |
| Force push required | Never destroy | Refuse + explain alternatives |
| Detached HEAD state | Preview first | Show current state, ask to proceed |
| Upstream branch missing| Notify the user and suggest creating a new upstream branch or reconfiguring the remote|

Final report format:
- What changed (branch alignment ahead/behind counts).
- Whether uncommitted local changes were preserved.
- Whether push is required next.
- Exact next command(s), minimal and safe.
