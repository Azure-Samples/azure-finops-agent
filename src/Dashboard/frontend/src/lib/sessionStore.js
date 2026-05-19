// IndexedDB-backed chat history. Sessions and messages live entirely in the
// user's browser — the server is stateless. Switching browser/device wipes
// history; clearing site data wipes history.
//
// Schema:
//   sessions:  { id, title, createdUtc, updatedUtc, messageCount }
//   messages:  { id (auto), sessionId, seq, role, content, toolCalls, charts, html, script, ts }
//   index: messages by [sessionId, seq]

import { openDB } from "idb";

const DB_NAME = "finops-agent";
const DB_VERSION = 1;

let dbPromise = null;

function getDB() {
  if (!dbPromise) {
    dbPromise = openDB(DB_NAME, DB_VERSION, {
      upgrade(db) {
        if (!db.objectStoreNames.contains("sessions")) {
          const s = db.createObjectStore("sessions", { keyPath: "id" });
          s.createIndex("updatedUtc", "updatedUtc");
        }
        if (!db.objectStoreNames.contains("messages")) {
          const m = db.createObjectStore("messages", {
            keyPath: "id",
            autoIncrement: true,
          });
          m.createIndex("bySession", ["sessionId", "seq"]);
        }
      },
    });
  }
  return dbPromise;
}

export function newSessionId() {
  // RFC4122 v4-ish — sufficient for a local key.
  return (
    "s-" +
    Date.now().toString(36) +
    "-" +
    Math.random().toString(36).slice(2, 10)
  );
}

export async function listSessions() {
  const db = await getDB();
  const all = await db.getAll("sessions");
  return all.sort((a, b) => (b.updatedUtc || 0) - (a.updatedUtc || 0));
}

export async function createSession(id, title = "New conversation") {
  const db = await getDB();
  const now = Date.now();
  const row = {
    id,
    title,
    createdUtc: now,
    updatedUtc: now,
    messageCount: 0,
  };
  await db.put("sessions", row);
  return row;
}

export async function deleteSession(id) {
  const db = await getDB();
  const tx = db.transaction(["sessions", "messages"], "readwrite");
  await tx.objectStore("sessions").delete(id);
  // Range over messages for this sessionId. IDB rejects Infinity as a key —
  // use finite bounds spanning the seq numeric range.
  const idx = tx.objectStore("messages").index("bySession");
  const range = IDBKeyRange.bound([id, 0], [id, Number.MAX_SAFE_INTEGER]);
  let cursor = await idx.openCursor(range);
  while (cursor) {
    await cursor.delete();
    cursor = await cursor.continue();
  }
  await tx.done;
}

export async function renameSession(id, title) {
  const db = await getDB();
  const row = await db.get("sessions", id);
  if (!row) return;
  row.title = title;
  row.updatedUtc = Date.now();
  await db.put("sessions", row);
}

export async function getMessages(sessionId) {
  const db = await getDB();
  const idx = db.transaction("messages").store.index("bySession");
  const range = IDBKeyRange.bound([sessionId, 0], [sessionId, Number.MAX_SAFE_INTEGER]);
  const out = [];
  let cursor = await idx.openCursor(range);
  while (cursor) {
    out.push(cursor.value);
    cursor = await cursor.continue();
  }
  return out.sort((a, b) => a.seq - b.seq);
}

