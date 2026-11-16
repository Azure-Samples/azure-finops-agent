using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public sealed class ReportTools(long owner)
{
    // Runs from a file like the upload helper: Windows caps a command line at 32,767 characters, and with charts the
    // exporter is about 24,000 once escaped.
    private static readonly Lazy<string> HelperScript = new(() => UploadedFileTools.WriteHelperScript("report_export.py"), LazyThreadSafetyMode.PublicationOnly);

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GenerateDataReport, "GenerateDataReport",
            "Generate a downloadable CSV, XLSX workbook or filterable HTML report from verified data. Filter and aggregate at the source, then supply only the requested columns, rows and sheets in dataJson; this formatter does not query or recover omitted data. Maximum 5000 total rows, 50 columns and 10 sheets. Include every requested output row. For rows that came from a query, give that query's row count as sourceRowCount: a payload whose rows do not match it is rejected whole and must be written again, so never adjust it to hide missing rows. For text you wrote yourself (a proposal, narrative or notes) there is no source count: leave sourceRowCount out. A top-N preview is not a full report. If the requested output exceeds limits, disclose the gap and agree a narrower or aggregated output instead of silently dropping rows or changing sourceRowCount. A document with sections, explanations and graphs is one HTML or XLSX report: each sheet is a section that can carry text (its short explanation or advice) and a chart drawn from that sheet's own rows ({type: bar, horizontal_bar, line or pie; label: the category column; values: 1 to 4 numeric columns, 1 for pie; at most 100 rows, 12 for pie}), so no figure is typed twice and the document needs no RenderChart. HTML shows the text and chart above the section's table; XLSX adds an Excel chart beside the table and a Notes sheet with the texts; CSV holds one table only.");
    }

    private async Task<string> GenerateDataReport(
        [Description("csv, xlsx, or html")] string format,
        [Description("JSON object: {title, source, sheets:[{name, text (optional), columns:[string], rows:[[scalar]], sourceRowCount:number (optional), chart (optional): {type, label, values:[string], title}}]}. Select only requested columns after source filtering/aggregation. Each row must match those columns; sourceRowCount is the row count the source query returned for that sheet, not a value adjusted to hide missing rows, and is left out for authored text. A chart's label and values name columns of its own sheet. Retain evidence and timestamp, or state timestamp unknown.")] string dataJson,
        [Description("Display filename without extension.")] string? filename = null,
        CancellationToken cancellationToken = default)
    {
        format = format.ToLowerInvariant();
        if (format is not ("csv" or "xlsx" or "html")) return "Error: supported formats are csv, xlsx, html.";
        if (dataJson.Length > 2 * 1024 * 1024) return "Error: report data exceeds the 2 MB limit.";
        JsonElement data;
        try { data = JsonSerializer.Deserialize<JsonElement>(dataJson); }
        catch (JsonException) { return "Error: report data must be valid JSON."; }
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("FINOPS_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add(HelperScript.Value);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { format, data }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            cancellationToken.ThrowIfCancellationRequested();
            return "Error: report generation timed out.";
        }
        await stderr;
        if (process.ExitCode != 0) return "Error: report generation failed.";
        using var response = JsonDocument.Parse(await stdout);
        if (!response.RootElement.GetProperty("ok").GetBoolean()) return "Error: " + response.RootElement.GetProperty("error").GetString();
        var content = Convert.FromBase64String(response.RootElement.GetProperty("contentBase64").GetString()!);
        var mime = format switch { "csv" => "text/csv", "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", _ => "text/html" };
        var name = TempFileHelper.SanitizeFilename(filename ?? "FinOps-Report", "FinOps-Report") + "." + format;
        var entry = ArtifactStore.Default.Register(owner, name, mime, content);
        var rows = response.RootElement.GetProperty("rows").GetInt32();
        return $"__HTML_READY__:{entry.Id}:{entry.FileName}:{rows} rows ({format.ToUpperInvariant()})";
    }
}