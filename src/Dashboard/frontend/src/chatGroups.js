// Groups conversations by their last activity (Today, Yesterday, Previous 7
// days, Older), newest first, after an optional case-insensitive title filter.

const DAY = 24 * 60 * 60 * 1000;

export const UNTITLED_CHAT = "Untitled conversation";

function activity(session) {
  const time = new Date(session.modified || session.started || 0).getTime();
  return Number.isFinite(time) ? time : 0;
}

export function groupChats(sessions, query = "", now = new Date()) {
  const needle = query.trim().toLocaleLowerCase();
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const groups = [
    { key: "today", label: "Today", from: today },
    { key: "yesterday", label: "Yesterday", from: today - DAY },
    { key: "week", label: "Previous 7 days", from: today - 7 * DAY },
    { key: "older", label: "Older", from: -Infinity },
  ].map((group) => ({ ...group, sessions: [] }));
  const matches = (sessions || []).filter(
    (session) =>
      !needle ||
      (session.summary || UNTITLED_CHAT).toLocaleLowerCase().includes(needle),
  );
  for (const session of [...matches].sort((a, b) => activity(b) - activity(a)))
    groups.find((group) => activity(session) >= group.from).sessions.push(session);
  return groups
    .filter((group) => group.sessions.length)
    .map(({ key, label, sessions: items }) => ({ key, label, sessions: items }));
}

// A conversation appears as soon as its turn starts, named by the first line of
// its question until the title generated from it arrives. Returns the updated
// list, or null when the conversation already has a name.
export function withQuestionTitle(sessions, id, question, now = new Date()) {
  const list = sessions || [];
  const index = list.findIndex((session) => session.id === id);
  if (
    index >= 0 &&
    list[index].summary?.trim() &&
    !/^untitled/i.test(list[index].summary)
  ) return null;
  const line = String(question || "")
    .split("\n")
    .map((part) => part.trim())
    .find(Boolean);
  if (!id || !line) return null;
  const row = {
    ...(index >= 0 ? list[index] : { id }),
    summary: line.length > 80 ? `${line.slice(0, 80)}…` : line,
    modified: now.toISOString(),
  };
  return index >= 0
    ? [...list.slice(0, index), row, ...list.slice(index + 1)]
    : [row, ...list];
}