export async function appendMessage(sessionId, message) {
  const db = await getDB();
  const tx = db.transaction(["sessions", "messages"], "readwrite");
  const sess = await tx.objectStore("sessions").get(sessionId);
  if (!sess) {
    // Auto-create if missing — keeps the writer side robust.
    const now = Date.now();
    await tx.objectStore("sessions").put({
      id: sessionId,
      title: "New conversation",
      createdUtc: now,
      updatedUtc: now,
      messageCount: 0,
    });
  }
  const ms = tx.objectStore("messages");
  const idx = ms.index("bySession");
  const range = IDBKeyRange.bound(
    [sessionId, 0],
    [sessionId, Number.MAX_SAFE_INTEGER],
  );
  let seq = 0;
  let cursor = await idx.openCursor(range, "prev");
  if (cursor) seq = (cursor.value.seq || 0) + 1;
  const row = {
    sessionId,
    seq,
    role: message.role,
    content: message.content || "",
    toolCalls: message.toolCalls || [],
    charts: message.charts || [],
    html: message.html || null,
    script: message.script || null,
    ts: Date.now(),
  };
  await ms.add(row);
  const s = (await tx.objectStore("sessions").get(sessionId)) || sess;
  if (s) {
    s.updatedUtc = Date.now();
    s.messageCount = (s.messageCount || 0) + 1;
    await tx.objectStore("sessions").put(s);
  }
  await tx.done;
  return row;
}

// Replace the most recent assistant message (used to commit a streamed reply
// in one final write rather than churn IndexedDB per delta).
export async function upsertLastAssistant(sessionId, message) {
  const db = await getDB();
  const tx = db.transaction(["sessions", "messages"], "readwrite");
  const idx = tx.objectStore("messages").index("bySession");
  const range = IDBKeyRange.bound(
    [sessionId, 0],
    [sessionId, Number.MAX_SAFE_INTEGER],
  );
  let cursor = await idx.openCursor(range, "prev");
  let target = null;
  while (cursor) {
    if (cursor.value.role === "assistant") {
      target = cursor.value;
      break;
    }
    cursor = await cursor.continue();
  }
  if (target) {
    target.content = message.content ?? target.content;
    target.toolCalls = message.toolCalls ?? target.toolCalls;
    target.charts = message.charts ?? target.charts;
    target.html = message.html ?? target.html;
    target.script = message.script ?? target.script;
    target.ts = Date.now();
    await tx.objectStore("messages").put(target);
    const s = await tx.objectStore("sessions").get(sessionId);
    if (s) {
      s.updatedUtc = Date.now();
      await tx.objectStore("sessions").put(s);
    }
    await tx.done;
    return target;
  }
  await tx.done;
  // No existing assistant row — append a new one.
  return appendMessage(sessionId, { ...message, role: "assistant" });
}

// Returns just { role, content } for the LLM replay payload.
export async function getHistoryForReplay(sessionId, maxTurns = 40) {
  const all = await getMessages(sessionId);
  const slim = all
    .filter((m) => m.role === "user" || m.role === "assistant")
    .map((m) => ({ role: m.role, content: m.content || "" }))
    .filter((m) => m.content.length > 0);
  return slim.slice(-maxTurns);
}

export async function exportAll() {
  const sessions = await listSessions();
  const out = { version: 1, exportedUtc: Date.now(), sessions: [] };
  for (const s of sessions) {
    const messages = await getMessages(s.id);
    out.sessions.push({ ...s, messages });
  }
  return out;
}

export async function importAll(payload) {
  if (!payload || !Array.isArray(payload.sessions)) return 0;
  const db = await getDB();
  let count = 0;
  for (const s of payload.sessions) {
    if (!s?.id) continue;
    const tx = db.transaction(["sessions", "messages"], "readwrite");
    await tx.objectStore("sessions").put({
      id: s.id,
      title: s.title || "Imported",
      createdUtc: s.createdUtc || Date.now(),
      updatedUtc: s.updatedUtc || Date.now(),
      messageCount: (s.messages || []).length,
    });
    const ms = tx.objectStore("messages");
    for (const m of s.messages || []) {
      await ms.add({
        sessionId: s.id,
        seq: m.seq ?? 0,
        role: m.role,
        content: m.content || "",
        toolCalls: m.toolCalls || [],
        charts: m.charts || [],
        html: m.html || null,
        script: m.script || null,
        ts: m.ts || Date.now(),
      });
    }
    await tx.done;
    count++;
  }
  return count;
}
