#!/usr/bin/env python3
"""Export real user conversations from a deployment as compact records for rating each one from 0 to 10.

Each conversation record holds what a judge needs to decide whether the agent served the user. Per turn:

* the user's own words, and the context the host injected for the agent (sign-in state, connected APIs,
  subscriptions and their offers, uploaded files);
* every tool call with its arguments, success, duration, error and a bounded result excerpt (the evidence);
* every answer the user saw, approval prompts and turn errors;
* the turn outcome, its timing (seconds in total, to the first answer text, model rounds, tool time, tokens),
  whether the browser rendered the answer live, and the user's own rating.

Sources, all read-only: the transcripts and turn records in App Service storage (Kudu), the agent runs in
Application Insights, linked to each turn through the ChatRequest span's request.id, and the browser's own
telemetry, whose location gives the client country. --diagnostics adds what a rating does not need but a root-cause
analysis does: the stored model context from the Foundry project Responses API (system prompt, tool definitions,
every model input item), each model call and tool span, server warnings and exceptions, and every browser event.

Left out by default: the maintainer's own use (browser telemetry from Denmark, plus that browser's and that owner's
other conversations), the post-deployment smoke check, conversations their users deleted, and records larger than
--max-chars (very long chats that no longer fit one judge call). Conversations of the retired Copilot SDK runtime
(before 29 September 2026) are not exported. Private reasoning is never exported, credentials are redacted, and
owners, sessions, GUIDs, e-mail addresses and response IDs are pseudonymised. Tenant resource names remain, so the
output is customer data: keep it outside the repository.

    python export_sessions.py --out <folder outside the repo> [--days 30] [--latest N] [--diagnostics]
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import hashlib
import hmac
import http.client
import io
import json
import os
import re
import secrets
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import zipfile
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path

PARALLEL = 24
RETRYABLE = {408, 429, 500, 502, 503, 504}
GUID = re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
EMAIL = re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")
RESPONSE_ID = re.compile(r"\bresp_[A-Za-z0-9]{20,}\b")
OWNER_PATH = re.compile(r"(/home/[^/\s\"']+/(?:anon|users(?:/v2)?))/([^/\s\"']+)")
CREDENTIALS = [
    (re.compile(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}"), "[REDACTED:jwt]"),
    (re.compile(r"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{20,}"), "Bearer [REDACTED]"),
    (re.compile(r"(?i)\b(sig|signature)=[A-Za-z0-9%+/=]{10,}"), r"\1=[REDACTED]"),
    (re.compile(r"(?i)\b(AccountKey|SharedAccessKey|Password|pwd|client_secret|clientSecret|InstrumentationKey|api[-_]?key)"
                r"(\"?\s*[=:]\s*\"?)[^;\"'\s,}]{6,}"), r"\1\2[REDACTED]"),
]
DATA_URI = re.compile(r"data:[\w/+.-]+;base64,[A-Za-z0-9+/=]{200,}")
PRIVATE_FIELDS = {"reasoningOpaque", "encryptedContent", "reasoningText", "encrypted_content"}
CITATION = re.compile(r"[ \t]*\ue200[^\ue200\ue201]{0,500}\ue201")
# infra/scripts/deployment-smoke.mjs asks this after every deployment; it is a probe, not a user.
SMOKE_PROMPT = "Deployment health check. Do not call any tools."
# Tools whose result the user sees as a chart, score card, file, script, deck, approval prompt or follow-up button.
VISIBLE_OUTPUT_TOOLS = {"RenderChart", "RenderAdvancedChart", "ReportMaturityScore", "GenerateScript", "GenerateDataReport",
                        "GenerateHtmlPresentation", "ApplyAzureChange", "PublishFAQ", "SuggestFollowUp"}
# A turn's browser stream and turn record are matched to its user message by time; client clocks can drift.
MATCH_WINDOW = timedelta(minutes=5)


def log(message: str) -> None:
    print(f"[{datetime.now():%H:%M:%S}] {message}", file=sys.stderr, flush=True)


def run(*args: str) -> str:
    result = subprocess.run(list(args), capture_output=True, text=True, shell=os.name == "nt")
    if result.returncode != 0:
        raise RuntimeError(f"{' '.join(args[:3])} failed: {result.stderr.strip()[:300]}")
    return result.stdout.strip()


def parse_time(value: str) -> datetime:
    value = value.replace("Z", "+00:00")
    # Python < 3.11 rejects 7-digit fractions, which .NET and Log Analytics emit.
    value = re.sub(r"(\.\d{6})\d+", r"\1", value)
    parsed = datetime.fromisoformat(value)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def bounded(text: str | None, limit: int) -> str | None:
    if text is None or len(text) <= limit:
        return text
    return f"{text[:limit]} …[cut: {len(text):,} characters in total]"


def arguments_view(raw: str | None, limit: int):
    if not raw:
        return None
    if len(raw) > limit:
        return bounded(raw, limit)
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw


def split_user_message(raw: str | None) -> tuple[str | None, str | None]:
    """(host-injected context, the user's own words), as SessionEndpoints.VisibleUserText separates them."""
    text = (raw or "").strip()
    if text.lower().startswith(("<skill-context", "<tool-context", "<agent-context")):
        return text, None
    blocks = []
    while text.startswith("["):
        depth, close = 0, -1
        for index, character in enumerate(text):
            if character == "[":
                depth += 1
            elif character == "]":
                depth -= 1
                if depth == 0:
                    close = index
                    break
        if close < 0:
            blocks.append(text)
            text = ""
            break
        blocks.append(text[: close + 1])
        text = text[close + 1:].lstrip()
    return ("\n".join(blocks) or None), (text or None)


def normalize(text) -> str:
    return re.sub(r"\s+", " ", CITATION.sub("", text if isinstance(text, str) else "")).strip()


def message_text(item: dict) -> str:
    content = item.get("content")
    if isinstance(content, str):
        return content
    return "\n".join(part.get("text", "") for part in content or [] if isinstance(part, dict))


def strip_private(item):
    """Removes private reasoning (reasoning items, encrypted or opaque reasoning fields) from a tree."""
    if isinstance(item, list):
        return [strip_private(entry) for entry in item if not (isinstance(entry, dict) and entry.get("type") == "reasoning")]
    if isinstance(item, dict):
        return {key: strip_private(value) for key, value in item.items() if key not in PRIVATE_FIELDS}
    return item


def take_nearest(items: list, at: datetime, when) -> dict | None:
    """Removes and returns the item closest in time to at, within MATCH_WINDOW."""
    best = min(items, key=lambda item: abs(when(item) - at), default=None)
    if best is None or abs(when(best) - at) > MATCH_WINDOW:
        return None
    items.remove(best)
    return best


class Tokens:
    """Azure CLI access tokens per resource, refreshed before expiry. Never printed or written."""

    def __init__(self, subscription: str):
        self._subscription = subscription
        self._cache: dict[str, tuple[str, float]] = {}
        self._locks: dict[str, threading.Lock] = {}
        self._guard = threading.Lock()

    def get(self, resource: str) -> str:
        with self._guard:
            lock = self._locks.setdefault(resource, threading.Lock())
        with lock:
            token, expires = self._cache.get(resource, ("", 0.0))
            if time.time() < expires - 300:
                return token
            raw = json.loads(run("az", "account", "get-access-token", "--subscription", self._subscription,
                                 "--resource", resource, "-o", "json"))
            self._cache[resource] = (raw["accessToken"], float(raw.get("expires_on") or time.time() + 1800))
            return raw["accessToken"]


class Http:
    """Keep-alive HTTPS connections per thread and host; a new connection per request stalled on connect."""

    def __init__(self, tokens: Tokens, timeout: int = 120):
        self._tokens = tokens
        self._timeout = timeout
        self._local = threading.local()

    def _connection(self, host: str, fresh: bool = False) -> http.client.HTTPSConnection:
        pool = self._local.__dict__.setdefault("pool", {})
        if fresh and host in pool:
            pool.pop(host).close()
        if host not in pool:
            pool[host] = http.client.HTTPSConnection(host, timeout=self._timeout)
        return pool[host]

    def request(self, url: str, resource: str, body: dict | None = None) -> bytes:
        parts = urllib.parse.urlsplit(url)
        target = parts.path + (f"?{parts.query}" if parts.query else "")
        data = json.dumps(body).encode() if body is not None else None
        for attempt in range(6):
            headers = {"Authorization": f"Bearer {self._tokens.get(resource)}", "Connection": "keep-alive"}
            if data:
                headers["Content-Type"] = "application/json"
            try:
                connection = self._connection(parts.netloc, fresh=attempt > 0)
                connection.request("POST" if data else "GET", target, body=data, headers=headers)
                response = connection.getresponse()
                payload = response.read()
            except (OSError, http.client.HTTPException):
                if attempt == 5:
                    raise
                time.sleep(2 ** attempt)
                continue
            if response.status < 400:
                return payload
            error = urllib.error.HTTPError(url, response.status, response.reason, response.headers, None)
            if response.status not in RETRYABLE or attempt == 5:
                raise error
            time.sleep(min(float(response.headers.get("Retry-After") or 2 ** attempt), 30))
        raise RuntimeError("unreachable")

    def json(self, url: str, resource: str, body: dict | None = None):
        return json.loads(self.request(url, resource, body))


class Pseudonymizer:
    """Stable aliases within one export; the key is random per run and never stored."""

    def __init__(self, redact_content: bool, keep_mapping: bool):
        self._key = secrets.token_bytes(32)
        self._redact = redact_content
        self.mapping: dict[str, str] | None = {} if keep_mapping else None
        self._lock = threading.Lock()

    def _digest(self, value: str) -> str:
        return hmac.new(self._key, value.lower().encode(), hashlib.sha256).hexdigest()

    def _remember(self, alias: str, value: str) -> str:
        if self.mapping is not None:
            with self._lock:
                self.mapping.setdefault(alias, value)
        return alias

    def guid(self, value: str) -> str:
        d = self._digest(value)
        return self._remember(f"{d[:8]}-{d[8:12]}-4{d[13:16]}-8{d[17:20]}-{d[20:32]}", value)

    def owner(self, directory: str) -> str:
        return self.guid(directory) if GUID.fullmatch(directory) else self._remember("owner-" + self._digest(directory)[:16], directory)

    def response(self, value: str) -> str:
        return self._remember("resp-" + self._digest(value)[:20], value)

    def text(self, value: str) -> str:
        value = DATA_URI.sub("[binary data omitted]", value)
        for pattern, replacement in CREDENTIALS:
            value = pattern.sub(replacement, value)
        if not self._redact:
            return value
        value = OWNER_PATH.sub(lambda m: f"{m.group(1)}/{self.owner(m.group(2))}", value)
        value = RESPONSE_ID.sub(lambda m: self.response(m.group(0)), value)
        value = EMAIL.sub(lambda m: f"user-{self._digest(m.group(0))[:10]}@example.invalid", value)
        return GUID.sub(lambda m: self.guid(m.group(0)), value)

    def deep(self, value):
        if isinstance(value, str):
            return self.text(value)
        if isinstance(value, list):
            return [self.deep(item) for item in value]
        if isinstance(value, dict):
            return {key: self.deep(item) for key, item in value.items()}
        return value


class Exporter:
    def __init__(self, args):
        self.args = args
        self.started = time.time()
        self.phases: dict[str, float] = {}
        self.cutoff = datetime.now(timezone.utc) - timedelta(days=args.days)
        self.since = (self.cutoff - timedelta(days=1)).strftime("%Y-%m-%dT%H:%M:%SZ")
        env = {}
        for line in run("azd", "env", "get-values", *(["--environment", args.environment] if args.environment else [])).splitlines():
            if "=" in line:
                key, value = line.split("=", 1)
                env[key] = value.strip().strip('"')
        required = ["AZURE_SUBSCRIPTION_ID", "AZURE_RESOURCE_GROUP", "WEB_APP_NAME", "AZURE_LOG_ANALYTICS_WORKSPACE_ID"]
        if args.diagnostics:
            required.append("AZURE_OPENAI_ENDPOINT")
        if missing := [key for key in required if not env.get(key)]:
            raise SystemExit(f"azd environment lacks {', '.join(missing)}")
        self.subscription, self.group, self.app = env["AZURE_SUBSCRIPTION_ID"], env["AZURE_RESOURCE_GROUP"], env["WEB_APP_NAME"]
        self.tokens = Tokens(self.subscription)
        self.http = Http(self.tokens)
        self.p = Pseudonymizer(not args.no_redact_content, args.keep_mapping)
        self.project = self._project_endpoint(env["AZURE_OPENAI_ENDPOINT"], env.get("AZURE_AI_PROJECT_NAME")) if args.diagnostics else None
        workspace = env["AZURE_LOG_ANALYTICS_WORKSPACE_ID"]
        site = ["--subscription", self.subscription, "-g", self.group, "-n", self.app]
        resources = ["https://management.azure.com", "https://api.loganalytics.io"] + (["https://ai.azure.com"] if args.diagnostics else [])
        # Each Azure CLI call takes seconds: tokens are fetched at once, and the rest comes from azd values and REST calls.
        with cf.ThreadPoolExecutor(4) as pool:
            for token in [pool.submit(self.tokens.get, resource) for resource in resources]:
                token.result()
        host = env.get("WEB_APP_HOSTNAME", "")
        if host.endswith(".azurewebsites.net"):
            first, rest = host.split(".", 1)
            self.scm_host = f"{first}.scm.{rest}"
        else:
            self.scm_host = next(h for h in json.loads(run("az", "webapp", "show", *site, "--query", "enabledHostNames", "-o", "json")) if ".scm." in h)
        with cf.ThreadPoolExecutor(2) as pool:
            customer = pool.submit(self.http.json, f"https://management.azure.com{workspace}?api-version=2022-10-01",
                                   "https://management.azure.com") if workspace.startswith("/subscriptions/") else None
            # infra/modules/site-config.bicep sets AGENT_HOME to /home/copilot; other values are read from the app settings.
            default_home = pool.submit(lambda: any(e["name"] == "turn-outcomes" for e in self.listing("copilot")))
            self.workspace = customer.result()["properties"]["customerId"] if customer else workspace
            home = args.agent_home or ("/home/copilot" if default_home.result() else None)
        if not home:
            # Only the two storage-root settings leave the CLI; other values, which can hold secrets, are never read.
            values = {item["name"]: item.get("value") for item in json.loads(run(
                "az", "webapp", "config", "appsettings", "list", *site, "--query",
                "[?name=='AGENT_HOME' || name=='COPILOT_HOME'].{name: name, value: value}", "-o", "json") or "[]")}
            home = values.get("AGENT_HOME") or values.get("COPILOT_HOME") or "/home/copilot"
        if not home.startswith("/home/"):
            raise SystemExit("AGENT_HOME is not under /home, so Kudu cannot read it")
        self.home = home[len("/home/"):].strip("/")
        self.responses: dict[str, dict] = {}
        self.items: dict[str, list | None] = {}
        self.cache_lock = threading.Lock()
        self.prompts: dict[str, str] = {}
        self.tool_sets: dict[str, list] = {}
        self.mark("connect")

    def mark(self, phase: str) -> None:
        now = time.time()
        self.phases[phase] = round(now - self.started - sum(self.phases.values()), 1)
        log(f"{phase}: {self.phases[phase]} s")

    @staticmethod
    def _project_endpoint(endpoint: str, project: str | None) -> str:
        uri = urllib.parse.urlparse(endpoint.strip())
        if "/api/projects/" in uri.path.lower():
            return f"https://{uri.netloc}{uri.path.rstrip('/')}"
        if not project:
            raise SystemExit("Set AZURE_AI_PROJECT_NAME or use a project endpoint")
        return f"https://{uri.netloc.split('.')[0]}.services.ai.azure.com/api/projects/{urllib.parse.quote(project)}"

    # ---------- App Service storage through Kudu (GET only) ----------
    def scm(self, api: str, path: str) -> bytes | None:
        try:
            return self.http.request(f"https://{self.scm_host}/api/{api}/{urllib.parse.quote(path)}", "https://management.azure.com")
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            raise

    def listing(self, path: str) -> list[dict]:
        body = self.scm("vfs", path.rstrip("/") + "/")
        entries = json.loads(body) if body else []
        return entries if isinstance(entries, list) else []

    def turn_records(self) -> dict[str, list]:
        """Every turn record in the window, by conversation. Each turn writes one with its SessionId, numeric owner ID,
        RequestId and status; the folder holds only this metadata, so one zip replaces a request per file."""
        body = self.scm("zip", f"{self.home}/turn-outcomes/")
        records: dict[str, list] = {}
        with zipfile.ZipFile(io.BytesIO(body or b"PK\x05\x06" + b"\0" * 18)) as archive:
            for name in archive.namelist():
                if not name.endswith(".json"):
                    continue
                try:
                    record = json.loads(archive.read(name))
                except (json.JSONDecodeError, UnicodeDecodeError):
                    continue
                if isinstance(record, dict) and record.get("SessionId") and record.get("StartedUtc") \
                        and parse_time(record["StartedUtc"]) >= self.cutoff:
                    records.setdefault(record["SessionId"], []).append(record)
        for items in records.values():
            items.sort(key=lambda record: parse_time(record["StartedUtc"]))
        log(f"turn records: {sum(map(len, records.values()))} turns in {len(records)} conversations")
        return records

    def entra_index(self) -> dict[str, tuple[str, str, str]]:
        """Session ID to signed-in owner folder. Only sessions/ is listed; identity files are never opened."""
        index: dict[str, tuple[str, str, str]] = {}
        with cf.ThreadPoolExecutor(PARALLEL) as pool:
            roots = {root: pool.submit(self.listing, f"{self.home}/{root}") for root in ("users/v2", "users")}
            owners = [(root, kind, e["name"]) for root, kind in (("users/v2", "entra"), ("users", "entra-legacy-oid"))
                      for e in roots[root].result() if e.get("mime") == "inode/directory" and e["name"] != "v2"]
            listings = pool.map(lambda owner: self.listing(f"{self.home}/{owner[0]}/{owner[2]}/sessions"), owners)
            for (root, kind, owner), sessions in zip(owners, listings):
                for session in sessions:
                    index[session["name"]] = (f"{root}/{owner}", owner, kind)
        return index

    # ---------- Application Insights ----------
    def log_analytics(self, query: str) -> list[dict]:
        table = self.http.json(f"https://api.loganalytics.io/v1/workspaces/{self.workspace}/query", "https://api.loganalytics.io",
                               {"query": query})["tables"][0]
        columns = [c["name"] for c in table["columns"]]
        return [dict(zip(columns, row)) for row in table["rows"]]

    def agent_runs(self) -> tuple[dict, dict]:
        """Agent runs with their model calls and tool spans, by the turn's request.id (the ChatRequest span is the run's
        parent) and, for runs started outside a chat request, by tool call ID."""
        rows = self.log_analytics(f"""
AppDependencies
| where TimeGenerated >= datetime({self.since}) and AppRoleName =~ '{self.app}'
| where Name == 'ChatRequest' or Name startswith_cs 'invoke_agent' or Name startswith_cs 'chat' or Name startswith_cs 'execute_tool'
| project TimeGenerated, Id, ParentId, Name, DurationMs, Success,
    requestId = tostring(Properties['request.id']),
    responseId = tostring(Properties['gen_ai.response.id']),
    finish = tostring(Properties['gen_ai.response.finish_reasons']),
    model = tostring(Properties['gen_ai.response.model']),
    inputTokens = tolong(Properties['gen_ai.usage.input_tokens']),
    cachedTokens = tolong(Properties['gen_ai.usage.cache_read.input_tokens']),
    outputTokens = tolong(Properties['gen_ai.usage.output_tokens']),
    reasoningTokens = tolong(Properties['gen_ai.usage.reasoning.output_tokens']),
    firstChunkSeconds = todouble(Properties['gen_ai.response.time_to_first_chunk']),
    tool = tostring(Properties['gen_ai.tool.name']),
    callId = tostring(Properties['gen_ai.tool.call.id']),
    errorType = tostring(Properties['error.type'])
""")
        spans = {row["Id"]: row for row in rows}

        def run_of(span):
            for _ in range(12):
                if span is None:
                    return None
                if span["Name"].startswith("invoke_agent"):
                    return span["Id"]
                span = spans.get(span["ParentId"])
            return None

        runs = {}
        for span in spans.values():
            if span["Name"].startswith("invoke_agent"):
                parent = spans.get(span["ParentId"])
                runs[span["Id"]] = {"span": span, "calls": [], "tools": [],
                                    "requestId": parent["requestId"] if parent and parent["Name"] == "ChatRequest" else ""}
        by_call: dict[str, dict] = {}
        for span in spans.values():
            if span["Name"] == "ChatRequest" or span["Name"].startswith("invoke_agent") or (owner := run_of(span)) is None:
                continue
            runs[owner]["calls" if span["Name"].startswith("chat") else "tools"].append(span)
            if span["callId"]:
                by_call[span["callId"]] = runs[owner]
        by_request: dict[str, list] = {}
        for item in runs.values():
            item["calls"].sort(key=lambda c: c["TimeGenerated"])
            if item["requestId"]:
                by_request.setdefault(item["requestId"], []).append(item)
        log(f"agent runs: {len(runs)}, linked to a turn request: {sum(map(len, by_request.values()))}")
        return by_request, by_call

    def browser_telemetry(self) -> dict[str, dict]:
        """The browser's own chat events per conversation. Server requests only see the App Service region (client IPs
        are not forwarded), but browser telemetry is located from the client IP and carries the sessionId."""
        rows = self.log_analytics(f"""
let since = datetime({self.since});
let excluded = dynamic({json.dumps(self.args.exclude_country)});
let flaggedBrowsers = union AppEvents, AppPageViews
    | where TimeGenerated >= since and ClientCountryOrRegion in (excluded) and isnotempty(UserId)
    | distinct UserId;
AppEvents
| where TimeGenerated >= since
| extend sid = tostring(Properties.sessionId)
| where isnotempty(sid)
| extend flagged = UserId in (flaggedBrowsers)
| project sid, at = TimeGenerated, name = Name, country = ClientCountryOrRegion, flagged,
    properties = bag_remove_keys(Properties, dynamic(['sessionId']))
| order by at asc
""")
        sessions: dict[str, dict] = {}
        for row in rows:
            entry = sessions.setdefault(row["sid"], {"countries": set(), "excludedCountry": False, "excludedBrowser": False, "events": []})
            if row["country"]:
                entry["countries"].add(row["country"])
            entry["excludedCountry"] |= row["country"] in self.args.exclude_country
            entry["excludedBrowser"] |= bool(row["flagged"])
            properties = row["properties"] if isinstance(row["properties"], dict) else json.loads(row["properties"] or "{}")
            entry["events"].append({"at": row["at"], "name": row["name"], "properties": properties})
        log(f"browser telemetry: {len(rows)} events in {len(sessions)} conversations")
        return sessions

    def server_logs(self) -> dict[str, list]:
        """Warnings, errors and exceptions logged while serving each turn, by the turn's request.id."""
        rows = self.log_analytics(f"""
let turns = AppDependencies
    | where TimeGenerated >= datetime({self.since}) and Name == 'ChatRequest' and isnotempty(OperationId)
    | extend requestId = tostring(Properties['request.id'])
    | where isnotempty(requestId)
    | distinct OperationId, requestId;
union
    (AppTraces | where TimeGenerated >= datetime({self.since}) and SeverityLevel >= 2
        | project at = TimeGenerated, OperationId, source = 'log', severity = SeverityLevel, category = tostring(Properties.CategoryName), text = Message),
    (AppExceptions | where TimeGenerated >= datetime({self.since})
        | project at = TimeGenerated, OperationId, source = 'exception', severity = SeverityLevel, category = ExceptionType,
            text = strcat(OuterType, ': ', OuterMessage, iff(isempty(InnermostMessage) or InnermostMessage == OuterMessage, '', strcat(' | inner ', InnermostType, ': ', InnermostMessage))))
| where isnotempty(OperationId)
| join kind=inner turns on OperationId
| project requestId, at, source, severity, category, text
| order by at asc
""")
        by_request: dict[str, list] = {}
        for row in rows:
            by_request.setdefault(row.pop("requestId"), []).append(row)
        log(f"server warnings and exceptions: {len(rows)} in {len(by_request)} turns")
        return by_request

    # ---------- Foundry project Responses API (diagnostics) ----------
    def response(self, response_id: str) -> dict:
        with self.cache_lock:
            if response_id in self.responses:
                return self.responses[response_id]
        try:
            value = self.http.json(f"{self.project}/openai/v1/responses/{response_id}", "https://ai.azure.com")
        except urllib.error.HTTPError as error:
            value = {"_error": error.code}
        with self.cache_lock:
            self.responses[response_id] = value
        return value

    def input_items(self, response_id: str) -> list | None:
        items, after = [], None
        try:
            while True:
                query = "limit=100&order=asc" + (f"&after={after}" if after else "")
                page = self.http.json(f"{self.project}/openai/v1/responses/{response_id}/input_items?{query}", "https://ai.azure.com")
                items.extend(page.get("data") or [])
                after = page.get("last_id")
                if not page.get("has_more") or not after:
                    return items
        except urllib.error.HTTPError:
            return None

    def remember(self, store: dict, value, prefix: str) -> str:
        text = value if isinstance(value, str) else json.dumps(value, sort_keys=True)
        digest = hashlib.sha256(text.encode()).hexdigest()[:16]
        with self.cache_lock:
            store.setdefault(digest, value)
        return f"{prefix}/{digest}"

    def model_context(self, candidate: dict, turns: list[dict]) -> list[dict]:
        """The stored model context of every response chain: the conversation's latest response, plus the last response of
        any turn a later chain does not contain (context compaction, a stopped or failed turn)."""
        meta = self.scm("vfs", f"{self.home}/{candidate['ownerPath']}/sessions/{candidate['session']}/session.json")
        head = ((json.loads(meta) if meta else {}).get("agentSession") or {}).get("conversationId") or ""
        chains, covered = [], set()

        def add(response_id: str, reason: str):
            response = self.response(response_id)
            items = None if "_error" in response else self.input_items(response_id)
            if items is None:
                chains.append({"response": self.p.response(response_id), "reason": reason, "unavailable": response.get("_error", "input_items")})
                return
            user_texts = [normalize(message_text(i)) for i in items if i.get("type") == "message" and i.get("role") == "user"]
            for index, turn in enumerate(turns):
                text = normalize(turn["question"])[:4000]
                if text and any(text in u for u in user_texts):
                    covered.add(index)
            chains.append({
                "response": self.p.response(response_id), "reason": reason, "createdAt": response.get("created_at"),
                "model": response.get("model"), "status": response.get("status"),
                "reasoningEffort": (response.get("reasoning") or {}).get("effort"),
                "systemPrompt": self.remember(self.prompts, response.get("instructions") or "", "prompts"),
                "tools": self.remember(self.tool_sets, response.get("tools") or [], "tools"),
                "usage": response.get("usage"),
                "items": strip_private(items), "output": strip_private(response.get("output") or []),
            })

        if head.startswith("resp_"):
            add(head, "latest response of the conversation")
        for index in reversed(range(len(turns))):
            ids = [r["span"]["responseId"] for r in turns[index]["runs"] if r["span"]["responseId"]]
            if index not in covered and ids:
                add(ids[-1], f"turn {index + 1} is not in a later response chain (context compaction, a stop or a failure)")
        return chains

    # ---------- Conversations ----------
    @staticmethod
    def streams(browser: dict | None) -> list[dict]:
        """The browser's chat streams in order: when each started, its first answer text, and how it ended."""
        streams: list[dict] = []
        for event in (browser or {}).get("events", []):
            name, properties = event["name"], event["properties"]
            if name == "chat.stream.start":
                streams.append({"start": parse_time(event["at"]), "firstTextSeconds": None, "done": None, "severed": False})
            elif streams and name == "chat.stream.firstDelta" and str(properties.get("ms", "")).isdigit():
                streams[-1]["firstTextSeconds"] = round(int(properties["ms"]) / 1000, 1)
            elif streams and name == "chat.stream.done":
                streams[-1]["done"] = properties
            elif streams and name == "chat.stream.severed":
                streams[-1]["severed"] = True
        return streams

    @staticmethod
    def run_summary(runs: list[dict]) -> dict | None:
        if not runs:
            return None
        calls = [c for r in runs for c in r["calls"]]
        total = lambda key: sum(c[key] or 0 for c in calls)
        return {
            "agentSeconds": round(sum(r["span"]["DurationMs"] or 0 for r in runs) / 1000, 1),
            "modelRounds": len(calls),
            "modelSeconds": round(total("DurationMs") / 1000, 1),
            "toolSeconds": round(sum(t["DurationMs"] or 0 for r in runs for t in r["tools"]) / 1000, 1),
            "inputTokens": total("inputTokens"), "cachedInputTokens": total("cachedTokens"),
            "outputTokens": total("outputTokens"), "reasoningTokens": total("reasoningTokens"),
            "model": (calls[-1]["model"] if calls else runs[-1]["span"]["model"]) or None,
            "lastFinishReason": (calls[-1]["finish"] if calls else None) or None,
        }

    def build(self, candidate: dict, events: list[dict], by_request: dict, by_call: dict, browser: dict | None) -> tuple[dict, list]:
        """The rating record of one conversation (not yet pseudonymised) and its turns with their agent runs."""
        args = self.args
        turns, ratings, visible = [], {}, 0
        for event in events:
            kind = event.get("type")
            if kind == "feedback":
                ratings[event.get("turn")] = event.get("rating")
            elif kind == "user":
                context, question = split_user_message(event.get("content"))
                visible += 1 if question else 0
                turns.append({"at": event.get("timestamp"), "context": context, "question": question,
                              "number": visible if question else None, "events": []})
            elif kind != "thinking" and turns:
                turns[-1]["events"].append(event)

        records, streams, output = list(candidate["records"]), self.streams(browser), []
        for index, turn in enumerate(turns):
            at = parse_time(turn["at"])
            record = take_nearest(records, at, lambda r: parse_time(r["StartedUtc"]))
            stream = take_nearest(streams, at, lambda s: s["start"])
            calls, by_id, answers, approvals, errors, consent, last = [], {}, [], [], [], [], at
            for event in turn["events"]:
                if event.get("timestamp"):
                    last = max(last, parse_time(event["timestamp"]))
                kind = event.get("type")
                if kind == "tool_start":
                    by_id[event.get("callId")] = call = {"tool": event.get("toolName"),
                                                          "arguments": arguments_view(event.get("arguments"), args.max_arguments)}
                    calls.append(call)
                elif kind == "tool_done":
                    call = by_id.get(event.get("callId"))
                    if call is None:
                        by_id[event.get("callId")] = call = {"tool": None, "arguments": None}
                        calls.append(call)
                    result = event.get("result") or ""
                    # A consent_required result is shown to the user live as a button labelled with the access to grant.
                    if '"consent_required"' in result[:400]:
                        consent.extend(label for label in re.findall(r'"label"\s*:\s*"([^"]+)"', result) if label not in consent)
                    call.update(success=event.get("success"),
                                seconds=round(event["durationMs"] / 1000, 1) if event.get("durationMs") is not None else None,
                                error=bounded(event.get("error"), 2000), resultCharacters=len(result),
                                result=bounded(result, args.max_tool_result) if result else None)
                elif kind == "assistant":
                    answers.append(event.get("content"))
                elif kind == "approval_request":
                    approvals.append({"tool": event.get("toolName"), "request": arguments_view(event.get("arguments"), args.max_arguments)})
                elif kind == "error":
                    errors.append({"message": event.get("message"), "code": event.get("code")})
            runs = by_request.get(record["RequestId"], []) if record else []
            if not runs:
                linked = {id(r): r for r in (by_call.get(call_id) for call_id in by_id) if r}
                runs = list(linked.values())
            turn.update(runs=runs, requestId=record["RequestId"] if record else None)
            done = (stream or {}).get("done") or {}
            output.append({
                "turn": index + 1,
                "at": turn["at"],
                "scheduledRun": bool(record and record.get("Scheduled")),
                "question": turn["question"],
                "hostContext": bounded(turn["context"], args.max_context),
                "toolCalls": calls,
                "approvalRequests": approvals,
                "answers": answers,
                "visibleOutputs": [c["tool"] for c in calls if c["tool"] in VISIBLE_OUTPUT_TOOLS and c.get("success")]
                                  + [f"consent button: {label}" for label in consent],
                "errors": errors,
                "outcome": {"status": record.get("Status"), "toolsFailed": record.get("ToolsFailed"),
                            "artifacts": len(record.get("ArtifactIds") or [])} if record else None,
                "seconds": round((last - at).total_seconds(), 1),
                # renderedLive: shown in the open conversation; switchedAway: the user was viewing another conversation
                # when it finished (it shows when they return); severed: the live stream broke and the app recovers the saved answer.
                "browser": {"firstTextSeconds": stream["firstTextSeconds"], "severed": stream["severed"],
                            "renderedLive": done.get("committedToView") == "true" and done.get("renderable") == "true" if done else None,
                            "switchedAway": done.get("committedToView") == "false" if done else None}
                if stream else None,
                "run": self.run_summary(runs),
                "userRating": ratings.get(turn["number"]) if turn["number"] else None,
            })
        rated = Counter(t["userRating"] for t in output if t["userRating"] in ("up", "down"))
        record = {
            "clientCountries": sorted((browser or {}).get("countries", [])) or None,
            "started": output[0]["at"] if output else None,
            "lastActivity": max((e.get("timestamp") for e in events if e.get("timestamp")), default=None),
            "turnCount": len(output),
            "userRatings": {"up": rated["up"], "down": rated["down"]},
            "turns": output,
        }
        return record, turns

    def export(self):
        args = self.args
        out = Path(args.out).resolve()
        repo = Path(__file__).resolve().parents[4]
        if out == repo or repo in out.parents:
            raise SystemExit("--out must be outside the repository")
        (out / "conversations").mkdir(parents=True, exist_ok=True)

        loaders = {"records": self.turn_records, "entra": self.entra_index, "runs": self.agent_runs, "browser": self.browser_telemetry}
        if args.diagnostics:
            loaders["serverLogs"] = self.server_logs
        with cf.ThreadPoolExecutor(len(loaders)) as pool:
            def timed(name, loader):
                begun = time.time()
                value = loader()
                log(f"  {name} loaded in {time.time() - begun:.1f} s")
                return value
            futures = {name: pool.submit(timed, name, loader) for name, loader in loaders.items()}
            loaded = {name: future.result() for name, future in futures.items()}
        records, entra, browser = loaded["records"], loaded["entra"], loaded["browser"]
        by_request, by_call = loaded["runs"]
        self.mark("load records and telemetry")

        flagged_owners = {str(items[0].get("Owner")) for sid, items in records.items()
                          if (b := browser.get(sid)) and (b["excludedCountry"] or b["excludedBrowser"])}
        excluded: Counter = Counter()

        def exclusion(sid: str, items: list) -> str | None:
            b = browser.get(sid)
            if b and b["excludedCountry"]:
                return "client in an excluded country"
            if b and b["excludedBrowser"]:
                return "browser also used from an excluded country"
            if str(items[0].get("Owner")) in flagged_owners:
                return "owner also used the app from an excluded country"
            if b is None and args.exclude_unknown_country:
                return "no browser telemetry"
            return None

        candidates = []
        for sid, items in records.items():
            if reason := exclusion(sid, items):
                excluded[reason] += 1
                continue
            owner = str(items[0].get("Owner"))
            path, name, kind = entra.get(sid) or (f"anon/{owner}", owner, "anonymous")
            candidates.append({"session": sid, "ownerPath": path, "owner": name, "ownerKind": kind, "records": items})

        def fetch(candidate):
            return candidate, self.scm("vfs", f"{self.home}/{candidate['ownerPath']}/sessions/{candidate['session']}/events.jsonl")

        with cf.ThreadPoolExecutor(PARALLEL) as pool:
            fetched = list(pool.map(fetch, candidates))
        self.mark("download transcripts")

        kept, dropped = [], []
        for candidate, body in fetched:
            if body is None:
                excluded["deleted by the user"] += 1
                continue
            events = []
            for line in body.decode("utf-8", "replace").splitlines():
                try:
                    events.append(json.loads(line)) if line.strip() else None
                except json.JSONDecodeError:
                    pass
            if not args.include_probes and any(e.get("type") == "user" and SMOKE_PROMPT in str(e.get("content", "")) for e in events):
                excluded["deployment smoke check"] += 1
                continue
            content, turns = self.build(candidate, events, by_request, by_call, browser.get(candidate["session"]))
            if not content["turnCount"]:
                excluded["no user turn"] += 1
                continue
            record = {"conversation": self.p.guid(candidate["session"]), "owner": self.p.owner(candidate["owner"]),
                      "ownerKind": candidate["ownerKind"]} | self.p.deep(content)
            characters = len(json.dumps(record, ensure_ascii=False))
            if characters > args.max_chars:
                excluded["record larger than --max-chars"] += 1
                dropped.append({"turns": record["turnCount"], "characters": characters})
                continue
            kept.append((record, characters, candidate, turns))
        kept.sort(key=lambda item: str(item[0]["lastActivity"]), reverse=True)
        if args.latest:
            kept = kept[: args.latest]
        self.mark("build records")

        if args.diagnostics:
            def diagnose(item):
                record, _, candidate, turns = item
                record["diagnostics"] = self.p.deep({
                    "modelContext": self.model_context(candidate, turns),
                    "browserEvents": (browser.get(candidate["session"]) or {}).get("events", []),
                    "turns": [{"turn": n, "serverLogs": loaded["serverLogs"].get(turn["requestId"], []) if turn["requestId"] else [],
                               "modelCalls": [{k: c[k] for k in ("TimeGenerated", "DurationMs", "finish", "model", "inputTokens", "cachedTokens",
                                                                  "outputTokens", "reasoningTokens", "firstChunkSeconds")}
                                              | {"response": self.p.response(c["responseId"]) if c["responseId"] else None}
                                              for r in turn["runs"] for c in r["calls"]],
                               "toolSpans": [{k: t[k] for k in ("TimeGenerated", "DurationMs", "Success", "tool", "callId", "errorType")}
                                             for r in turn["runs"] for t in r["tools"]]}
                              for n, turn in enumerate(turns, 1)],
                })

            with cf.ThreadPoolExecutor(PARALLEL) as pool:
                list(pool.map(diagnose, kept))
            for folder, store, suffix in (("prompts", self.prompts, ".txt"), ("tools", self.tool_sets, ".json")):
                (out / folder).mkdir(exist_ok=True)
                for digest, value in store.items():
                    text = value if isinstance(value, str) else json.dumps(value, indent=1)
                    (out / folder / f"{digest}{suffix}").write_text(self.p.text(text), encoding="utf-8")
            self.mark("diagnostics")

        manifest = []
        for record, characters, _, _ in kept:
            (out / "conversations" / f"{record['conversation']}.json").write_text(
                json.dumps(record, ensure_ascii=False, indent=1, default=str), encoding="utf-8")
            turns = record["turns"]
            manifest.append({
                "conversation": record["conversation"], "owner": record["owner"], "ownerKind": record["ownerKind"],
                "clientCountries": record["clientCountries"], "started": record["started"], "lastActivity": record["lastActivity"],
                "turns": record["turnCount"], "toolCalls": sum(len(t["toolCalls"]) for t in turns),
                "failedToolCalls": sum(1 for t in turns for c in t["toolCalls"] if c.get("success") is False),
                "turnsWithError": sum(1 for t in turns if t["errors"]), "turnsWithoutAnswer": sum(1 for t in turns if not t["answers"]),
                "userRatings": record["userRatings"], "characters": characters,
                "firstQuestion": bounded(next((t["question"] for t in turns if t["question"]), None), 160),
            })
        with open(out / "manifest.jsonl", "w", encoding="utf-8") as handle:
            for row in manifest:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        if self.p.mapping is not None:
            (out / "private-mapping.json").write_text(json.dumps(self.p.mapping, indent=1), encoding="utf-8")
        self.mark("write")
        summary = {
            "exportedUtc": datetime.now(timezone.utc).isoformat(), "windowDays": args.days, "cutoffUtc": self.cutoff.isoformat(),
            "conversations": len(manifest), "turns": sum(r["turns"] for r in manifest),
            "distinctOwners": len({r["owner"] for r in manifest}),
            "byOwnerKind": dict(Counter(r["ownerKind"] for r in manifest)),
            "clientCountryKnown": sum(1 for r in manifest if r["clientCountries"]),
            "byClientCountry": dict(Counter(c for r in manifest for c in r["clientCountries"] or []).most_common()),
            "excludedCountries": args.exclude_country, "excluded": dict(excluded), "droppedAsTooLarge": dropped,
            "userRatings": {"up": sum(r["userRatings"]["up"] for r in manifest), "down": sum(r["userRatings"]["down"] for r in manifest)},
            "limits": {"maxChars": args.max_chars, "maxToolResult": args.max_tool_result, "maxArguments": args.max_arguments},
            "diagnostics": args.diagnostics, "contentPseudonymised": not args.no_redact_content,
            "seconds": round(time.time() - self.started, 1), "phaseSeconds": self.phases,
        }
        (out / "summary.json").write_text(json.dumps(summary, indent=1, ensure_ascii=False), encoding="utf-8")
        log("summary: " + json.dumps(summary, ensure_ascii=False))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", required=True, help="Output folder outside the repository")
    parser.add_argument("--days", type=int, default=30, help="Conversations with a turn in this many days (turn records and the Responses API keep about 30)")
    parser.add_argument("--latest", type=int, default=0, help="Only the N most recently active conversations (default: all)")
    parser.add_argument("--environment", help="azd environment name (default: the default environment)")
    parser.add_argument("--agent-home", help="The app's AGENT_HOME under /home (default: /home/copilot when present, else read from the app settings)")
    parser.add_argument("--max-chars", type=int, default=300_000,
                        help="Leave out conversations whose rating record is longer (default 300,000 characters, about 75,000 tokens)")
    parser.add_argument("--max-tool-result", type=int, default=6_000, help="Characters kept of each tool result (default 6,000)")
    parser.add_argument("--max-arguments", type=int, default=8_000, help="Characters kept of each tool call's arguments (default 8,000)")
    parser.add_argument("--max-context", type=int, default=4_000, help="Characters kept of each injected context block (default 4,000)")
    parser.add_argument("--exclude-country", action="append", default=None,
                        help="Leave out conversations whose browser telemetry came from this country, plus that browser's and owner's "
                             "other conversations; repeatable (default: Denmark)")
    parser.add_argument("--no-country-filter", action="store_true", help="Keep every country")
    parser.add_argument("--exclude-unknown-country", action="store_true", help="Also leave out conversations without browser telemetry")
    parser.add_argument("--include-probes", action="store_true", help="Keep the post-deployment smoke check conversations")
    parser.add_argument("--diagnostics", action="store_true",
                        help="Add the Responses API model context, model calls, tool spans, server warnings and browser events")
    parser.add_argument("--keep-mapping", action="store_true", help="Write private-mapping.json (aliases to real identifiers)")
    parser.add_argument("--no-redact-content", action="store_true", help="Keep GUIDs, e-mails and response IDs in content; credentials stay redacted")
    args = parser.parse_args()
    args.exclude_country = [] if args.no_country_filter else (args.exclude_country or ["Denmark"])
    Exporter(args).export()


if __name__ == "__main__":
    main()
