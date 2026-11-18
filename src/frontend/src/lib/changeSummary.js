// A plain-language line for a held change, derived only from the exact
// request the user approves (method, URL and body), never from model text.
// The card still shows the exact URL, If-Match and body under it.

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

// Microsoft 365 Copilot cost management changes go to Microsoft Graph: the operation comes from the URL, and a credit
// request decision, a service's state or a new policy's name from the body that is sent.
const COPILOT_CHANGE =
  /^https:\/\/graph\.microsoft\.com\/(?:v1\.0|beta)\/copilot\/costManagement\/(.+)$/i;
const POLICY = "a Copilot spending policy";

function bodyObject(body) {
  try {
    const parsed = typeof body === "string" ? JSON.parse(body) : body;
    return parsed && typeof parsed === "object" && !Array.isArray(parsed)
      ? parsed
      : {};
  } catch {
    return {};
  }
}

// A policy's monthly limits: perUserLimit for each user, periodLimit for all its users together.
function describeLimit(name, quantity) {
  const type = String(quantity?.["@odata.type"] ?? "").toLowerCase();
  const perUser = name === "perUserLimit";
  if (type.includes("unlimited"))
    return perUser ? "remove the per-user limit" : "remove the monthly limit";
  const value = Number(quantity?.value);
  if (!type.includes("limited") || !Number.isFinite(value)) return null;
  const credits = `${value.toLocaleString("en-US")} Copilot Credits a month`;
  return perUser
    ? `limit each user to ${credits}`
    : `limit all its users together to ${credits}`;
}

function describeCopilotChange(verb, operation, body) {
  const parts = operation.split("/").filter(Boolean);
  const lower = parts.map((part) => part.toLowerCase());
  const fields = bodyObject(body);
  if (lower[0] === "creditrequests") {
    if (lower[1] === "batchrecorddecisions") {
      const items = Array.isArray(fields.requests) ? fields.requests : [];
      const count = (state) =>
        items.filter((item) => item && item.state === state).length;
      const decisions = [
        count("approved") && `approve ${count("approved")}`,
        count("rejected") && `reject ${count("rejected")}`,
      ].filter(Boolean);
      if (!decisions.length) return "Decide Copilot credit requests";
      const text = `${decisions.join(" and ")} Copilot credit request${items.length === 1 ? "" : "s"}`;
      return text[0].toUpperCase() + text.slice(1);
    }
    if (fields.state === "approved") return "Approve a Copilot credit request";
    if (fields.state === "rejected") return "Reject a Copilot credit request";
    return "Update a Copilot credit request";
  }
  if (lower[0] !== "spendingpolicies") return "Change Copilot cost management";
  if (parts.length === 1)
    return typeof fields.displayName === "string" && fields.displayName
      ? `Create the Copilot spending policy ${fields.displayName}`
      : "Create a Copilot spending policy";
  if (lower.includes("assignedgroups")) return `Add a group to ${POLICY}`;
  if (lower.includes("recipients"))
    return `Add an alert recipient to ${POLICY}`;
  if (lower[2] === "emailthresholdsettings")
    return `Change the alerts of ${POLICY}`;
  if (lower[2] === "servicesettings") {
    if (verb === "POST") return `Add a service to ${POLICY}`;
    const service = parts[3] || "a service";
    return fields.state === "disabled"
      ? `Turn off ${service} in ${POLICY}`
      : `Update ${service} in ${POLICY}`;
  }
  const properties = Object.keys(fields);
  const limits = properties.filter(
    (name) => name === "perUserLimit" || name === "periodLimit",
  );
  if (limits.length && limits.length === properties.length) {
    const phrases = limits.map((name) => describeLimit(name, fields[name]));
    if (phrases.every(Boolean)) {
      const text = `${phrases.join(" and ")} on ${POLICY}`;
      return text[0].toUpperCase() + text.slice(1);
    }
  }
  return properties.length
    ? `Update ${listProperties(properties)} on ${POLICY}`
    : `Update ${POLICY}`;
}

export function describeChange({ method = "", target = "", body = "" } = {}) {
  const path = String(target).split("?")[0];
  const copilot = path.match(COPILOT_CHANGE);
  if (copilot)
    return describeCopilotChange(String(method).toUpperCase(), copilot[1], body);
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
