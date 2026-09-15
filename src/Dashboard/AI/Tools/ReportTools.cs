using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public sealed class ReportTools(long owner)
{
    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GenerateDataReport, "GenerateDataReport",
            "Generate a real downloadable CSV, XLSX workbook, or filterable HTML report from verified tabular data. Use for spreadsheets, full tables, and detailed reports. Maximum 5000 total rows, 50 columns, and 10 sheets. Include every requested row and reconcile source totals; do not invent data or filesystem links. For larger datasets narrow or aggregate first, explicitly disclosing coverage.");
    }

    private async Task<string> GenerateDataReport(
        [Description("csv, xlsx, or html")] string format,
        [Description("JSON object: {title, source, sheets:[{name, columns:[string], rows:[[scalar]], sourceRowCount:number}]}. Each row must match the columns. source names the evidence and data timestamp, or states timestamp unknown.")] string dataJson,
        [Description("Display filename without extension.")] string? filename = null,
        CancellationToken cancellationToken = default)
    {
        format = format.ToLowerInvariant();
        if (format is not ("csv" or "xlsx" or "html")) return "Error: supported formats are csv, xlsx, html.";
        if (dataJson.Length > 2 * 1024 * 1024 || SensitiveContent.ContainsSecret(dataJson)) return "Error: report data exceeds the limit or contains credentials.";
        JsonElement data;
        try { data = JsonSerializer.Deserialize<JsonElement>(dataJson); }
        catch (JsonException) { return "Error: report data must be valid JSON."; }
        using var scriptStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AzureFinOps.Dashboard.AI.Tools.Resources.report_export.py")!;
        using var reader = new StreamReader(scriptStream);
        var script = await reader.ReadToEndAsync(cancellationToken);
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("FINOPS_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
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