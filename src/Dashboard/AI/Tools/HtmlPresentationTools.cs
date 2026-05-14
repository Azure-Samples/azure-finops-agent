using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Generates a beautiful, self-contained HTML presentation deck (one .html file).
/// The LLM provides structured slide data; the renderer stamps it into a vetted
/// template with Chart.js (CDN) for charts. Keyboard nav: ←/→ to navigate,
/// ↑ fullscreen, ↓ exit. Click zones, dot nav, progress bar, swipe — all included.
/// </summary>
public static class HtmlPresentationTools
{
    internal static readonly ConcurrentDictionary<string, (string Path, DateTime Created)> GeneratedFiles = new();

    internal static void CleanupOldFiles() =>
        TempFileHelper.CleanupOldFiles(GeneratedFiles, v => v.Created, v => v.Path);

    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GenerateHtmlPresentation, "GenerateHtmlPresentation",
            @"Generates a self-contained HTML deck (one .html file). Use whenever the user asks for a 'presentation', 'deck', 'slides', or 'exec summary' — there's no other format. Built-in nav: ←/→ navigate, ↑ fullscreen, ↓/Esc exit, number keys jump, touch swipe, dot nav, progress bar.

LAYOUTS: title | kpi | chart | content | two_column | maturity | alerts | table | closing.
Use 'alerts' (good/warn/bad rows) for findings. Use 'table' for top-N rankings (status column auto-colors OK/Watch/Alert; numeric col auto-renders inline bar). 'maturity' supports single-state mode: omit `before` (or set =after) and it collapses to one column.

DESIGN RULES:
- Report CURRENT state, not a narrative. State metric + 3-word verdict (good/watch/bad). No multi-quarter story arcs unless user asks for trends.
- Max 5 bullets/slide, ≤10 words each, no paragraphs. Always include units ($12.4K/mo, 47 VMs).
- Slide title = the CONCLUSION, not the topic. Bad: 'VM Costs'. Good: 'VMs drive 38% of spend — too concentrated'.
- One chart OR one table per slide, never both. Chart values are numbers, not strings. Use chart only when ≥3 data points.
- Chart types: pie/doughnut (composition ≤6), horizontal_bar (top-N rankings — preferred), bar (small comparison), line (time series 7+), waterfall (ONLY if user asks for before→after impact stacking).
- No 'Thank You' or 'Questions?' slides. No padding — if 4 slides is the report, ship 4.

DEFAULT CURRENT-STATE PATTERN (for any 'show me where we stand'): title → kpi (4 cards, accent green/amber/red as verdicts) → alerts (3-6 findings) → chart (horizontal_bar top 5-10) → table (5-10 rows: Name, Owner, $, Status) → maturity (single-state) → closing (3-5 verbs-first next steps + optional CTA). Switch to before/after mode (waterfall + before≠after) ONLY when user explicitly asks for a remediation recap.
");
    }

    private static Task<string> GenerateHtmlPresentation(
        [Description(@"JSON array of slides. SLIDE OBJECT SCHEMA:
- layout: 'title' | 'kpi' | 'chart' | 'content' | 'two_column' | 'maturity' | 'alerts' | 'table' | 'closing' (REQUIRED)
- title: slide title (REQUIRED, except 'title' layout uses it as the hero h1)
- subtitle: optional one-line lead
- eyebrow: optional small uppercase label above title (title/closing layouts)
- bullets: optional string array (≤5, ≤10 words each)
- bullets_right: right column for 'two_column' layout
- kpis: REQUIRED for 'kpi' — [{""label"":""Monthly spend"",""value"":""$45.2K"",""sublabel"":""+18% vs Mar"",""accent"":""amber""}]. accent ∈ blue|teal|green|amber|red.
- rows: REQUIRED for 'maturity' — [{""label"":""Tagging"",""before"":0,""after"":4,""action"":""Tagged 16 resources""}].
- footnote: optional bold blue line under the maturity table.
- chart: optional {type, title, labels[], values[], colors[]?}. type ∈ bar|horizontal_bar|line|pie|doughnut|waterfall.
- alerts: REQUIRED for 'alerts' — [{""title"":""142 untagged resources"",""detail"":""Chargeback impossible without tags"",""impact"":""$8K/mo unattributed"",""severity"":""bad""}]. severity ∈ good|warn|bad.
- columns + rows: REQUIRED for 'table' — columns:[""Resource"",""Owner"",""Monthly cost"",""Status""], rows:[[""rg-prod-eu"",""Platform"",""$12,700"",""Watch""],...]. Status cells with values OK/Watch/Alert auto-color. Numeric column auto-renders inline bar.
- cta: closing layout — {""label"":""View on GitHub"",""url"":""https://...""}.

EXAMPLE:
[
  {""layout"":""title"",""title"":""Contoso FinOps Review"",""subtitle"":""May 2026 — Generated live from Azure""},
  {""layout"":""kpi"",""title"":""Where Contoso stands today"",""kpis"":[{""label"":""Monthly spend"",""value"":""$45.2K"",""sublabel"":""+18% vs March"",""accent"":""amber""},{""label"":""Identified savings"",""value"":""$12.4K/mo"",""sublabel"":""27% reduction"",""accent"":""green""},{""label"":""Untagged"",""value"":""142"",""sublabel"":""of 387"",""accent"":""red""}]},
  {""layout"":""chart"",""title"":""VMs and AKS account for 61% of spend"",""chart"":{""type"":""horizontal_bar"",""title"":""Top services (USD)"",""labels"":[""VMs"",""AKS"",""Storage"",""SQL""],""values"":[17200,10500,5400,4200]},""bullets"":[""rg-prod-eu — top cost center at $12.7K""]},
  {""layout"":""closing"",""title"":""Re-score in 30 days"",""bullets"":[""Tag remaining 142 resources"",""Set budget alerts at 80%"",""Configure cost exports""],""cta"":{""label"":""Re-run scoring"",""url"":""/""}}
]")] string slidesJson,
        [Description("Filename (without extension). Default: 'FinOps-Deck'.")] string? filename,
        [Description("Optional customer/tenant name shown on the title slide and in chrome.")] string? customer = null)
    {
        if (string.IsNullOrWhiteSpace(slidesJson))
            return Task.FromResult("Error: No slides data provided.");

        CleanupOldFiles();

        JsonElement root;
        try { root = JsonDocument.Parse(slidesJson).RootElement; }
        catch (JsonException jex) { return Task.FromResult($"Error: Invalid slides JSON — {jex.Message}"); }

        if (root.ValueKind != JsonValueKind.Array)
            return Task.FromResult("Error: slides must be a JSON array.");

        var fileId = Guid.NewGuid().ToString("N")[..12];
        var safeName = TempFileHelper.SanitizeFilename(filename ?? "FinOps-Deck", "FinOps-Deck");
        var outputPath = Path.Combine(Path.GetTempPath(), $"{fileId}_{safeName}.html");

        var slidesHtml = new StringBuilder();
        var chartScripts = new StringBuilder();
        var slideCount = 0;
        foreach (var slide in root.EnumerateArray())
        {
            var (slideHtml, chartJs) = RenderSlide(slide, slideCount);
            slidesHtml.Append(slideHtml);
            if (!string.IsNullOrEmpty(chartJs)) chartScripts.Append(chartJs);
            slideCount++;
        }

        var deckTitle = customer is { Length: > 0 }
            ? $"{customer} · FinOps Deck"
            : "Azure FinOps · Generated Deck";

        var html = BuildShell(deckTitle, slidesHtml.ToString(), chartScripts.ToString());
        File.WriteAllText(outputPath, html, new UTF8Encoding(false));

        GeneratedFiles[fileId] = (outputPath, DateTime.UtcNow);
        return Task.FromResult($"__HTML_READY__:{fileId}:{safeName}.html:{slideCount}");
    }

    // ────────────────────────────────────────────────────────────────────
    // Slide rendering
    // ────────────────────────────────────────────────────────────────────

    private static (string Html, string ChartJs) RenderSlide(JsonElement s, int idx)
    {
        var layout = GetString(s, "layout") ?? "content";
        var title = GetString(s, "title") ?? "";
        var subtitle = GetString(s, "subtitle");
        var eyebrow = GetString(s, "eyebrow");
        var bullets = GetStringArray(s, "bullets");
        var bulletsRight = GetStringArray(s, "bullets_right");
        var footnote = GetString(s, "footnote");
        var first = idx == 0 ? " active" : "";

        return layout switch
        {
            "title" => (RenderTitleSlide(idx, first, eyebrow, title, subtitle), ""),
            "kpi" => (RenderKpiSlide(idx, first, title, subtitle, GetKpis(s), bullets), ""),
            "chart" => RenderChartSlide(idx, first, title, subtitle, GetChart(s), bullets),
            "two_column" => (RenderTwoColumnSlide(idx, first, title, subtitle, bullets, bulletsRight), ""),
            "maturity" => (RenderMaturitySlide(idx, first, title, subtitle, GetMaturityRows(s), footnote), ""),
            "alerts" => (RenderAlertsSlide(idx, first, title, subtitle, GetAlerts(s)), ""),
            "table" => (RenderTableSlide(idx, first, title, subtitle, GetTableColumns(s), GetTableRows(s)), ""),
            "closing" => (RenderClosingSlide(idx, first, eyebrow, title, subtitle, bullets, GetCta(s)), ""),
            _ => (RenderContentSlide(idx, first, title, subtitle, bullets), ""),
        };
    }

    private static string RenderTitleSlide(int idx, string first, string? eyebrow, string title, string? subtitle) => $@"
<section class=""slide s-title{first}"" data-idx=""{idx}"">
  <div class=""s-title-bg""></div>
  <div class=""content"">
    {(eyebrow is { Length: > 0 } ? $@"<div class=""eyebrow anim d1"">{Esc(eyebrow)}</div>" : "")}
    <h1 class=""anim d2"">{Esc(title)}</h1>
    {(subtitle is { Length: > 0 } ? $@"<p class=""sub anim d3"">{Esc(subtitle)}</p>" : "")}
    <div class=""title-meta anim d4""><span>← →</span><span>↑ fullscreen</span></div>
  </div>
</section>";

    private static string RenderKpiSlide(int idx, string first, string title, string? subtitle, List<Kpi> kpis, List<string>? bullets)
    {
        var cards = string.Concat(kpis.Take(4).Select((k, i) =>
            $@"<div class=""kpi-card anim d{i + 2} kpi-{k.Accent}"">
                <div class=""kpi-bar""></div>
                <div class=""kpi-label"">{Esc(k.Label)}</div>
                <div class=""kpi-value"">{Esc(k.Value)}</div>
                {(string.IsNullOrEmpty(k.Sublabel) ? "" : $@"<div class=""kpi-sub"">{Esc(k.Sublabel)}</div>")}
            </div>"));

        var bulletList = bullets is { Count: > 0 }
            ? $@"<ul class=""kpi-bullets anim d6"">{string.Concat(bullets.Take(5).Select(b => $"<li>{Esc(b)}</li>"))}</ul>"
            : "";

        return $@"
<section class=""slide s-kpi{first}"" data-idx=""{idx}"">
  <div class=""content"">
    <h2 class=""section-title anim d1"">{Esc(title)}</h2>
    {(subtitle is { Length: > 0 } ? $@"<p class=""section-lead anim d1"">{Esc(subtitle)}</p>" : "")}
    <div class=""kpi-grid kpi-cols-{Math.Min(kpis.Count, 4)}"">{cards}</div>
    {bulletList}
  </div>
</section>";
    }

    private static (string Html, string ChartJs) RenderChartSlide(int idx, string first, string title, string? subtitle, JsonElement? chart, List<string>? bullets)
    {
        var canvasId = $"chart_{idx}";
        var html = $@"
<section class=""slide s-chart{first}"" data-idx=""{idx}"">
  <div class=""content"">
    <h2 class=""section-title anim d1"">{Esc(title)}</h2>
    {(subtitle is { Length: > 0 } ? $@"<p class=""section-lead anim d2"">{Esc(subtitle)}</p>" : "")}
    <div class=""chart-wrap anim d3""><canvas id=""{canvasId}""></canvas></div>
    {(bullets is { Count: > 0 } ? $@"<ul class=""chart-takeaway anim d4"">{string.Concat(bullets.Take(2).Select(b => $"<li>{Esc(b)}</li>"))}</ul>" : "")}
  </div>
</section>";

        var chartJs = chart.HasValue ? BuildChartJs(canvasId, chart.Value) : "";
        return (html, chartJs);
    }

    private static string RenderContentSlide(int idx, string first, string title, string? subtitle, List<string>? bullets)
    {
        // Auto-scale font with bullet count so few bullets read big
        var n = bullets?.Count ?? 0;
        var sizeClass = n <= 3 ? "big" : n <= 5 ? "med" : "small";
        var bulletList = bullets is { Count: > 0 }
            ? $@"<ul class=""content-bullets {sizeClass} anim d2"">{string.Concat(bullets.Select(b => $"<li>{RenderBullet(b)}</li>"))}</ul>"
            : "";

        return $@"
<section class=""slide s-content{first}"" data-idx=""{idx}"">
  <div class=""content"">
    <h2 class=""section-title anim d1"">{Esc(title)}</h2>
    {(subtitle is { Length: > 0 } ? $@"<p class=""section-lead anim d1"">{Esc(subtitle)}</p>" : "")}
    {bulletList}
  </div>
</section>";
    }

    private static string RenderTwoColumnSlide(int idx, string first, string title, string? subtitle, List<string>? left, List<string>? right) => $@"
<section class=""slide s-twocol{first}"" data-idx=""{idx}"">
  <div class=""content"">
    <h2 class=""section-title anim d1"">{Esc(title)}</h2>
    {(subtitle is { Length: > 0 } ? $@"<p class=""section-lead anim d1"">{Esc(subtitle)}</p>" : "")}
    <div class=""twocol-grid"">
      <div class=""col col-left anim d2""><ul>{string.Concat((left ?? new()).Select(b => $"<li>{RenderBullet(b)}</li>"))}</ul></div>
      <div class=""col col-right anim d3""><ul>{string.Concat((right ?? new()).Select(b => $"<li>{RenderBullet(b)}</li>"))}</ul></div>
    </div>
  </div>
</section>";

    private static string RenderMaturitySlide(int idx, string first, string title, string? subtitle, List<MaturityRow> rows, string? footnote)
    {
        string Stars(int n) { n = Math.Clamp(n, 0, 5); return new string('★', n) + new string('☆', 5 - n); }
        string ColorClass(int n) => n >= 4 ? "good" : n >= 3 ? "blue" : n >= 1 ? "amber" : "bad";

        // Single-state mode: when every row's before equals after (or before is 0/missing AND after >0),
        // collapse to a single 'Score' column. Default heuristic: if all rows have before == after, single-state.
        var singleState = rows.Count > 0 && rows.All(r => r.Before == r.After);

        var headHtml = singleState
            ? @"<thead><tr><th>Dimension</th><th>Score</th><th>What we observed</th></tr></thead>"
            : @"<thead><tr><th>Dimension</th><th>Before</th><th></th><th>After</th><th>What we did</th></tr></thead>";

        var rowsHtml = string.Concat(rows.Select((r, i) => singleState
            ? $@"
            <tr class=""anim d{Math.Min(i + 2, 6)}"">
              <td class=""mat-label"">{Esc(r.Label)}</td>
              <td class=""mat-stars stars-{ColorClass(r.After)}"">{Stars(r.After)}</td>
              <td class=""mat-action"">{Esc(r.Action)}</td>
            </tr>"
            : $@"
            <tr class=""anim d{Math.Min(i + 2, 6)}"">
              <td class=""mat-label"">{Esc(r.Label)}</td>
              <td class=""mat-stars stars-{ColorClass(r.Before)}"">{Stars(r.Before)}</td>
              <td class=""mat-arrow"">→</td>
              <td class=""mat-stars stars-{ColorClass(r.After)}"">{Stars(r.After)}</td>
              <td class=""mat-action"">{Esc(r.Action)}</td>
            </tr>"));

        return $@"
<section class=""slide s-maturity{first}"" data-idx=""{idx}"">
  <div class=""content"">
    <h2 class=""section-title anim d1"">{Esc(title)}</h2>
    {(subtitle is { Length: > 0 } ? $@"<p class=""section-lead anim d1"">{Esc(subtitle)}</p>" : "")}
    <table class=""maturity-table"">
      {headHtml}
      <tbody>{rowsHtml}</tbody>
    </table>
    {(footnote is { Length: > 0 } ? $@"<div class=""maturity-foot anim d6"">{Esc(footnote)}</div>" : "")}
  </div>
</section>";
    }

    private static string RenderClosingSlide(int idx, string first, string? eyebrow, string title, string? subtitle, List<string>? bullets, Cta? cta)
    {
        var bulletList = bullets is { Count: > 0 }
            ? $@"<ul class=""closing-bullets anim d3"">{string.Concat(bullets.Select(b => $"<li>{RenderBullet(b)}</li>"))}</ul>"
            : "";
        var ctaHtml = cta is not null
            ? $@"<a class=""cta anim d5"" href=""{Esc(cta.Url)}"" target=""_blank"" rel=""noopener"">{Esc(cta.Label)} →</a>"
            : "";

        return $@"
<section class=""slide s-closing{first}"" data-idx=""{idx}"">
  <div class=""content"">
    {(eyebrow is { Length: > 0 } ? $@"<div class=""eyebrow anim d1"">{Esc(eyebrow)}</div>" : "")}
    <h2 class=""closing-title anim d2"">{Esc(title)}</h2>
    {(subtitle is { Length: > 0 } ? $@"<p class=""section-lead anim d2"">{Esc(subtitle)}</p>" : "")}
    {bulletList}
    {ctaHtml}
  </div>
</section>";
    }

    // ───── Alerts: color-coded findings list (good/warn/bad) ─────
    private static string RenderAlertsSlide(int idx, string first, string title, string? subtitle, List<Alert> alerts)
    {
        var rowsHtml = string.Concat(alerts.Select((a, i) => $@"
            <li class=""alert-row alert-{a.Severity} anim d{Math.Min(i + 2, 6)}"">
              <div class=""alert-mark"">{(a.Severity == "good" ? "✓" : a.Severity == "bad" ? "!" : "•")}</div>
              <div class=""alert-body"">
                <div class=""alert-title"">{Esc(a.Title)}</div>
                {(string.IsNullOrEmpty(a.Detail) ? "" : $@"<div class=""alert-detail"">{Esc(a.Detail)}</div>")}
              </div>
              {(string.IsNullOrEmpty(a.Impact) ? "" : $@"<div class=""alert-impact"">{Esc(a.Impact)}</div>")}
            </li>"));

        return $@"
<section class=""slide s-alerts{first}"" data-idx=""{idx}"">
  <div class=""content"">
    <h2 class=""section-title anim d1"">{Esc(title)}</h2>
    {(subtitle is { Length: > 0 } ? $@"<p class=""section-lead anim d1"">{Esc(subtitle)}</p>" : "")}
    <ul class=""alerts-list"">{rowsHtml}</ul>
  </div>
</section>";
    }

    // ───── Table: top-N ranking with optional bar + status tag ─────
    private static string RenderTableSlide(int idx, string first, string title, string? subtitle, List<string> cols, List<List<string>> rows)
    {
        // Auto-detect a numeric column for mini-bars: scan column with most numeric values.
        int barCol = -1;
        double maxVal = 0;
        if (rows.Count > 0)
        {
            for (int c = 0; c < (cols.Count > 0 ? cols.Count : rows[0].Count); c++)
            {
                int numCount = 0;
                double colMax = 0;
                foreach (var r in rows)
                {
                    if (c >= r.Count) continue;
                    if (TryParseNumeric(r[c], out var n)) { numCount++; if (n > colMax) colMax = n; }
                }
                if (numCount >= rows.Count - 1 && colMax > maxVal) { barCol = c; maxVal = colMax; }
            }
        }

        var headHtml = cols.Count > 0
            ? $"<thead><tr>{string.Concat(cols.Select(c => $"<th>{Esc(c)}</th>"))}</tr></thead>"
            : "";

        var bodyRows = string.Concat(rows.Select((r, i) => $@"
            <tr class=""anim d{Math.Min(i + 2, 6)}"">
              {string.Concat(r.Select((cell, c) => RenderTableCell(cell, c, c == barCol, maxVal)))}
            </tr>"));

        return $@"
<section class=""slide s-table{first}"" data-idx=""{idx}"">
  <div class=""content"">
    <h2 class=""section-title anim d1"">{Esc(title)}</h2>
    {(subtitle is { Length: > 0 } ? $@"<p class=""section-lead anim d1"">{Esc(subtitle)}</p>" : "")}
    <table class=""data-table"">
      {headHtml}
      <tbody>{bodyRows}</tbody>
    </table>
  </div>
</section>";
    }

    private static string RenderTableCell(string cell, int colIdx, bool withBar, double max)
    {
        // Auto-tag known status words for color
        var lower = cell.Trim().ToLowerInvariant();
        var tagClass = lower switch
        {
            "ok" or "good" or "healthy" or "optimal" or "compliant" => "tag tag-good",
            "watch" or "warning" or "review" or "investigate" => "tag tag-warn",
            "alert" or "bad" or "critical" or "high" or "non-compliant" => "tag tag-bad",
            _ => null,
        };
        if (tagClass is not null)
            return $@"<td><span class=""{tagClass}"">{Esc(cell)}</span></td>";

        if (withBar && TryParseNumeric(cell, out var v) && max > 0)
        {
            var pct = Math.Clamp(v / max * 100, 1, 100);
            return $@"<td><div class=""cell-bar""><span class=""cell-bar-text"">{Esc(cell)}</span><i style=""width:{pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%""></i></div></td>";
        }

        return $"<td>{Esc(cell)}</td>";
    }

    private static bool TryParseNumeric(string s, out double v)
    {
        var clean = new string(s.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        return double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    // ────────────────────────────────────────────────────────────────────
    // Chart.js builder
    // ────────────────────────────────────────────────────────────────────

    private static string BuildChartJs(string canvasId, JsonElement chart)
    {
        var type = GetString(chart, "type") ?? "bar";
        var labels = GetStringArray(chart, "labels") ?? new();
        var values = GetNumberArray(chart, "values") ?? new();
        var chartTitle = GetString(chart, "title") ?? "";
        var palette = new[] { "#0078D4", "#00BCF2", "#107C10", "#FFB900", "#E81123", "#5C2D91", "#00B7C3", "#008272" };

        // Waterfall: render as a stacked bar where each segment = [transparent base, delta]
        // values = [baseline, +d1, +d2, ..., final]. First/last = absolute, middle = deltas.
        if (type == "waterfall" && values.Count >= 3)
        {
            var bases = new List<double>();
            var deltas = new List<double>();
            var colors = new List<string>();
            double running = values[0];
            bases.Add(0); deltas.Add(running); colors.Add("#5C2D91"); // baseline (purple)
            for (int i = 1; i < values.Count - 1; i++)
            {
                var d = values[i];
                if (d >= 0) { bases.Add(running); deltas.Add(d); colors.Add("#107C10"); }
                else { bases.Add(running + d); deltas.Add(-d); colors.Add("#E81123"); }
                running += d;
            }
            bases.Add(0); deltas.Add(values[^1]); colors.Add("#0078D4"); // final (blue)
            var basesJs = JsonSerializer.Serialize(bases);
            var deltasJs = JsonSerializer.Serialize(deltas);
            var colorsJs = JsonSerializer.Serialize(colors);
            var labelsJsW = JsonSerializer.Serialize(labels);
            return $@"
(function(){{
  const ctx=document.getElementById('{canvasId}');
  if(!ctx) return;
  new Chart(ctx,{{
    type:'bar',
    data:{{ labels:{labelsJsW}, datasets:[
      {{ label:'_base', data:{basesJs}, backgroundColor:'rgba(0,0,0,0)', stack:'w' }},
      {{ label:'value', data:{deltasJs}, backgroundColor:{colorsJs}, stack:'w', borderRadius:4 }}
    ] }},
    options:{{
      responsive:true, maintainAspectRatio:false,
      plugins:{{
        legend:{{ display:false }},
        title:{{ display:{(string.IsNullOrEmpty(chartTitle) ? "false" : "true")}, text:{JsonSerializer.Serialize(chartTitle)}, font:{{size:16, weight:'600'}}, padding:12 }},
        tooltip:{{ filter: c=>c.dataset.label!=='_base', callbacks:{{ label:c=>c.parsed.y }} }}
      }},
      scales:{{ x:{{ stacked:true, ticks:{{font:{{size:13}}}} }}, y:{{ stacked:true, ticks:{{font:{{size:13}}}} }} }}
    }}
  }});
}})();";
        }

        // Map our type names to Chart.js types
        var (chartType, indexAxis, isCircular) = type switch
        {
            "horizontal_bar" => ("bar", "'y'", false),
            "pie" => ("pie", "'x'", true),
            "doughnut" => ("doughnut", "'x'", true),
            "line" => ("line", "'x'", false),
            _ => ("bar", "'x'", false),
        };

        var labelsJs = JsonSerializer.Serialize(labels);
        var valuesJs = JsonSerializer.Serialize(values);
        var bgColors = isCircular
            ? JsonSerializer.Serialize(labels.Select((_, i) => palette[i % palette.Length]).ToArray())
            : $"'{palette[0]}'";

        return $@"
(function(){{
  const ctx=document.getElementById('{canvasId}');
  if(!ctx) return;
  new Chart(ctx,{{
    type:'{chartType}',
    data:{{ labels:{labelsJs}, datasets:[{{ label:{JsonSerializer.Serialize(chartTitle)}, data:{valuesJs}, backgroundColor:{bgColors}, borderColor:'{palette[0]}', borderWidth:2, tension:0.35, fill:{(chartType == "line" ? "true" : "false")} }}] }},
    options:{{
      responsive:true, maintainAspectRatio:false, indexAxis:{indexAxis},
      plugins:{{
        legend:{{ display:{(isCircular ? "true" : "false")}, position:'bottom', labels:{{ font:{{size:13}}, padding:14 }} }},
        title:{{ display:{(string.IsNullOrEmpty(chartTitle) ? "false" : "true")}, text:{JsonSerializer.Serialize(chartTitle)}, font:{{size:16, weight:'600'}}, padding:12 }},
        tooltip:{{ callbacks:{{ label:c=>(c.dataset.label?c.dataset.label+': ':'')+(typeof c.parsed==='object'?c.parsed.y ?? c.parsed.x:c.parsed) }} }}
      }},
      scales:{(isCircular ? "{}" : "{ x:{ ticks:{font:{size:13}} }, y:{ ticks:{font:{size:13}} } }")}
    }}
  }});
}})();";
    }

    // ────────────────────────────────────────────────────────────────────
    // Shell template (with nav chrome)
    // ────────────────────────────────────────────────────────────────────

    private static string BuildShell(string title, string slidesHtml, string chartScripts) => $@"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>{Esc(title)}</title>
<link href=""https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800;900&display=swap"" rel=""stylesheet"">
<script src=""https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.min.js""></script>
<style>
*{{margin:0;padding:0;box-sizing:border-box}}
:root{{
  --azure-blue:#0078d4;--azure-blue-dark:#106ebe;--azure-blue-darker:#005a9e;
  --azure-cyan:#00bcf2;--ink:#1a1a1a;--ink-soft:#4a4a4a;--ink-mute:#767676;
  --line:#e1e1e1;--bg-soft:#f5f9fd;--bg-blue:#f0f6fc;
  --good:#107c10;--amber:#bf8700;--red:#a4262c;--teal:#00bcf2;
}}
html,body{{height:100%;width:100%;overflow:hidden;font-family:'Inter','Segoe UI',-apple-system,sans-serif;background:#fff;color:var(--ink);-webkit-font-smoothing:antialiased}}
.deck{{position:relative;width:100vw;height:100vh;overflow:hidden}}
.slide{{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;padding:4vh 5vw;opacity:0;visibility:hidden;transform:translateX(60px);transition:opacity .55s cubic-bezier(.22,1,.36,1),transform .55s cubic-bezier(.22,1,.36,1),visibility 0s linear .55s}}
.slide.active{{opacity:1;visibility:visible;transform:translateX(0);transition:opacity .55s cubic-bezier(.22,1,.36,1),transform .55s cubic-bezier(.22,1,.36,1),visibility 0s}}
.slide.prev{{transform:translateX(-60px)}}
.content{{width:100%;max-width:1280px}}
.anim{{opacity:0;transform:translateY(16px);transition:opacity .7s ease,transform .7s cubic-bezier(.22,1,.36,1)}}
.slide.active .anim{{opacity:1;transform:translateY(0)}}
.slide.active .anim.d1{{transition-delay:.08s}}
.slide.active .anim.d2{{transition-delay:.20s}}
.slide.active .anim.d3{{transition-delay:.32s}}
.slide.active .anim.d4{{transition-delay:.44s}}
.slide.active .anim.d5{{transition-delay:.56s}}
.slide.active .anim.d6{{transition-delay:.68s}}

/* Title slide */
.s-title{{background:radial-gradient(ellipse at 15% 20%,rgba(0,120,212,.06),transparent 55%),radial-gradient(ellipse at 85% 80%,rgba(0,188,242,.06),transparent 55%),#fff;text-align:center}}
.s-title h1{{font-size:clamp(2.6rem,7vw,6rem);font-weight:800;line-height:1;letter-spacing:-.035em;color:var(--ink);margin-bottom:1.2rem}}
.s-title .eyebrow{{font-size:.85rem;font-weight:700;letter-spacing:.3em;text-transform:uppercase;color:var(--azure-blue);margin-bottom:1.2rem}}
.s-title .sub{{font-size:clamp(1.1rem,1.6vw,1.5rem);color:var(--ink-soft);max-width:760px;margin:0 auto 2rem;line-height:1.5}}
.s-title .title-meta{{display:flex;gap:2rem;justify-content:center;font-size:.8rem;color:var(--ink-mute);letter-spacing:.18em;text-transform:uppercase}}

/* Section / content titles */
.section-title{{font-size:clamp(2rem,3.6vw,3rem);font-weight:700;letter-spacing:-.015em;line-height:1.1;margin-bottom:.6rem;color:var(--ink)}}
.section-lead{{color:var(--ink-soft);font-size:clamp(1.05rem,1.4vw,1.3rem);margin-bottom:1.8rem;max-width:1000px;line-height:1.5}}

/* Content layout — bullets scale with count */
.content-bullets{{list-style:none;display:flex;flex-direction:column;gap:.9rem;margin-top:1rem}}
.content-bullets.big li{{font-size:clamp(1.4rem,2.2vw,2rem);line-height:1.35}}
.content-bullets.med li{{font-size:clamp(1.15rem,1.7vw,1.55rem);line-height:1.4}}
.content-bullets.small li{{font-size:clamp(1rem,1.3vw,1.25rem);line-height:1.45}}
.content-bullets li{{position:relative;padding-left:1.6rem;color:var(--ink)}}
.content-bullets li::before{{content:'';position:absolute;left:0;top:.55em;width:.6rem;height:.6rem;background:var(--azure-blue);border-radius:2px}}
.content-bullets li b{{color:var(--ink);font-weight:700}}

/* KPI layout */
.kpi-grid{{display:grid;gap:1.2rem;margin-top:1.5rem}}
.kpi-cols-1{{grid-template-columns:1fr}}
.kpi-cols-2{{grid-template-columns:1fr 1fr}}
.kpi-cols-3{{grid-template-columns:repeat(3,1fr)}}
.kpi-cols-4{{grid-template-columns:repeat(4,1fr)}}
.kpi-card{{position:relative;padding:1.5rem 1.4rem;border:1px solid var(--line);border-radius:14px;background:#fff;overflow:hidden;min-width:0}}
.kpi-card .kpi-bar{{position:absolute;top:0;left:0;right:0;height:5px;background:var(--azure-blue)}}
.kpi-card.kpi-blue  .kpi-bar{{background:var(--azure-blue)}}
.kpi-card.kpi-teal  .kpi-bar{{background:var(--teal)}}
.kpi-card.kpi-green .kpi-bar{{background:var(--good)}}
.kpi-card.kpi-amber .kpi-bar{{background:var(--amber)}}
.kpi-card.kpi-red   .kpi-bar{{background:var(--red)}}
.kpi-label{{font-size:.78rem;font-weight:700;letter-spacing:.15em;text-transform:uppercase;color:var(--ink-mute);margin-top:.5rem;margin-bottom:.6rem}}
.kpi-value{{font-size:clamp(1.6rem,2.6vw,2.4rem);font-weight:800;letter-spacing:-.02em;color:var(--ink);line-height:1.05;word-break:break-word}}
.kpi-sub{{margin-top:.5rem;font-size:.95rem;color:var(--ink-soft);line-height:1.35}}
.kpi-bullets{{list-style:none;margin-top:2rem;display:flex;flex-direction:column;gap:.7rem}}
.kpi-bullets li{{font-size:1.15rem;color:var(--ink-soft);padding-left:1.4rem;position:relative}}
.kpi-bullets li::before{{content:'';position:absolute;left:0;top:.55em;width:.5rem;height:.5rem;background:var(--azure-blue);border-radius:2px}}

/* Chart layout */
.chart-wrap{{position:relative;width:100%;height:55vh;max-height:560px;margin-top:.5rem}}
.chart-takeaway{{list-style:none;margin-top:1.2rem;display:flex;flex-direction:column;gap:.4rem}}
.chart-takeaway li{{font-size:clamp(1rem,1.3vw,1.25rem);color:var(--ink-soft);padding-left:1.4rem;position:relative;line-height:1.45}}
.chart-takeaway li::before{{content:'';position:absolute;left:0;top:.55em;width:.5rem;height:.5rem;background:var(--teal);border-radius:50%}}

/* Two-column */
.twocol-grid{{display:grid;grid-template-columns:1fr 1fr;gap:2rem;margin-top:1rem}}
.col ul{{list-style:none;display:flex;flex-direction:column;gap:.8rem}}
.col li{{font-size:clamp(1.1rem,1.5vw,1.4rem);padding:1rem 1.2rem;background:var(--bg-soft);border-left:4px solid var(--azure-blue);border-radius:6px;line-height:1.4}}
.col-right li{{border-left-color:var(--teal)}}
.col li b{{color:var(--ink);font-weight:700}}

/* Maturity table */
.maturity-table{{width:100%;border-collapse:separate;border-spacing:0 .5rem;margin-top:1rem;font-size:clamp(.95rem,1.2vw,1.15rem)}}
.maturity-table th{{text-align:left;padding:.6rem .9rem;color:var(--ink-mute);font-weight:600;font-size:.78rem;letter-spacing:.15em;text-transform:uppercase}}
.maturity-table td{{padding:.85rem .9rem;background:#fff;border-top:1px solid var(--line);border-bottom:1px solid var(--line)}}
.maturity-table td:first-child{{border-left:1px solid var(--line);border-radius:8px 0 0 8px}}
.maturity-table td:last-child{{border-right:1px solid var(--line);border-radius:0 8px 8px 0}}
.mat-label{{font-weight:700;color:var(--ink)}}
.mat-stars{{font-family:'Segoe UI Symbol','Apple Symbols',sans-serif;letter-spacing:2px;white-space:nowrap}}
.mat-stars.stars-good{{color:var(--good)}}
.mat-stars.stars-blue{{color:var(--azure-blue)}}
.mat-stars.stars-amber{{color:var(--amber)}}
.mat-stars.stars-bad{{color:var(--red)}}
.mat-arrow{{color:var(--ink-mute);text-align:center;width:2rem}}
.mat-action{{color:var(--ink-soft)}}
.maturity-foot{{margin-top:1.5rem;padding:.9rem 1.2rem;background:var(--bg-blue);border-left:4px solid var(--azure-blue);border-radius:6px;color:var(--azure-blue-darker);font-weight:700;font-size:1.1rem}}

/* Alerts */
.alerts-list{{list-style:none;display:flex;flex-direction:column;gap:.7rem;margin-top:1rem}}
.alert-row{{display:flex;align-items:center;gap:1rem;padding:1rem 1.2rem;background:#fff;border:1px solid var(--line);border-left:4px solid var(--ink-mute);border-radius:8px}}
.alert-row.alert-good{{border-left-color:var(--good);background:linear-gradient(90deg,rgba(16,124,16,.05),#fff 30%)}}
.alert-row.alert-warn{{border-left-color:var(--amber);background:linear-gradient(90deg,rgba(191,135,0,.05),#fff 30%)}}
.alert-row.alert-bad{{border-left-color:var(--red);background:linear-gradient(90deg,rgba(164,38,44,.05),#fff 30%)}}
.alert-mark{{flex-shrink:0;width:32px;height:32px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-weight:800;color:#fff}}
.alert-good .alert-mark{{background:var(--good)}}
.alert-warn .alert-mark{{background:var(--amber)}}
.alert-bad .alert-mark{{background:var(--red)}}
.alert-body{{flex:1;min-width:0}}
.alert-title{{font-size:clamp(1.1rem,1.4vw,1.3rem);font-weight:700;color:var(--ink);line-height:1.3}}
.alert-detail{{font-size:.95rem;color:var(--ink-soft);margin-top:.2rem;line-height:1.4}}
.alert-impact{{flex-shrink:0;font-size:1rem;font-weight:700;color:var(--ink);padding:.4rem .8rem;background:var(--bg-soft);border-radius:6px;white-space:nowrap}}

/* Data table */
.data-table{{width:100%;border-collapse:separate;border-spacing:0;margin-top:1rem;font-size:clamp(.95rem,1.15vw,1.1rem)}}
.data-table th{{text-align:left;padding:.7rem .9rem;color:var(--ink-mute);font-weight:600;font-size:.78rem;letter-spacing:.15em;text-transform:uppercase;border-bottom:2px solid var(--line)}}
.data-table td{{padding:.85rem .9rem;border-bottom:1px solid var(--line);color:var(--ink)}}
.data-table tbody tr:hover td{{background:var(--bg-soft)}}
.cell-bar{{position:relative;display:block;padding:.3rem .6rem;border-radius:4px;overflow:hidden;background:rgba(0,120,212,.08)}}
.cell-bar i{{position:absolute;left:0;top:0;bottom:0;background:linear-gradient(90deg,rgba(0,120,212,.18),rgba(0,120,212,.32));border-radius:4px}}
.cell-bar-text{{position:relative;z-index:1;font-variant-numeric:tabular-nums;font-weight:600}}
.tag{{display:inline-block;padding:.2rem .7rem;border-radius:4px;font-size:.78rem;font-weight:700;letter-spacing:.05em;text-transform:uppercase}}
.tag-good{{background:rgba(16,124,16,.12);color:var(--good)}}
.tag-warn{{background:rgba(191,135,0,.12);color:var(--amber)}}
.tag-bad{{background:rgba(164,38,44,.12);color:var(--red)}}

/* Closing */
.s-closing{{background:linear-gradient(135deg,var(--azure-blue) 0%,var(--azure-blue-darker) 100%);color:#fff}}
.s-closing .content{{text-align:center;max-width:900px}}
.s-closing .eyebrow{{font-size:.78rem;letter-spacing:.3em;text-transform:uppercase;color:rgba(255,255,255,.85);margin-bottom:1rem}}
.s-closing .closing-title{{font-size:clamp(2.2rem,5vw,4rem);font-weight:800;line-height:1.05;letter-spacing:-.025em;margin-bottom:1rem;color:#fff}}
.s-closing .section-lead{{color:rgba(255,255,255,.92);max-width:700px;margin:0 auto 2rem}}
.closing-bullets{{list-style:none;display:flex;flex-direction:column;gap:.8rem;margin:0 auto 2rem;max-width:680px;text-align:left}}
.closing-bullets li{{font-size:1.2rem;padding:.9rem 1.2rem;background:rgba(255,255,255,.1);border:1px solid rgba(255,255,255,.18);border-radius:10px}}
.s-closing .cta{{display:inline-flex;align-items:center;gap:.5rem;padding:1.05rem 2.2rem;border-radius:10px;background:#fff;color:var(--azure-blue-darker);font-weight:700;font-size:1.05rem;text-decoration:none;transition:transform .2s,box-shadow .2s}}
.s-closing .cta:hover{{transform:translateY(-2px);box-shadow:0 10px 24px rgba(0,0,0,.18)}}

/* Nav chrome */
.progress{{position:fixed;top:0;left:0;right:0;height:3px;background:rgba(0,120,212,.08);z-index:100}}
.progress-bar{{height:100%;background:var(--azure-blue);width:0;transition:width .5s cubic-bezier(.22,1,.36,1)}}
body:has(.s-closing.active) .progress-bar{{background:#fff}}
.nav-arrow{{position:fixed;top:50%;transform:translateY(-50%);width:40px;height:40px;border-radius:50%;background:rgba(255,255,255,.85);backdrop-filter:blur(8px);border:1px solid rgba(0,120,212,.18);color:var(--azure-blue);cursor:pointer;z-index:60;display:flex;align-items:center;justify-content:center;transition:all .25s}}
.nav-arrow svg{{width:18px;height:18px}}
.nav-arrow.left{{left:1.5rem}}
.nav-arrow.right{{right:1.5rem}}
.nav-arrow:hover{{background:var(--azure-blue);color:#fff;transform:translateY(-50%) scale(1.08)}}
body:has([data-idx=""0""].active) .nav-arrow.left{{opacity:0;pointer-events:none}}
body:has(.slide.active[data-last]) .nav-arrow.right{{opacity:0;pointer-events:none}}
body:has(.s-closing.active) .nav-arrow{{background:rgba(255,255,255,.18);border-color:rgba(255,255,255,.35);color:#fff}}
body:has(.s-closing.active) .nav-arrow:hover{{background:#fff;color:var(--azure-blue-darker)}}
.fs-toggle{{position:fixed;top:1.4rem;right:1.4rem;width:36px;height:36px;border-radius:8px;background:rgba(255,255,255,.7);backdrop-filter:blur(8px);border:1px solid rgba(0,120,212,.18);color:var(--ink-mute);cursor:pointer;z-index:65;display:flex;align-items:center;justify-content:center;transition:all .25s}}
.fs-toggle svg{{width:18px;height:18px}}
.fs-toggle:hover{{background:var(--azure-blue);color:#fff;border-color:var(--azure-blue)}}
body:has(.s-closing.active) .fs-toggle{{background:rgba(255,255,255,.18);border-color:rgba(255,255,255,.35);color:#fff}}
.dots{{position:fixed;bottom:1.5rem;left:50%;transform:translateX(-50%);display:flex;gap:.5rem;z-index:60}}
.dot-nav{{width:8px;height:8px;border-radius:50%;background:rgba(0,120,212,.25);border:0;cursor:pointer;padding:0;transition:all .3s}}
.dot-nav.active{{background:var(--azure-blue);width:28px;border-radius:4px}}
body:has(.s-closing.active) .dot-nav{{background:rgba(255,255,255,.4)}}
body:has(.s-closing.active) .dot-nav.active{{background:#fff}}
.counter{{position:fixed;bottom:1.5rem;right:2rem;font-size:.8rem;color:var(--ink-mute);letter-spacing:.15em;z-index:60;font-variant-numeric:tabular-nums}}
body:has(.s-closing.active) .counter{{color:rgba(255,255,255,.85)}}
.hint{{position:fixed;bottom:4rem;left:50%;transform:translateX(-50%);font-size:.75rem;color:var(--ink-mute);z-index:60;transition:opacity .6s ease;pointer-events:none}}
.hint kbd{{display:inline-block;padding:1px 6px;margin:0 2px;background:var(--bg-soft);border:1px solid var(--line);border-radius:4px;font-family:inherit;font-size:.7rem;color:var(--ink-soft)}}
.hint.fade{{opacity:0}}
body:has(.s-closing.active) .hint{{color:rgba(255,255,255,.7)}}
body:has(.s-closing.active) .hint kbd{{background:rgba(255,255,255,.15);border-color:rgba(255,255,255,.3);color:#fff}}
.click-zone{{position:fixed;top:0;bottom:0;width:18%;z-index:50;cursor:pointer}}
.click-zone.left{{left:0}}
.click-zone.right{{right:0}}
@media(max-width:900px){{.kpi-cols-3,.kpi-cols-4{{grid-template-columns:1fr 1fr}};.twocol-grid{{grid-template-columns:1fr}}}}
</style>
</head>
<body>
<div class=""progress""><div class=""progress-bar"" id=""pbar""></div></div>
<div class=""deck"" id=""deck"">
{slidesHtml}
</div>
<div class=""click-zone left"" id=""zl""></div>
<div class=""click-zone right"" id=""zr""></div>
<button class=""nav-arrow left"" id=""al"" aria-label=""Previous""><svg viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2.4"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""15 18 9 12 15 6""/></svg></button>
<button class=""nav-arrow right"" id=""ar"" aria-label=""Next""><svg viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2.4"" stroke-linecap=""round"" stroke-linejoin=""round""><polyline points=""9 18 15 12 9 6""/></svg></button>
<button class=""fs-toggle"" id=""fs"" aria-label=""Fullscreen""><svg viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M3 9V3h6M21 9V3h-6M3 15v6h6M21 15v6h-6""/></svg></button>
<div class=""dots"" id=""dots""></div>
<div class=""counter"" id=""ctr"">01 / 01</div>
<div class=""hint"" id=""hint""><kbd>←</kbd> <kbd>→</kbd> navigate &middot; <kbd>↑</kbd> fullscreen &middot; <kbd>↓</kbd> exit</div>
<script>
const slides=[...document.querySelectorAll('.slide')];const total=slides.length;let cur=0;
const ctr=document.getElementById('ctr');const pbar=document.getElementById('pbar');const dotsW=document.getElementById('dots');const hint=document.getElementById('hint');
if(slides.length) slides[slides.length-1].setAttribute('data-last','');
for(let i=0;i<total;i++){{const b=document.createElement('button');b.className='dot-nav'+(i===0?' active':'');b.addEventListener('click',e=>{{e.stopPropagation();go(i)}});dotsW.appendChild(b)}}
const dots=dotsW.querySelectorAll('.dot-nav');const pad=n=>String(n).padStart(2,'0');
function update(){{slides.forEach((s,i)=>{{s.classList.remove('active','prev');if(i===cur)s.classList.add('active');else if(i<cur)s.classList.add('prev')}});dots.forEach((d,i)=>d.classList.toggle('active',i===cur));ctr.textContent=pad(cur+1)+' / '+pad(total);pbar.style.width=((cur+1)/total*100)+'%';location.hash=cur+1}}
const next=()=>{{if(cur<total-1){{cur++;update();fade()}}}};const prev=()=>{{if(cur>0){{cur--;update();fade()}}}};const go=i=>{{if(i>=0&&i<total){{cur=i;update();fade()}}}};
let faded=false;function fade(){{if(!faded){{faded=true;hint.classList.add('fade')}}}}
document.getElementById('al').addEventListener('click',prev);document.getElementById('ar').addEventListener('click',next);
document.getElementById('zl').addEventListener('click',prev);document.getElementById('zr').addEventListener('click',next);
function fsEl(){{return document.fullscreenElement||document.webkitFullscreenElement||null}}
function fsEnter(){{if(fsEl())return;const r=document.documentElement.requestFullscreen||document.documentElement.webkitRequestFullscreen;if(r)try{{r.call(document.documentElement)}}catch(e){{}}}}
function fsExit(){{if(!fsEl())return;const x=document.exitFullscreen||document.webkitExitFullscreen;if(x)x.call(document)}}
document.getElementById('fs').addEventListener('click',e=>{{e.stopPropagation();fsEl()?fsExit():fsEnter()}});
document.addEventListener('keydown',e=>{{
  if(e.key==='ArrowRight'||e.key===' '||e.key==='PageDown'){{e.preventDefault();next()}}
  else if(e.key==='ArrowLeft'||e.key==='PageUp'){{e.preventDefault();prev()}}
  else if(e.key==='ArrowUp'){{e.preventDefault();fsEnter()}}
  else if(e.key==='ArrowDown'){{e.preventDefault();fsExit()}}
  else if(e.key==='Home'){{go(0)}} else if(e.key==='End'){{go(total-1)}}
  else if(e.key>='1'&&e.key<='9'){{const i=parseInt(e.key,10)-1;if(i<total)go(i)}}
}});
let tx=0;document.addEventListener('touchstart',e=>{{tx=e.touches[0].clientX}},{{passive:true}});
document.addEventListener('touchend',e=>{{const dx=e.changedTouches[0].clientX-tx;if(Math.abs(dx)>50)(dx<0?next:prev)()}});
const h=parseInt(location.hash.slice(1),10);if(!isNaN(h)&&h>=1&&h<=total)cur=h-1;
update();setTimeout(fade,5000);
{chartScripts}
</script>
</body>
</html>";

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private static string Esc(string? s) => string.IsNullOrEmpty(s) ? "" : WebUtility.HtmlEncode(s);

    /// <summary>Render bullet text with optional bold lead-in (split on em-dash, en-dash, or " - ").</summary>
    private static string RenderBullet(string text)
    {
        foreach (var sep in new[] { " — ", " – ", " - " })
        {
            var idx = text.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
                return $"<b>{Esc(text[..idx])}</b>{Esc(sep + text[(idx + sep.Length)..])}";
        }
        return Esc(text);
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<string>? GetStringArray(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        return arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList();
    }

    private static List<double>? GetNumberArray(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        return arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList();
    }

    private static JsonElement? GetChart(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("chart", out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private record Kpi(string Label, string Value, string? Sublabel, string Accent);

    private static List<Kpi> GetKpis(JsonElement e)
    {
        var result = new List<Kpi>();
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kpis", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var k in arr.EnumerateArray())
            result.Add(new Kpi(GetString(k, "label") ?? "", GetString(k, "value") ?? "", GetString(k, "sublabel"), (GetString(k, "accent") ?? "blue").ToLowerInvariant()));
        return result;
    }

    private record MaturityRow(string Label, int Before, int After, string Action);

    private static List<MaturityRow> GetMaturityRows(JsonElement e)
    {
        var result = new List<MaturityRow>();
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("rows", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var r in arr.EnumerateArray())
        {
            int before = r.TryGetProperty("before", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
            int after = r.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
            result.Add(new MaturityRow(GetString(r, "label") ?? "", before, after, GetString(r, "action") ?? ""));
        }
        return result;
    }

    private record Cta(string Label, string Url);

    private static Cta? GetCta(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("cta", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        return new Cta(GetString(c, "label") ?? "Open", GetString(c, "url") ?? "#");
    }

    private record Alert(string Title, string Detail, string Impact, string Severity);

    private static List<Alert> GetAlerts(JsonElement e)
    {
        var result = new List<Alert>();
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("alerts", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var a in arr.EnumerateArray())
        {
            var sev = (GetString(a, "severity") ?? "warn").ToLowerInvariant();
            if (sev != "good" && sev != "bad" && sev != "warn") sev = "warn";
            result.Add(new Alert(GetString(a, "title") ?? "", GetString(a, "detail") ?? "", GetString(a, "impact") ?? "", sev));
        }
        return result;
    }

    private static List<string> GetTableColumns(JsonElement e) => GetStringArray(e, "columns") ?? new();

    private static List<List<string>> GetTableRows(JsonElement e)
    {
        var result = new List<List<string>>();
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("rows", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var row in arr.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var cells = new List<string>();
            foreach (var cell in row.EnumerateArray())
                cells.Add(cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : cell.ToString());
            result.Add(cells);
        }
        return result;
    }
}
