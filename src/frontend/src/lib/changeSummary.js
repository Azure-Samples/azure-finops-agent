// A plain-language line for a held Azure change, derived only from the exact
// request the user approves (method, URL and body), never from model text.
// The card still shows the exact URL and body under it.

const MAX_PROPERTIES = 3;

function changedProperties(body) {
  let parsed;
  try {
    parsed = typeof body === "string" ? JSON.parse(body) : body;
  } catch {
    return [];
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return [];
  return Object.entries(parsed).flatMap(([key, value]) =>
    key === "properties" &&
    value &&
    typeof value === "object" &&
    !Array.isArray(value) &&
    Object.keys(value).length
      ? Object.keys(value)
      : [key],
  );
}

function listProperties(names) {
  const shown = names.slice(0, MAX_PROPERTIES);
  const rest = names.length - shown.length;
  const text =
    shown.length > 1
      ? `${shown.slice(0, -1).join(", ")} and ${shown.at(-1)}`
      : shown[0];
  return rest > 0 ? `${shown.join(", ")} and ${rest} more` : text;
}

export function describeChange({ method = "", target = "", body = "" } = {}) {
  const path = String(target).split("?")[0];
  const parts = path.split("/").filter(Boolean);
  const lower = parts.map((part) => part.toLowerCase());
  const name = parts.at(-1) || "the resource";

  const provider = lower.lastIndexOf("providers");
  const typeSegments =
    provider >= 0 && parts.length - provider >= 4
      ? [
          parts[provider + 1],
          ...parts
            .slice(provider + 2, -1)
            .filter((_, index) => index % 2 === 0),
        ]
      : [];
  const type = typeSegments.length > 1 ? typeSegments.join("/") : "";
  const group = lower.indexOf("resourcegroups");
  const resourceGroup =
    group >= 0 && group + 1 < parts.length ? parts[group + 1] : "";

  const properties = changedProperties(body);
  const verb = String(method).toUpperCase();
  const action =
    verb === "PUT"
      ? `Create or replace ${name}` +
        (properties.length ? ` with ${listProperties(properties)}` : "")
      : properties.length
        ? `Update ${listProperties(properties)} on ${name}`
        : `Update ${name}`;
  return [action, type, resourceGroup && `resource group ${resourceGroup}`]
    .filter(Boolean)
    .join(" · ");
}
