const TENANT_GUID =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const TENANT_LABEL = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i;
const TENANT_ALIASES = new Set(["common", "organizations", "consumers"]);

export function normalizeTenantIdentifier(value) {
  const tenant = String(value ?? "").trim();
  if (!tenant) return "";
  if (TENANT_GUID.test(tenant) || TENANT_ALIASES.has(tenant.toLowerCase()))
    return tenant.toLowerCase();
  if (tenant.length > 253) return "";

  // A top-level label is never all digits, which also rules out IP addresses.
  const labels = tenant.split(".");
  if (
    labels.length < 2 ||
    labels.some((label) => !TENANT_LABEL.test(label)) ||
    /^\d+$/.test(labels[labels.length - 1])
  )
    return "";

  return tenant.toLowerCase();
}

export function isValidTenantIdentifier(value) {
  return normalizeTenantIdentifier(value) !== "";
}

export function withTenantIdentifier(url, value) {
  const tenant = normalizeTenantIdentifier(value);
  if (!tenant) return url;

  const target = new URL(url, "https://local.invalid");
  target.searchParams.set("tenant", tenant);
  return `${target.pathname}${target.search}${target.hash}`;
}
