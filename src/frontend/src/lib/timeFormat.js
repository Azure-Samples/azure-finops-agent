// Short relative times for list rows. Pass `now` from a reactive clock when a
// row must keep ticking while it is on screen.

// "just now", "5m ago", "3h ago", "2d ago", "4mo ago" for past timestamps.
export function formatAgo(iso, now = Date.now()) {
  if (!iso) return "";
  const t = new Date(iso).getTime();
  if (!t) return "";
  const min = Math.round((now - t) / 60000);
  if (min < 1) return "just now";
  if (min < 60) return `${min}m ago`;
  const h = Math.round(min / 60);
  if (h < 24) return `${h}h ago`;
  const d = Math.round(h / 24);
  if (d < 30) return `${d}d ago`;
  return `${Math.round(d / 30)}mo ago`;
}

// "any moment", "in 4 min", "in 3 h", "in 2 d" for future timestamps.
export function formatUntil(iso, now = Date.now()) {
  if (!iso) return "";
  const t = new Date(iso).getTime();
  if (!t) return "";
  const diffMs = t - now;
  if (diffMs <= 30000) return "any moment";
  const min = Math.round(diffMs / 60000);
  if (min < 60) return `in ${min} min`;
  const h = Math.round(min / 60);
  if (h < 48) return `in ${h} h`;
  return `in ${Math.round(h / 24)} d`;
}
