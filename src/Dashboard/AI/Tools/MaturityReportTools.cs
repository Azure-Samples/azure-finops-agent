using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Generates a deep, print-style FinOps maturity ASSESSMENT REPORT (one scrolling
/// self-contained .html document — not a slide deck). Models the canonical FinOps
/// Foundation framework: 4 domains, up to 19 capabilities, each with score, stage,
/// priority, effort, per-capability maturity definitions, multi-bullet evidence,
/// per-subscription breakdown, and a recommendation. Also renders a maturity-stage
/// distribution, priority-actions summary, domain overview, a rich per-subscription
/// matrix, a phased roadmap, and a data-source appendix.
///
/// Reuses HtmlPresentationTools.GeneratedFiles + the __HTML_READY__ marker so the SSE
/// handler, /api/download/html/{id} endpoint, frontend download card, and 30-min
/// cleanup all work unchanged.
/// </summary>
public static class MaturityReportTools
{
    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GenerateMaturityReport, "GenerateMaturityReport",
            @"Generates a DEEP, evidence-based FinOps maturity ASSESSMENT REPORT as one scrolling, print/PDF-friendly HTML document. Use this — NOT GenerateHtmlPresentation — whenever the user wants a 'maturity assessment', 'full FinOps assessment', 'FinOps Foundation report', or a board/exec maturity report with depth.

This renders the canonical FinOps Foundation framework: 4 DOMAINS — 'Understand Cloud Usage & Cost', 'Quantify Business Value', 'Optimize Cloud Usage & Cost', 'Manage the FinOps Practice' — and up to 19 CAPABILITIES. Score every capability 0-5 from REAL Azure API data before calling (Resource Graph, Cost Management query+forecast, Consumption budgets, Advisor, Policy, Locks, Reservations/Capacity, Cost exports, Graph, Log Analytics).

MANDATORY for credibility — every capability MUST include: concrete EVIDENCE bullets with real numbers (counts, %, $), a per-capability priority (CRITICAL/HIGH/MEDIUM/LOW), an effort estimate, the Crawl/Walk/Run definitions for THAT capability, and a per-subscription score breakdown. No empty evidence. Never invent numbers — only report what the APIs returned. Prefer projected EOM cost (Cost Management Forecast) for the headline spend.

The 19 canonical capabilities (group under the 4 domains):
- Understand: Data Ingestion & Normalization, Cost Allocation, Reporting & Analytics, Anomaly Management.
- Quantify: Planning & Forecasting, Budgeting, Benchmarking / Unit Economics.
- Optimize: Architecting for Cloud, Rate Optimization (commitments), Workload Optimization (right-sizing), Cloud Sustainability, Licensing & SaaS.
- Manage: FinOps Practice Operations, Onboarding Workloads, FinOps Education, Policy & Governance, Invoicing & Chargeback, FinOps Assessment, Intersecting Frameworks (+ FinOps Tools & Services).

Returns a __HTML_READY__ marker; the UI shows a download card and inline viewer.");
    }

    private static Task<string> GenerateMaturityReport(
        [Description(@"JSON object describing the full assessment. SCHEMA:
{
  ""customer"": ""Contoso"",                       // optional
  ""assessmentDate"": ""June 7, 2026"",
  ""scope"": { ""subscriptions"": 10, ""monthlySpend"": ""$1,793.67 (projected EOM)"" },
  ""overall"": { ""score"": 12, ""maxScore"": 95, ""percent"": 13, ""stage"": ""Crawl"", ""notStarted"": 8, ""walkOrRun"": 0 },
  ""executiveSummary"": [ ""3-5 bullet strings, verdict + biggest evidence"" ],
  ""stageDistribution"": [ { ""stage"": ""Not Started"", ""range"": ""0"", ""count"": 8, ""percent"": 42, ""description"": ""..."" } ],
  ""priorityActions"":  [ { ""priority"": ""CRITICAL"", ""count"": 3, ""keyActions"": ""Cost Allocation, Rate Optimization, FinOps Practice"" } ],
  ""domains"": [ { ""name"": ""Understand Cloud Usage & Cost"", ""capabilities"": 4, ""avgScore"": 0.2, ""stage"": ""Not Started"" } ],
  ""capabilities"": [ {
      ""domain"": ""Understand Cloud Usage & Cost"",
      ""name"": ""Cost Allocation"",
      ""score"": 0, ""stage"": ""Not Started"", ""priority"": ""CRITICAL"", ""effort"": ""Medium (1-2 weeks)"",
      ""definitions"": { ""crawl"": ""..."", ""walk"": ""..."", ""run"": ""..."" },
      ""evidence"": [ ""0% governance tag coverage across all 10 subs"", ""Top sub has 4.1% tag rate"" ],
      ""perSubscription"": [ { ""name"": ""sub-5"", ""score"": 0 }, { ""name"": ""sub-3"", ""score"": 1 } ],
      ""recommendation"": ""Enforce tag policy; configure cost allocation rules.""
  } ],
  ""subscriptions"": [ { ""name"": ""sub-5"", ""eomCost"": ""$1,684"", ""resources"": 73, ""tags"": ""4.1%"", ""law"": 3, ""locks"": 0, ""alerts"": 1, ""avgScore"": ""1.2/5"", ""percentSpend"": ""93.9%"" } ],
  ""subscriptionNote"": ""Subscription Sprawl Alert: 6 subs <$1/mo each carry a $3K budget."",
  ""roadmap"": [ { ""phase"": ""Phase 1: Foundation"", ""timeframe"": ""Weeks 1-4"", ""target"": ""20/95"",
                  ""actions"": [ { ""action"": ""Implement governance tagging"", ""capability"": ""Cost Allocation"", ""priority"": ""CRITICAL"", ""effort"": ""Medium"", ""impact"": ""Enables all cost attribution"" } ] } ],
  ""dataSources"": [ { ""source"": ""Resource Inventory"", ""api"": ""Azure Resource Graph"", ""finding"": ""186 resources across 10 subs"" } ]
}
Only 'capabilities' is strictly required; every other section renders when present. Provide as many as you have real data for.")] string reportJson,
        [Description("Filename without extension. Default 'FinOps-Maturity-Assessment'.")] string? filename = null,
        [Description("Optional customer/tenant name (overrides reportJson.customer for chrome).")] string? customer = null)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
            return Task.FromResult("Error: reportJson is required.");

        HtmlPresentationTools.CleanupOldFiles();

        JsonElement root;
        try { root = JsonDocument.Parse(reportJson).RootElement; }
        catch (JsonException jex) { return Task.FromResult($"Error: invalid reportJson — {jex.Message}"); }
        if (root.ValueKind != JsonValueKind.Object)
            return Task.FromResult("Error: reportJson must be a JSON object.");

        using var activity = HttpHelper.Telemetry.StartActivity("GenerateMaturityReport");

        var cust = !string.IsNullOrWhiteSpace(customer) ? customer : Str(root, "customer");
        var body = BuildBody(root, cust, out var capabilityCount);
        var title = string.IsNullOrWhiteSpace(cust)
            ? "FinOps Maturity Assessment"
            : $"{cust} · FinOps Maturity Assessment";
        var html = BuildShell(title, body);

        var fileId = Guid.NewGuid().ToString("N")[..12];
        var safeName = TempFileHelper.SanitizeFilename(filename ?? "FinOps-Maturity-Assessment", "FinOps-Maturity-Assessment");
        var outputPath = Path.Combine(Path.GetTempPath(), $"{fileId}_{safeName}.html");
        File.WriteAllText(outputPath, html, new UTF8Encoding(false));

        HtmlPresentationTools.GeneratedFiles[fileId] = (outputPath, DateTime.UtcNow, HttpHelper.CurrentTurnUserId());
        activity?.SetTag("report.capabilities", capabilityCount);

        // slideCount slot doubles as the section/capability count shown on the card.
        return Task.FromResult($"__HTML_READY__:{fileId}:{safeName}.html:{capabilityCount} capabilities");
    }

    // ────────────────────────────────────────────────────────────────────
    // Body sections
    // ────────────────────────────────────────────────────────────────────

    private static string BuildBody(JsonElement root, string? customer, out int capabilityCount)
    {
        var sb = new StringBuilder();
        capabilityCount = 0;

        // Header
        var date = Str(root, "assessmentDate");
        var scope = Obj(root, "scope");
        var subs = scope is { } sc && sc.TryGetProperty("subscriptions", out var sv) ? sv.ToString() : "";
        var spend = scope is { } sc2 ? Str(sc2, "monthlySpend") : "";
        sb.Append("<header class='report-header'>");
        sb.Append($"<h1>{Enc(string.IsNullOrWhiteSpace(customer) ? "FinOps Foundation Maturity Assessment" : customer + " — FinOps Maturity Assessment")}</h1>");
        sb.Append("<div class='subtitle'>Crawl / Walk / Run framework — capabilities scored 0–5 from live Azure data</div>");
        sb.Append("<div class='header-meta'>");
        if (!string.IsNullOrWhiteSpace(date)) sb.Append(MetaItem("Assessment Date", date));
        if (!string.IsNullOrWhiteSpace(subs)) sb.Append(MetaItem("Scope", $"{subs} Azure Subscriptions"));
        if (!string.IsNullOrWhiteSpace(spend)) sb.Append(MetaItem("Monthly Spend", spend));
        sb.Append("</div></header>");

        // Score banner
        var overall = Obj(root, "overall");
        if (overall is { } ov)
        {
            var score = Str(ov, "score");
            var max = Str(ov, "maxScore");
            var pct = Str(ov, "percent");
            var stage = Str(ov, "stage");
            var notStarted = Str(ov, "notStarted");
            var walkRun = Str(ov, "walkOrRun");
            sb.Append("<div class='score-banner'>");
            if (score.Length > 0)
                sb.Append(ScoreCard(max.Length > 0 ? $"{score}/{max}" : score,
                    pct.Length > 0 ? $"Overall Score ({pct}%)" : "Overall Score", "critical"));
            if (stage.Length > 0) sb.Append(ScoreCard(stage.ToUpperInvariant(), "Maturity Stage", "critical", isText: true));
            if (notStarted.Length > 0) sb.Append(ScoreCard(notStarted, "Not Started", "warning"));
            if (walkRun.Length > 0) sb.Append(ScoreCard(walkRun, "Walk or Run", null));
            sb.Append("</div>");
        }

        // Executive summary + stage distribution + priority actions
        var exec = Arr(root, "executiveSummary");
        var stageDist = Arr(root, "stageDistribution");
        var priorityActions = Arr(root, "priorityActions");
        if (exec is not null || stageDist is not null || priorityActions is not null)
        {
            sb.Append("<div class='card'><h2>Executive Summary</h2>");
            if (exec is { } ex)
            {
                sb.Append("<ul class='exec-list'>");
                foreach (var b in ex.EnumerateArray())
                    if (b.ValueKind == JsonValueKind.String) sb.Append($"<li>{Enc(b.GetString())}</li>");
                sb.Append("</ul>");
            }
            if (stageDist is { } sd)
            {
                sb.Append("<h3>Maturity Stage Distribution</h3><table><thead><tr><th>Stage</th><th>Score Range</th><th>Count</th><th>%</th><th>Description</th></tr></thead><tbody>");
                foreach (var r in sd.EnumerateArray())
                {
                    sb.Append("<tr>");
                    sb.Append($"<td>{StageBadge(Str(r, "stage"))}</td>");
                    sb.Append($"<td>{Enc(Str(r, "range"))}</td>");
                    sb.Append($"<td>{Enc(Str(r, "count"))}</td>");
                    sb.Append($"<td>{Enc(Str(r, "percent"))}{(Str(r, "percent").Length > 0 ? "%" : "")}</td>");
                    sb.Append($"<td>{Enc(Str(r, "description"))}</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
            }
            if (priorityActions is { } pa)
            {
                sb.Append("<h3>Priority Actions Required</h3><table><thead><tr><th>Priority</th><th>Count</th><th>Key Actions</th></tr></thead><tbody>");
                foreach (var r in pa.EnumerateArray())
                {
                    sb.Append("<tr>");
                    sb.Append($"<td>{PriorityBadge(Str(r, "priority"))}</td>");
                    sb.Append($"<td>{Enc(Str(r, "count"))}</td>");
                    sb.Append($"<td>{Enc(Str(r, "keyActions"))}</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</div>");
        }

        // Domain overview
        var domains = Arr(root, "domains");
        if (domains is { } dm)
        {
            sb.Append("<div class='card'><h2>Domain Scores Overview</h2><table><thead><tr><th>Domain</th><th>Capabilities</th><th>Avg Score</th><th>Stage</th><th>Visual</th></tr></thead><tbody>");
            foreach (var d in dm.EnumerateArray())
            {
                var avg = Num(d, "avgScore");
                sb.Append("<tr>");
                sb.Append($"<td><strong>{Enc(Str(d, "name"))}</strong></td>");
                sb.Append($"<td>{Enc(Str(d, "capabilities"))}</td>");
                sb.Append($"<td>{avg:0.0}/5</td>");
                sb.Append($"<td>{StageBadge(Str(d, "stage"))}</td>");
                sb.Append($"<td>{ScoreBar(avg)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table></div>");
        }

        // Capability score chart (Chart.js) — all capabilities, lowest first
        var caps = Arr(root, "capabilities");
        string? chartScript = null;
        if (caps is { } capsForChart)
        {
            var pairs = new List<(string label, double score)>();
            foreach (var c in capsForChart.EnumerateArray())
                pairs.Add((Str(c, "name"), Num(c, "score")));
            if (pairs.Count >= 3)
            {
                pairs = pairs.OrderBy(p => p.score).ToList();
                var labels = JsonSerializer.Serialize(pairs.Select(p => p.label));
                var values = JsonSerializer.Serialize(pairs.Select(p => p.score));
                var height = Math.Max(320, pairs.Count * 34);
                sb.Append("<div class='card'><h2>Capability Scores — Lowest First</h2>");
                sb.Append($"<div style='position:relative;height:{height}px'><canvas id='capChart'></canvas></div></div>");
                chartScript =
                    "new Chart(document.getElementById('capChart'),{type:'bar',data:{labels:" + labels +
                    ",datasets:[{label:'Score (0-5)',data:" + values +
                    ",backgroundColor:'#0078d4',borderRadius:3}]},options:{indexAxis:'y',responsive:true,maintainAspectRatio:false," +
                    "plugins:{legend:{display:false}},scales:{x:{min:0,max:5,ticks:{stepSize:1}}}}});";
            }
        }

        // Detailed capabilities grouped by domain
        if (caps is { } cps)
        {
            sb.Append("<div class='card'><h2>Detailed Capability Assessment</h2>");
            sb.Append("<p class='muted'>Each capability is scored 0–5 with maturity-stage definitions, evidence from Azure APIs, a per-subscription breakdown, and a prioritized recommendation.</p>");

            string? currentDomain = null;
            foreach (var c in cps.EnumerateArray())
            {
                capabilityCount++;
                var domain = Str(c, "domain");
                if (!string.IsNullOrWhiteSpace(domain) && domain != currentDomain)
                {
                    currentDomain = domain;
                    sb.Append($"<div class='domain-header'><h3>{Enc(domain)}</h3></div>");
                }
                sb.Append(RenderCapability(c));
            }
            sb.Append("</div>");
        }

        // Per-subscription summary
        var subsArr = Arr(root, "subscriptions");
        if (subsArr is { } su)
        {
            sb.Append("<div class='card'><h2>Per-Subscription Summary</h2>");
            sb.Append("<table><thead><tr><th>Subscription</th><th>Cost</th><th>Resources</th><th>Tags</th><th>LAW</th><th>Locks</th><th>Alerts</th><th>Avg Score</th><th>% of Spend</th></tr></thead><tbody>");
            foreach (var s in su.EnumerateArray())
            {
                sb.Append("<tr>");
                sb.Append($"<td>{Enc(Str(s, "name"))}</td>");
                sb.Append($"<td>{Enc(Str(s, "eomCost"))}</td>");
                sb.Append($"<td>{Enc(Str(s, "resources"))}</td>");
                sb.Append($"<td>{Enc(Str(s, "tags"))}</td>");
                sb.Append($"<td>{Enc(Str(s, "law"))}</td>");
                sb.Append($"<td>{Enc(Str(s, "locks"))}</td>");
                sb.Append($"<td>{Enc(Str(s, "alerts"))}</td>");
                sb.Append($"<td>{Enc(Str(s, "avgScore"))}</td>");
                sb.Append($"<td>{Enc(Str(s, "percentSpend"))}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
            var note = Str(root, "subscriptionNote");
            if (note.Length > 0) sb.Append($"<div class='callout'>{Enc(note)}</div>");
            sb.Append("</div>");
        }

        // Roadmap
        var roadmap = Arr(root, "roadmap");
        if (roadmap is { } rm)
        {
            sb.Append("<div class='card'><h2>Improvement Roadmap: Crawl → Run</h2>");
            foreach (var phase in rm.EnumerateArray())
            {
                var target = Str(phase, "target");
                sb.Append("<div class='phase'><div class='phase-header'>");
                sb.Append($"<span><strong>{Enc(Str(phase, "phase"))}</strong>{(Str(phase, "timeframe").Length > 0 ? " — " + Enc(Str(phase, "timeframe")) : "")}</span>");
                if (target.Length > 0) sb.Append($"<span>Target: {Enc(target)}</span>");
                sb.Append("</div><div class='phase-body'>");
                var actions = Arr(phase, "actions");
                if (actions is { } ac)
                {
                    sb.Append("<table style='margin:0'><thead><tr><th>#</th><th>Action</th><th>Capability</th><th>Priority</th><th>Effort</th><th>Impact</th></tr></thead><tbody>");
                    var i = 1;
                    foreach (var a in ac.EnumerateArray())
                    {
                        sb.Append("<tr>");
                        sb.Append($"<td>{i++}</td>");
                        sb.Append($"<td>{Enc(Str(a, "action"))}</td>");
                        sb.Append($"<td>{Enc(Str(a, "capability"))}</td>");
                        sb.Append($"<td>{PriorityBadge(Str(a, "priority"))}</td>");
                        sb.Append($"<td>{Enc(Str(a, "effort"))}</td>");
                        sb.Append($"<td>{Enc(Str(a, "impact"))}</td>");
                        sb.Append("</tr>");
                    }
                    sb.Append("</tbody></table>");
                }
                sb.Append("</div></div>");
            }
            sb.Append("</div>");
        }

        // Appendix: data sources
        var dataSources = Arr(root, "dataSources");
        if (dataSources is { } ds)
        {
            sb.Append("<div class='card'><h2>Appendix: Evidence Data Sources</h2><table><thead><tr><th>Data Source</th><th>API / Method</th><th>Key Finding</th></tr></thead><tbody>");
            foreach (var d in ds.EnumerateArray())
            {
                sb.Append("<tr>");
                sb.Append($"<td>{Enc(Str(d, "source"))}</td>");
                sb.Append($"<td>{Enc(Str(d, "api"))}</td>");
                sb.Append($"<td>{Enc(Str(d, "finding"))}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
            sb.Append($"<p class='muted' style='margin-top:14px;font-style:italic'>Evidence-based assessment using live Azure API data{(date.Length > 0 ? " retrieved " + Enc(date) : "")}. No values estimated.</p>");
            sb.Append("</div>");
        }

        if (chartScript is not null)
            sb.Append($"<script>{chartScript}</script>");

        return sb.ToString();
    }

    private static string RenderCapability(JsonElement c)
    {
        var sb = new StringBuilder();
        var score = Num(c, "score");
        sb.Append("<div class='cap-card'><div class='cap-card-header'>");
        sb.Append($"<h4>{Enc(Str(c, "name"))}</h4><div class='cap-meta'>");
        sb.Append($"<span class='cap-score' style='color:{ScoreColor(score)}'>{score:0}/5</span>");
        var stage = Str(c, "stage");
        if (stage.Length > 0) sb.Append(StageBadge(stage));
        var priority = Str(c, "priority");
        if (priority.Length > 0) sb.Append(PriorityBadge(priority));
        sb.Append("</div></div>");

        var effort = Str(c, "effort");
        if (effort.Length > 0) sb.Append($"<div class='cap-effort'>Effort: {Enc(effort)}</div>");

        var defs = Obj(c, "definitions");
        if (defs is { } df)
        {
            sb.Append("<div class='maturity-defs'>");
            AppendDef(sb, "Crawl", Str(df, "crawl"), "#f57c00");
            AppendDef(sb, "Walk", Str(df, "walk"), "#66bb6a");
            AppendDef(sb, "Run", Str(df, "run"), "#2e7d32");
            sb.Append("</div>");
        }

        var evidence = Arr(c, "evidence");
        if (evidence is { } ev)
        {
            sb.Append("<h5>Evidence Collected</h5><ul class='evidence-list'>");
            foreach (var e in ev.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) sb.Append($"<li>{Enc(e.GetString())}</li>");
            sb.Append("</ul>");
        }

        var perSub = Arr(c, "perSubscription");
        if (perSub is { } ps)
        {
            sb.Append("<h5>Per-Subscription Breakdown</h5><div class='per-sub-grid'>");
            foreach (var p in ps.EnumerateArray())
                sb.Append($"<span class='per-sub-chip'>{Enc(Str(p, "name"))}: {Enc(Str(p, "score"))}/5</span>");
            sb.Append("</div>");
        }

        var rec = Str(c, "recommendation");
        if (rec.Length > 0) sb.Append($"<div class='recommendation-box'><strong>Recommendation:</strong> {Enc(rec)}</div>");

        sb.Append("</div>");
        return sb.ToString();
    }

    private static void AppendDef(StringBuilder sb, string label, string text, string color)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.Append($"<div class='def-row'><span class='def-label' style='color:{color}'>{label}</span><span>{Enc(text)}</span></div>");
    }

    // ────────────────────────────────────────────────────────────────────
    // Small render helpers
    // ────────────────────────────────────────────────────────────────────

    private static string MetaItem(string label, string value) =>
        $"<div class='meta-item'><div class='meta-label'>{Enc(label)}</div><div class='meta-value'>{Enc(value)}</div></div>";

    private static string ScoreCard(string value, string label, string? variant, bool isText = false)
    {
        var cls = variant is null ? "score-card" : $"score-card {variant}";
        var style = isText ? " style='color:var(--red)'" : "";
        return $"<div class='{cls}'><div class='score-value'{style}>{Enc(value)}</div><div class='score-label'>{Enc(label)}</div></div>";
    }

    private static string ScoreBar(double score)
    {
        var pct = Math.Clamp(score / 5d * 100d, 1, 100);
        var color = ScoreColor(score);
        return $"<div class='score-bar'><div class='score-bar-fill' style='width:{pct:0}%;background:{color}'></div></div>";
    }

    private static string StageBadge(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return "";
        var key = stage.Trim().ToLowerInvariant();
        var cls = key switch
        {
            "not started" or "notstarted" => "badge-not-started",
            "crawl" => "badge-crawl",
            "walk" => "badge-walk",
            "run" => "badge-run",
            _ => "badge-medium"
        };
        return $"<span class='badge {cls}'>{Enc(stage)}</span>";
    }

    private static string PriorityBadge(string priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return "";
        var cls = priority.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" => "badge-critical",
            "HIGH" => "badge-high",
            "MEDIUM" => "badge-medium",
            "LOW" => "badge-low",
            _ => "badge-medium"
        };
        return $"<span class='badge {cls}'>{Enc(priority)}</span>";
    }

    private static string ScoreColor(double score) =>
        score >= 4 ? "#2e7d32" : score >= 3 ? "#66bb6a" : score >= 1 ? "#f57c00" : "#d32f2f";

    // ────────────────────────────────────────────────────────────────────
    // JSON accessors
    // ────────────────────────────────────────────────────────────────────

    private static string Str(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static double Num(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return 0d;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var sd)) return sd;
        return 0d;
    }

    private static JsonElement? Obj(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static JsonElement? Arr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    private static string Enc(string? s) => string.IsNullOrEmpty(s)
        ? ""
        : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ────────────────────────────────────────────────────────────────────
    // Document shell (Azure-branded, print/PDF friendly)
    // ────────────────────────────────────────────────────────────────────

    private static string BuildShell(string title, string body) =>
        ShellTemplate
            .Replace("__TITLE__", Enc(title))
            .Replace("__BODY__", body);

    private const string ShellTemplate =
"""
<!DOCTYPE html>
<html lang="en"><head><meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>__TITLE__</title>
<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.min.js"></script>
<style>
:root{--azure-blue:#0078d4;--azure-dark:#003d6b;--azure-light:#e6f2ff;--red:#d32f2f;--orange:#f57c00;--green:#2e7d32;--gray-50:#fafafa;--gray-200:#e0e0e0;--gray-600:#757575;--gray-800:#424242;}
*{margin:0;padding:0;box-sizing:border-box;}
body{font-family:'Segoe UI',system-ui,-apple-system,sans-serif;color:var(--gray-800);background:#f0f2f5;line-height:1.6;}
.container{max-width:1100px;margin:0 auto;padding:20px;}
@media print{body{background:#fff;}.no-print{display:none!important;}.card{box-shadow:none!important;border:1px solid #ddd;}.page-break{page-break-before:always;}}
.report-header{background:linear-gradient(135deg,var(--azure-dark),var(--azure-blue));color:#fff;padding:40px;border-radius:12px;margin-bottom:24px;position:relative;overflow:hidden;}
.report-header::after{content:'';position:absolute;top:-50%;right:-10%;width:400px;height:400px;background:rgba(255,255,255,.05);border-radius:50%;}
.report-header h1{font-size:30px;font-weight:700;margin-bottom:4px;}
.report-header .subtitle{font-size:15px;opacity:.85;margin-bottom:20px;}
.header-meta{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px;}
.meta-item{background:rgba(255,255,255,.1);padding:10px 14px;border-radius:8px;}
.meta-label{font-size:11px;text-transform:uppercase;letter-spacing:1px;opacity:.7;}
.meta-value{font-size:15px;font-weight:600;}
.score-banner{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:16px;margin-bottom:24px;}
.score-card{background:#fff;border-radius:12px;padding:24px;text-align:center;box-shadow:0 2px 8px rgba(0,0,0,.08);border-top:4px solid var(--azure-blue);}
.score-card.critical{border-top-color:var(--red);}
.score-card.warning{border-top-color:var(--orange);}
.score-value{font-size:34px;font-weight:800;color:var(--azure-dark);}
.score-label{font-size:12px;color:var(--gray-600);text-transform:uppercase;letter-spacing:1px;margin-top:4px;}
.card{background:#fff;border-radius:12px;padding:28px;margin-bottom:20px;box-shadow:0 2px 8px rgba(0,0,0,.06);}
.card h2{font-size:22px;color:var(--azure-dark);margin-bottom:16px;padding-bottom:10px;border-bottom:2px solid var(--azure-light);}
.card h3{font-size:16px;color:var(--azure-blue);margin:16px 0 8px;}
.card h5{font-size:13px;margin:8px 0 4px;color:var(--gray-800);}
.muted{color:var(--gray-600);font-size:13px;margin-bottom:16px;}
table{width:100%;border-collapse:collapse;font-size:13px;margin:12px 0;}
thead th{background:var(--azure-blue);color:#fff;padding:10px 12px;text-align:left;font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.5px;}
tbody td{padding:9px 12px;border-bottom:1px solid var(--gray-200);vertical-align:top;}
tbody tr:nth-child(even){background:var(--gray-50);}
.exec-list{margin:6px 0 10px 20px;}
.exec-list li{margin:4px 0;font-size:14px;}
.badge{display:inline-block;padding:2px 10px;border-radius:12px;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.5px;}
.badge-critical{background:#ffebee;color:var(--red);}
.badge-high{background:#fff3e0;color:#e65100;}
.badge-medium{background:#e3f2fd;color:#1565c0;}
.badge-low{background:#e8f5e9;color:var(--green);}
.badge-not-started{background:#ffebee;color:var(--red);}
.badge-crawl{background:#fff3e0;color:#e65100;}
.badge-walk{background:#e8f5e9;color:var(--green);}
.badge-run{background:#e8f5e9;color:#1b5e20;}
.score-bar{width:120px;height:8px;background:var(--gray-200);border-radius:4px;overflow:hidden;}
.score-bar-fill{height:100%;border-radius:4px;}
.domain-header{background:linear-gradient(135deg,var(--azure-light),#fff);border-left:4px solid var(--azure-blue);padding:14px 18px;border-radius:0 10px 10px 0;margin:20px 0 12px;}
.domain-header h3{color:var(--azure-dark);margin:0;font-size:18px;}
.cap-card{border:1px solid var(--gray-200);border-radius:10px;padding:20px;margin:14px 0;}
.cap-card-header{display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;}
.cap-card-header h4{font-size:15px;color:var(--azure-dark);margin:0;}
.cap-meta{display:flex;gap:8px;flex-wrap:wrap;align-items:center;}
.cap-score{font-size:16px;font-weight:800;}
.cap-effort{font-size:12px;color:var(--gray-600);margin-top:4px;}
.maturity-defs{background:var(--gray-50);border-radius:8px;padding:12px 16px;margin:10px 0;font-size:12px;}
.def-row{display:grid;grid-template-columns:60px 1fr;gap:8px;padding:3px 0;}
.def-label{font-weight:700;}
.evidence-list{margin:8px 0;padding-left:20px;}
.evidence-list li{font-size:12px;margin:3px 0;}
.per-sub-grid{display:flex;flex-wrap:wrap;gap:6px;margin:8px 0;}
.per-sub-chip{background:#f5f5f5;padding:3px 10px;border-radius:6px;font-size:11px;}
.recommendation-box{background:linear-gradient(135deg,#e8f5e9,#f1f8e9);border-left:4px solid var(--green);padding:12px 16px;border-radius:0 8px 8px 0;margin:10px 0;font-size:13px;}
.callout{background:var(--gray-50);border-radius:8px;padding:14px;margin-top:12px;font-size:13px;}
.phase{margin-bottom:16px;}
.phase-header{background:var(--azure-blue);color:#fff;padding:12px 20px;border-radius:8px 8px 0 0;display:flex;justify-content:space-between;align-items:center;font-size:14px;}
.phase-body{border:1px solid var(--gray-200);border-top:none;border-radius:0 0 8px 8px;overflow:hidden;}
.toolbar{text-align:right;margin-bottom:12px;}
.print-btn{background:var(--azure-blue);color:#fff;border:none;padding:10px 24px;border-radius:8px;cursor:pointer;font-size:14px;font-weight:600;}
</style></head>
<body><div class="container">
<div class="toolbar no-print"><button class="print-btn" onclick="window.print()">Print / Save PDF</button></div>
__BODY__
<footer style="text-align:center;padding:24px;color:var(--gray-600);font-size:12px">
<p>Azure FinOps Agent — FinOps Foundation Maturity Assessment</p>
</footer>
</div></body></html>
""";
}
