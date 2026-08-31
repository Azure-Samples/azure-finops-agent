---
agent: agent
description: "Plan a generic rebuild-and-cutover of Azure FinOps Agent to another tenant without committing deployment coordinates."
---

# Migrate Azure FinOps Agent to another tenant

Azure resources cannot generally be moved across Entra tenants. Treat this as a **rebuild, verify, and cut over** operation. Keep all real values in the current shell or deployment environment; never write them into tracked files.

## Required inputs

Collect and confirm:

- Source and target tenant/subscription
- Source deployment and DNS scopes
- Target `azd` environment name and region
- Custom domain, registrar/DNS provider, and current DNS TTL
- Required retention for telemetry, files, sessions, and job history

Use placeholders such as `<SOURCE_SUBSCRIPTION_ID>`, `<SOURCE_RESOURCE_GROUP>`, and `<CUSTOM_DOMAIN>` in notes or proposed commands.

## 1. Understand non-transferable state

- Entra app registrations must be recreated; users must re-consent.
- Refresh tokens are tenant-bound and cannot be migrated.
- App Service `/home` state and Data Protection keys require an explicit migration decision.
- Historical Application Insights data remains in the source workspace unless exported separately.
- Managed certificates must be reissued for the target app.

## 2. Provision the target

1. Confirm the active target account.
2. Register required resource providers.
3. Create a new `azd` environment.
4. Set custom-domain and optional infrastructure settings with `azd env set`.
5. Run `azd up`.
6. Capture generated outputs only in the `azd` environment or current shell.

## 3. Verify before cutover

On the target app's generated hostname:

- Check `/api/version`.
- Complete Entra sign-in and consent.
- Run a real chat/tool turn.
- Render a chart and test generated downloads.
- Confirm monitoring and availability probes.
- Verify managed identity and least-privilege role assignments.

Do not modify public DNS until these checks pass.

## 4. Prepare a reversible DNS cutover

1. Export the current DNS record sets and TTLs.
2. Add target hostname-verification records.
3. Bind the custom hostnames to the target app.
4. Pre-stage a valid certificate using a method compatible with the current DNS provider.
5. Record source and target values in an untracked operational worksheet.
6. Define rollback as restoring the exported source records.

Never place addresses, verification tokens, resource names, or DNS zone coordinates in this prompt.

## 5. Cut over

- Lower TTL ahead of the maintenance window when appropriate.
- Update the authoritative A/AAAA/CNAME records to the target values.
- Validate DNS from multiple resolvers.
- Validate HTTPS, certificate chain/expiry, redirects, security headers, canonical/noindex behavior, `/api/version`, sign-in, and a representative chat turn.
- Keep the source deployment and DNS zone intact through the agreed soak period.

Move DNS hosting or registrar nameservers only as a separate, later phase after the application cutover is stable.

## 6. Post-cutover

- Update target Entra redirect URIs using the confirmed custom and generated hostnames.
- Confirm availability tests and alerts.
- Rotate or remove obsolete credentials outside the application repository.
- Export any retained source data.
- Decommission source resources only with a separate explicit owner approval and reviewed destructive plan.

## Guardrails

- Never commit real deployment coordinates or secrets.
- Never delete source resources during cutover.
- Never claim success from DNS resolution alone; verify the application and identity flows.
- Prefer managed identity/OIDC and least-privilege scopes.
- Stop on ambiguity rather than guessing the source or target.
