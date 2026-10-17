#!/usr/bin/env python3
"""Where answer time goes, from an export_sessions.py --diagnostics export: prints aggregates and round sequences only.

    python latency_report.py --export <folder> [--since 2026-10-09] [--slowest 15]

Each model round costs a fixed few seconds plus its reasoning and written tokens, so the report attributes model time to
round types (evidence reads, calculations, documentation lookups, files, the final answer) and written tokens to what was
written (calculation arguments, query bodies, file rows, answer text), then lists the slowest turns round by round.
"""
from __future__ import annotations

import argparse
import json
import statistics
from collections import Counter, defaultdict
from pathlib import Path

DOCS = {"microsoft_docs_search", "microsoft_docs_fetch", "web_search"}
FILES = {"GenerateDataReport", "GenerateScript", "GenerateHtmlPresentation"}


def kind(call: dict) -> str:
    """A tool call's kind: the tool, or for QueryAzure the API it read."""
    if call["tool"] != "QueryAzure":
        return call["tool"]
    a = call.get("arguments") if isinstance(call.get("arguments"), dict) else {}
    url = str(a.get("url") or "")
    if not url:
        return "calculation"
    if a.get("combine"):
        return "combine"
    for marker, name in (("CostManagement", "Cost Management"), ("ResourceGraph", "Resource Graph"), ("prices.azure", "Retail Prices"),
                         ("loganalytics", "Log Analytics"), ("graph.microsoft", "Graph"), ("Microsoft.Insights/metrics", "Monitor metrics")):
        if marker in url:
            return name + (" (several urls)" if "\n" in url.strip() else "")
    return "public page" if url.startswith("https://") and "management.azure.com" not in url else "other ARM"


def percentile(values: list, q: float):
    values = sorted(values)
    return values[min(len(values) - 1, int(q * len(values)))] if values else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--export", required=True, help="Folder written by export_sessions.py --diagnostics")
    parser.add_argument("--since", default="", help="Only turns at or after this ISO date")
    parser.add_argument("--slowest", type=int, default=15, help="Slowest turns to list round by round")
    args = parser.parse_args()

    turns, round_time, round_count, written = [], Counter(), Counter(), Counter()
    for path in sorted(Path(args.export, "conversations").glob("*.json")):
        record = json.loads(path.read_text(encoding="utf-8"))
        diagnostics = {d["turn"]: d for d in (record.get("diagnostics") or {}).get("turns", [])}
        if not diagnostics:
            raise SystemExit("The export has no diagnostics: run export_sessions.py with --diagnostics.")
        for turn in record["turns"]:
            if turn["at"] < args.since or not turn["run"] or turn.get("scheduledRun"):
                continue
            d = diagnostics.get(turn["turn"]) or {}
            calls = d.get("modelCalls") or []
            spans = sorted(d.get("toolSpans") or [], key=lambda s: s["TimeGenerated"])
            queue = defaultdict(list)
            for call in turn["toolCalls"]:
                queue[call["tool"]].append(call)
            steps = []
            for index, call in enumerate(calls):
                end = calls[index + 1]["TimeGenerated"] if index + 1 < len(calls) else "9999"
                tools = [queue[s["tool"]].pop(0) if queue[s["tool"]] else {"tool": s["tool"]} for s in spans if call["TimeGenerated"] <= s["TimeGenerated"] < end]
                seconds = (call["DurationMs"] or 0) / 1000
                out, reasoning = call["outputTokens"] or 0, call["reasoningTokens"] or 0
                kinds = [kind(t) for t in tools]
                if not tools:
                    label = "final answer" if "stop" in (call["finish"] or "") else "other"
                elif all(t["tool"] in DOCS for t in tools):
                    label = "documentation/web only"
                elif all(k == "calculation" for k in kinds):
                    label = "calculation only"
                elif any(t["tool"] in FILES for t in tools):
                    label = "file/script/deck"
                else:
                    label = "evidence reads"
                round_time[label] += seconds
                round_count[label] += 1
                sizes = [len(json.dumps(t.get("arguments"))) if t.get("arguments") is not None else 0 for t in tools]
                for k, size in zip(kinds, sizes):
                    written[k] += max(0, out - reasoning) * (size / sum(sizes) if sum(sizes) else 1 / len(kinds))
                if label == "final answer":
                    written["answer text"] += max(0, out - reasoning)
                steps.append(f"{seconds:.0f}s:{'+'.join(sorted(set(kinds))) or label.split()[0].upper()}")
            turns.append({"seconds": turn["seconds"], "rounds": len(calls), "model": sum((c["DurationMs"] or 0) for c in calls) / 1000,
                          "agent": turn["run"]["agentSeconds"] or 0, "steps": " > ".join(steps), "at": turn["at"][:10]})

    if not turns:
        raise SystemExit("No turns in the window.")
    seconds = [t["seconds"] for t in turns]
    model = sum(t["model"] for t in turns)
    print(f"turns: {len(turns)}; seconds p50 {percentile(seconds, .5):.0f}, p75 {percentile(seconds, .75):.0f}, p90 {percentile(seconds, .9):.0f}, "
          f"max {max(seconds):.0f}; over 60 s: {sum(s > 60 for s in seconds)}, over 120 s: {sum(s > 120 for s in seconds)}")
    print(f"model rounds per turn: p50 {percentile([t['rounds'] for t in turns], .5)}, p90 {percentile([t['rounds'] for t in turns], .9)}; "
          f"model share of agent time {model / max(sum(t['agent'] for t in turns), 1):.0%}")
    total = sum(round_time.values())
    print("\nmodel time by round type:")
    for label, value in round_time.most_common():
        print(f"  {label:24} {round_count[label]:4} rounds {value / total:5.1%}  avg {value / round_count[label]:5.1f} s")
    print("\nwritten tokens by what was written (about 8 s per 1,000):")
    all_written = sum(written.values()) or 1
    for label, value in written.most_common(10):
        print(f"  {label:32} {value / all_written:5.1%}")
    print(f"\nslowest {args.slowest} turns, round by round (model seconds:what the round called):")
    for turn in sorted(turns, key=lambda t: -t["seconds"])[: args.slowest]:
        print(f"  {turn['seconds']:5.0f}s {turn['at']} | {turn['steps']}")


if __name__ == "__main__":
    main()
