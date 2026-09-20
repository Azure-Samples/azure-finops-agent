export function createAssistantMessageStream() {
  const segments = [];
  const byId = new Map();
  let current = null;
  let boundary = false;

  const text = () => segments
    .filter((segment) => segment.content.trim())
    .map((segment) => segment.content)
    .join("\n\n");

  function select(messageId) {
    const id = typeof messageId === "string" && messageId ? messageId : null;
    if (id && byId.has(id)) {
      current = byId.get(id);
    } else if (current && !current.complete && !boundary && !current.id) {
      if (id) {
        current.id = id;
        byId.set(id, current);
      }
    } else {
      current = { id, content: "", complete: false };
      segments.push(current);
      if (id) byId.set(id, current);
    }
    boundary = false;
    return current;
  }

  return {
    text,
    append(content, messageId) {
      if (typeof content !== "string" || !content) return text();
      const segment = select(messageId);
      if (!segment.complete) segment.content += content;
      return text();
    },
    complete(content, messageId) {
      if (typeof content !== "string" || !content.trim()) return text();
      if (!messageId && !boundary && current?.complete && current.content === content)
        return text();
      const segment = select(messageId);
      segment.content = content;
      segment.complete = true;
      return text();
    },
    toolBoundary() {
      boundary = true;
    },
  };
}
