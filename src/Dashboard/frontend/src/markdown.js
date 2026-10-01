// Escape-first Markdown renderer for model and tool-influenced text.
//
// SECURITY: every character of the input is HTML-escaped (&, <, >) before any
// transform, so raw markup can never reach v-html. Entities the model already
// wrote (&lt; &#65; …) are kept: in text they only ever decode to characters.
// Every element below is built by this module; attribute values are quoted and
// links are limited to absolute http(s) URLs that open in a new tab.
//
// Text is never rewritten: identifiers such as Standard_D4s_v5, host names and
// versions render byte-for-byte, so `_` inside a word is never emphasis and a
// lone `*` between spaces (5 * 3) is never italics.
import { stripCitationMarkers } from "./modelText.js";

const FENCE = /^ {0,3}(`{3,}|~{3,})[ \t]*([\w+#.-]*)[^`]*$/;
const HEADING = /^ {0,3}(#{1,6})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$/;
const RULE = /^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$/;
const QUOTE = /^ {0,3}&gt;[ \t]?/;
const LIST_ITEM = /^([ \t]*)([-*+]|\d{1,9}[.)])(?:[ \t]+(.*))?$/;
const TABLE_RULE = /^[ \t]*\|?[ \t]*:?-+:?[ \t]*(?:\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$/;
const TOKEN = /\u0000(\d+)\u0000/g;

export function escapeHtml(value) {
  return String(value)
    .replace(/&(?!(?:[a-zA-Z][a-zA-Z0-9]*|#\d+|#x[\da-fA-F]+);)/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

/** Renders model Markdown to safe HTML for v-html. */
export function renderMarkdown(text) {
  if (!text) return "";
  const escaped = escapeHtml(stripCitationMarkers(String(text)).replace(/\u0000/g, ""));
  const lines = escaped.replace(/\r\n?/g, "\n").split("\n").map(expandIndent);
  return renderBlocks(lines);
}

function expandIndent(line) {
  const lead = line.match(/^[ \t]*/)[0];
  return lead.includes("\t") ? lead.replace(/\t/g, "    ") + line.slice(lead.length) : line;
}

const indentOf = (line) => line.match(/^ */)[0].length;
const isOrdered = (marker) => /\d/.test(marker);

function renderBlocks(lines) {
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) {
      i++;
      continue;
    }
    const fence = line.match(FENCE);
    if (fence) {
      const body = [];
      i++;
      while (i < lines.length && !closesFence(lines[i], fence[1])) body.push(lines[i++]);
      if (i < lines.length) i++;
      const lang = fence[2] ? ` class="lang-${fence[2]}"` : "";
      out.push(`<pre><code${lang}>${body.join("\n")}</code></pre>`);
      continue;
    }
    const heading = line.match(HEADING);
    if (heading) {
      const level = Math.min(4, heading[1].length + 1);
      out.push(`<h${level}>${renderInline(heading[2])}</h${level}>`);
      i++;
      continue;
    }
    if (RULE.test(line)) {
      out.push("<hr/>");
      i++;
      continue;
    }
    if (startsTable(lines, i)) {
      i = renderTable(lines, i, out);
      continue;
    }
    if (QUOTE.test(line)) {
      const inner = [];
      while (i < lines.length && lines[i].trim() && (QUOTE.test(lines[i]) || !startsBlock(lines, i)))
        inner.push(lines[i++].replace(QUOTE, ""));
      out.push(`<blockquote>${renderBlocks(inner)}</blockquote>`);
      continue;
    }
    if (LIST_ITEM.test(line)) {
      i = renderList(lines, i, out);
      continue;
    }
    const paragraph = [line.trim()];
    i++;
    while (i < lines.length && lines[i].trim() && !startsBlock(lines, i)) paragraph.push(lines[i++].trim());
    out.push(`<p>${renderInline(paragraph.join("\n")).replace(/\n/g, "<br/>")}</p>`);
  }
  return out.join("");
}

function closesFence(line, open) {
  const trimmed = line.trim();
  return trimmed.length >= open.length && trimmed[0] === open[0] && /^(`+|~+)$/.test(trimmed);
}

function startsBlock(lines, i) {
  const line = lines[i];
  return (
    FENCE.test(line) ||
    HEADING.test(line) ||
    RULE.test(line) ||
    QUOTE.test(line) ||
    LIST_ITEM.test(line) ||
    startsTable(lines, i)
  );
}

function startsTable(lines, i) {
  return (
    i + 1 < lines.length &&
    lines[i].includes("|") &&
    lines[i + 1].includes("|") &&
    TABLE_RULE.test(lines[i + 1])
  );
}

function splitRow(line) {
  let row = line.trim();
  if (row.startsWith("|")) row = row.slice(1);
  if (row.endsWith("|") && !row.endsWith("\\|")) row = row.slice(0, -1);
  const cells = [];
  let cell = "";
  let code = false;
  for (let k = 0; k < row.length; k++) {
    const ch = row[k];
    if (ch === "\\" && row[k + 1] === "|") {
      cell += "|";
      k++;
      continue;
    }
    if (ch === "`") code = !code;
    if (ch === "|" && !code) {
      cells.push(cell.trim());
      cell = "";
      continue;
    }
    cell += ch;
  }
  cells.push(cell.trim());
  return cells;
}

function renderTable(lines, start, out) {
  const header = splitRow(lines[start]);
  const align = splitRow(lines[start + 1]).map((cell) =>
    /^:-+:$/.test(cell) ? "wt-c" : /^-+:$/.test(cell) ? "wt-r" : "",
  );
  const rows = [];
  let i = start + 2;
  while (i < lines.length && lines[i].trim() && lines[i].includes("|")) rows.push(splitRow(lines[i++]));
  out.push(buildTable(header, rows, align));
  return i;
}

/** Builds the data table: numeric columns align right in tabular figures, status words become pills and bare deltas get arrows. */
export function buildTable(header, rows, align = []) {
  const numeric = header.map((_, c) => {
    const values = rows.map((row) => row[c] ?? "").filter((cell) => cell.trim());
    return values.length > 0 && values.filter((cell) => parseNumeric(cell) !== null).length / values.length >= 0.6;
  });
  const cellClass = (c) => {
    const names = [numeric[c] ? "wt-num" : "", align[c] || ""].filter(Boolean).join(" ");
    return names ? ` class="${names}"` : "";
  };
  const head = header.map((cell, c) => `<th${cellClass(c)}>${renderInline(cell)}</th>`).join("");
  const body = rows
    .map((row) => `<tr>${header.map((_, c) => `<td${cellClass(c)}>${renderCell(row[c] ?? "")}</td>`).join("")}</tr>`)
    .join("");
  return `<div class="wt-wrap"><table class="wow-table"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

export function parseNumeric(cell) {
  if (!cell) return null;
  const match = String(cell)
    .replace(/[▲▼↑↓]/g, "")
    .match(/-?[\d,]+\.?\d*\s*[kKmMbB%]?/);
  if (!match) return null;
  let raw = match[0].replace(/,/g, "").trim();
  let multiplier = 1;
  if (/k$/i.test(raw)) multiplier = 1e3;
  else if (/m$/i.test(raw)) multiplier = 1e6;
  else if (/b$/i.test(raw)) multiplier = 1e9;
  if (/[kmb%]$/i.test(raw)) raw = raw.slice(0, -1);
  const value = parseFloat(raw);
  return Number.isFinite(value) ? value * multiplier : null;
}

const GOOD = new Set(["ok", "optimal", "healthy", "good", "pass", "passed", "✓", "✅", "yes", "active", "enabled"]);
const WARN = new Set(["watch", "warning", "warn", "caution", "pending", "medium", "partial"]);
const BAD = new Set(["alert", "critical", "fail", "failed", "error", "high", "✗", "❌", "no", "down", "orphan", "orphaned", "unattached", "idle", "disabled"]);

function renderCell(raw) {
  const trimmed = raw.trim();
  const lower = trimmed.toLowerCase();
  if (GOOD.has(lower)) return `<span class="wt-tag wt-tag-g">${trimmed}</span>`;
  if (WARN.has(lower)) return `<span class="wt-tag wt-tag-w">${trimmed}</span>`;
  if (BAD.has(lower)) return `<span class="wt-tag wt-tag-b">${trimmed}</span>`;
  const up = trimmed.match(/^(?:▲|↑)\s*([\d.,]+\s*%?)$/);
  if (up) return `<span class="wt-up">▲ ${up[1]}</span>`;
  const down = trimmed.match(/^(?:▼|↓)\s*([\d.,]+\s*%?)$/);
  if (down) return `<span class="wt-down">▼ ${down[1]}</span>`;
  return renderInline(raw);
}

function renderList(lines, start, out) {
  const first = lines[start].match(LIST_ITEM);
  const base = indentOf(first[1]);
  const ordered = isOrdered(first[2]);
  const items = [];
  let item = null;
  let offset = 0;
  let i = start;
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) {
      let next = i + 1;
      while (next < lines.length && !lines[next].trim()) next++;
      if (next >= lines.length) {
        i = next;
        break;
      }
      const marker = lines[next].match(LIST_ITEM);
      const continues =
        indentOf(lines[next]) > base ||
        (marker && indentOf(marker[1]) === base && isOrdered(marker[2]) === ordered && !RULE.test(lines[next]));
      if (!continues) break;
      if (item && indentOf(lines[next]) > base) item.lines.push("");
      i = next;
      continue;
    }
    const marker = RULE.test(line) ? null : line.match(LIST_ITEM);
    const indent = indentOf(line);
    if (marker && indent === base) {
      if (isOrdered(marker[2]) !== ordered) break;
      const content = marker[3] ?? "";
      offset = indent + marker[2].length + Math.max(1, Math.min(4, line.length - indent - marker[2].length - content.length));
      item = { lines: [content], number: ordered ? parseInt(marker[2], 10) : null };
      items.push(item);
      i++;
      continue;
    }
    if (indent > base && item) {
      item.lines.push(line.slice(Math.min(indent, offset)));
      i++;
      continue;
    }
    // A lazy continuation line extends the last item's text; anything else ends the list.
    if (indent < base || startsBlock(lines, i) || !item) break;
    item.lines.push(line.trim());
    i++;
  }
  const tag = ordered ? "ol" : "ul";
  const startNumber = ordered && items[0]?.number !== 1 ? ` start="${items[0].number}"` : "";
  const body = items
    .map((entry) => {
      let html = renderBlocks(entry.lines).replace(/^<p>([\s\S]*?)<\/p>/, "$1");
      const task = html.match(/^\[([ xX])\] /);
      if (task) html = `<span class="md-task${task[1] === " " ? "" : " md-task--done"}" aria-hidden="true"></span>${html.slice(4)}`;
      return `<li>${html}</li>`;
    })
    .join("");
  out.push(`<${tag}${startNumber}>${body}</${tag}>`);
  return i;
}

const attribute = (value) => value.replace(/"/g, "&quot;");

function link(url, label) {
  return `<a href="${attribute(url)}" target="_blank" rel="noopener noreferrer">${label}</a>`;
}

/** Renders inline Markdown in already-escaped text. */
function renderInline(text) {
  const slots = [];
  const hold = (html) => `\u0000${slots.push(html) - 1}\u0000`;
  let s = text;
  s = s.replace(/(?<!`)(`+)(?!`)([\s\S]*?[^`])\1(?!`)/g, (_, ticks, code) =>
    hold(`<code>${code.length > 2 && code.startsWith(" ") && code.endsWith(" ") ? code.slice(1, -1) : code}</code>`),
  );
  s = s.replace(/\\(&amp;|&lt;|&gt;|[!-/:-@[-`{-~])/g, (_, ch) => hold(ch));
  // Suggested questions: [label](prompt:full question) becomes a chip that sends it.
  s = s.replace(/\[([^\]\n]+)\]\(prompt:([^)\n]+)\)/g, (_, label, question) =>
    hold(`<button type="button" class="prompt-chip" data-prompt="${attribute(question.trim())}">${emphasis(label.trim())}</button>`),
  );
  s = s.replace(
    /\[([^\]\n]+)\]\((https?:\/\/[^\s()]*(?:\([^\s()]*\)[^\s()]*)*)(?:[ \t]+"[^"\n]*")?\)/g,
    (_, label, url) => hold(link(url, emphasis(label))),
  );
  s = s.replace(/&lt;(https?:\/\/[^\s]+?)&gt;/g, (_, url) => hold(link(url, url)));
  s = s.replace(/(?<![\w/="'@-])https?:\/\/[^\s"'`\u0000]+/g, (match) => {
    let url = match.split(/&lt;|&gt;/)[0];
    for (;;) {
      const trimmed = url.replace(/[.,;:!?*_~'"]+$/, "");
      if (trimmed.endsWith(")") && (trimmed.match(/\(/g) || []).length < (trimmed.match(/\)/g) || []).length) {
        url = trimmed.slice(0, -1);
        continue;
      }
      url = trimmed;
      break;
    }
    if (!/^https?:\/\/[^/]/.test(url)) return match;
    return hold(link(url, url)) + match.slice(url.length);
  });
  s = emphasis(s);
  const restore = (value) => value.replace(TOKEN, (_, index) => restore(slots[Number(index)]));
  return restore(s);
}

function emphasis(text) {
  return text
    .replace(/\*\*\*(?=[^\s*])([\s\S]*?[^\s*])\*\*\*/g, "<strong><em>$1</em></strong>")
    .replace(/\*\*(?=[^\s*])([\s\S]*?[^\s*])\*\*/g, "<strong>$1</strong>")
    .replace(/(?<![*\w\\])\*(?=[^\s*])([^*\n]*?[^\s*])\*(?![*\w])/g, "<em>$1</em>")
    .replace(/(?<![\w\\])_(?=[^\s_])([^_\n]*?[^\s_])_(?!\w)/g, "<em>$1</em>")
    .replace(/~~(?=\S)([\s\S]*?\S)~~/g, "<del>$1</del>");
}
