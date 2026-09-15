using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// User-uploaded file inspection. Files dropped in the chat (CSV, TSV, JSON,
/// TXT, XLSX, PDF, Parquet) are persisted to the OS temp dir, tagged with a
/// fileId, and exposed to the LLM via <see cref="QueryUploadedFile"/>.
/// All actual parsing is delegated to the embedded Python helper so we get
/// pandas/openpyxl/pyarrow/pdfminer for free and one consistent code path.
/// </summary>
public sealed class UploadedFileTools
{
    public sealed record UploadEntry(
        string FileId,
        long UserId,
        string FileName,
        string Kind,
        [property: JsonIgnore] string Path,
        long SizeBytes,
        DateTime CreatedUtc,
        string? SchemaSummary,
        string? SessionId = null,
        DateTime ExpiresUtc = default,
        string Sha256 = "",
        bool Removed = false);

    internal static readonly UploadCatalog Catalog = new(TempFileHelper.UploadRoot);

    private const int TimeoutSeconds = 30;
    private const long MaxBytes = 100L * 1024 * 1024; // 100 MB
    private const long MaxImageBytes = 20L * 1024 * 1024; // 20 MB — vision models cap prompt image size

    // .xls intentionally omitted — pandas needs the (uninstalled) xlrd package for legacy xls.
    // Images are stored as-is and attached to the model natively (vision) — no Python parsing.
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".json", ".txt", ".log", ".md", ".xlsx", ".pdf", ".parquet",
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    static UploadedFileTools()
    {
        // Background TTL sweep — without this, expired temp files only get evicted
        // when a new upload happens. Fires every 5 min, ignores its own exceptions.
        _cleanupTimer = new Timer(_ => { try { Cleanup(); } catch { } },
            null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    private static readonly Timer _cleanupTimer;

    // The model controls paramsJson. These keys are resolved by the host from the
    // per-user upload registry and must never be overridable, or the model could
    // point the reader at an arbitrary file on disk (CWE-73).
    private static readonly HashSet<string> ReservedRequestKeys =
        new(StringComparer.OrdinalIgnoreCase) { "mode", "path", "kind" };

    private readonly UserTokens _tokens; // not used today, kept for symmetry with other per-user tools

    public UploadedFileTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(QueryUploadedFile, "QueryUploadedFile",
@"Inspect or query a file the user dropped into the chat (CSV, TSV, JSON, TXT/log/md, XLSX, PDF, Parquet).
Each upload is announced at the start of the user's turn with its fileId, kind, size, and a short preview.
Call this tool to fetch more data — head/tail/slice for rows, schema/count for shape, workbook for all XLSX sheets,
filter/aggregate for tabular analysis,
text_range for long text/PDF, json_path for nested JSON. Responses are capped (≤200 rows or ≤8000 chars per call) so make
multiple calls if you need more.

This tool is the only permitted way to inspect uploaded files. Never use shell, PowerShell, Python, filesystem search, or
the file's temp path. For XLSX, use `workbook` first: it returns every sheet's shape, columns, and bounded numeric
count/sum/min/max/mean summaries in ONE call. If that summary answers the question, do not follow it with aggregate.
Pass `sheet` in paramsJson only when another tabular mode is genuinely needed.

Modes:
  preview     Re-emit the initial preview (rarely needed)
  schema      Columns + dtypes (tabular) or JSON schema tree
  count       Row count
    workbook    XLSX only: every worksheet's shape, columns, and numeric summaries in one call
  head        First N rows (param: count, default 50, max 200)
  tail        Last N rows (param: count)
  slice       Rows offset..offset+count (params: offset, count)
  text_range  txt/pdf substring (params: start, length, max 8000)
  filter      Tabular: rows where column {op} value (params: column, op in eq|ne|gt|lt|ge|le|contains, value, limit)
  aggregate   Tabular: group_by + agg (params: group_by, agg in sum|mean|min|max|count, column, limit)
    query       Tabular: filters[], group_by[] and aggregates[{column,op,as}], sort[{column,direction}], offset, limit
    json_path   JSON: navigate dot/bracket selector (param: jsonPath, e.g. 'properties.rows[0].cost')
    Both aggregate and query accept filters:[{column,op,value}]. All predicates must match. group_by accepts one column or an array of up to 6 columns. Aggregate output includes source/filtered counts, totals, and explicit truncation.

Examples:
  QueryUploadedFile(fileId, 'aggregate', '{""group_by"":""ServiceName"",""agg"":""sum"",""column"":""PreTaxCost""}')
  QueryUploadedFile(fileId, 'filter', '{""column"":""ResourceGroup"",""op"":""contains"",""value"":""prod""}')
  QueryUploadedFile(fileId, 'slice', '{""offset"":1000,""count"":100}')");
    }

    private async Task<string> QueryUploadedFile(
        [Description("The fileId returned at upload time (12-char hex).")] string fileId,
        [Description("Operation: preview, schema, count, workbook, head, tail, slice, text_range, filter, aggregate, query, json_path.")] string mode,
        [Description("Optional JSON object with mode-specific parameters (see tool description).")] string? paramsJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileId)) return Json(new { ok = false, error = "fileId required" });
        if (string.IsNullOrWhiteSpace(mode)) return Json(new { ok = false, error = "mode required" });

        IDisposable lease;
        UploadEntry entry;
        try { lease = Catalog.Acquire(_tokens.UserId, ToolExecutionContext.Current?.SessionId, fileId, out entry); }
        catch (InvalidOperationException exception) { return Json(new { ok = false, error = exception.Message, code = "upload_unavailable" }); }
        using var uploadLease = lease;
        if (entry.Kind == "image")
            return Json(new { ok = false, error = "This fileId is an image — it is attached to the user's message as a visual. Look at the attached image directly instead of querying it." });

        var requestObj = new Dictionary<string, object?>
        {
            ["mode"] = mode.ToLowerInvariant(),
            ["path"] = entry.Path,
            ["kind"] = entry.Kind,
        };
        if (!string.IsNullOrWhiteSpace(paramsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(paramsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return Json(new { ok = false, error = "params must be a JSON object." });
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (ReservedRequestKeys.Contains(p.Name))
                        return Json(new { ok = false, error = $"'{p.Name}' cannot be set in params — the file is selected by fileId." });
                    requestObj[p.Name] = JsonValueToObject(p.Value);
                }
            }
            catch (JsonException jex)
            {
                return Json(new { ok = false, error = $"params is not valid JSON: {jex.Message}" });
            }
        }

        var result = await RunPythonAsync(JsonSerializer.Serialize(requestObj), cancellationToken);
        using var response = JsonDocument.Parse(result);
        var payload = response.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        payload["source"] = new { fileId = entry.FileId, fileName = entry.FileName, sizeBytes = entry.SizeBytes, sha256 = entry.Sha256, expiresUtc = entry.ExpiresUtc };
        return Json(payload);
    }

    // ---------------------------------------------------------------- Public API

    /// <summary>Persists an uploaded file and returns the entry plus an inline preview JSON for the chat context.</summary>
    public static async Task<(UploadEntry Entry, string PreviewJson)> RegisterAsync(
        long userId, Stream content, string fileName, long? declaredSize = null)
    {
        Cleanup();

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || !SupportedExtensions.Contains(ext))
            throw new InvalidOperationException($"Unsupported file type '{ext}'. Allowed: {string.Join(", ", SupportedExtensions)}");

        var fileId = Guid.NewGuid().ToString("N")[..12];
        var safeName = TempFileHelper.SanitizeFilename(fileName, "upload" + ext);
        var path = Catalog.PathFor(fileId, safeName);

        await using (var fs = File.Create(path))
        {
            // copy with hard size cap
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                {
                    fs.Close();
                    try { File.Delete(path); } catch { }
                    throw new InvalidOperationException($"File exceeds {MaxBytes / 1024 / 1024} MB upload limit.");
                }
                await fs.WriteAsync(buffer.AsMemory(0, read));
            }
        }

        var size = new FileInfo(path).Length;
        var kind = KindFromExt(ext);

        string previewJson;
        string? schemaSummary;
        if (kind == "image")
        {
            // Images bypass the Python helper entirely — they're attached to the
            // model natively as vision content by ChatEndpoints. The preview is a
            // synthetic stub so the upload response / chat context stay uniform.
            if (size > MaxImageBytes)
            {
                try { File.Delete(path); } catch { }
                throw new InvalidOperationException($"Image exceeds {MaxImageBytes / 1024 / 1024} MB limit for vision input.");
            }
            previewJson = JsonSerializer.Serialize(new { ok = true, kind = "image", note = "Image attached — the assistant sees it directly." });
            schemaSummary = $"image ({ext.TrimStart('.').ToLowerInvariant()}, {Math.Max(1, size / 1024)} KB) — attached to the model as a visual";
        }
        else
        {
            // Generate the preview synchronously (for the upload response)
            var previewRequest = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["mode"] = "preview",
                ["path"] = path,
                ["kind"] = kind,
            });
            previewJson = await RunPythonAsync(previewRequest);
            schemaSummary = SummarizeSchema(kind, previewJson);
        }

        await using var hashStream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream)).ToLowerInvariant();
        var entry = new UploadEntry(fileId, userId, safeName, kind, path, size, DateTime.UtcNow,
            SensitiveContent.Redact(schemaSummary ?? ""), ExpiresUtc: DateTime.UtcNow.AddMinutes(30), Sha256: hash);
        Catalog.Add(entry);
        return (entry, previewJson);
    }

    /// <summary>Compact one-line schema for the LLM context (kept under ~300 chars).</summary>
    private static string? SummarizeSchema(string kind, string previewJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(previewJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
                return null;

            if (kind is "csv" or "tsv" or "xlsx" or "parquet")
            {
                var rows = r.TryGetProperty("total_rows", out var tr) ? tr.GetInt64() : -1L;
                var cols = r.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", c.EnumerateArray().Take(20).Select(x => x.GetString()))
                    : "";
                if (c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 20)
                    cols += $", … (+{c.GetArrayLength() - 20} more)";
                return $"rows={rows} columns=[{cols}]";
            }
            if (kind == "json")
            {
                if (r.TryGetProperty("shape", out var shape))
                {
                    if (shape.GetString() == "array")
                    {
                        var len = r.TryGetProperty("length", out var l) ? l.GetInt64() : -1L;
                        var schema = r.TryGetProperty("schema", out var s) ? s.GetRawText() : "";
                        if (schema.Length > 240) schema = schema[..240] + "…";
                        return $"array length={len} item_schema={schema}";
                    }
                    if (shape.GetString() == "object")
                    {
                        var schema = r.TryGetProperty("schema", out var s) ? s.GetRawText() : "";
                        if (schema.Length > 280) schema = schema[..280] + "…";
                        return $"object top_keys_schema={schema}";
                    }
                }
            }
            if (kind == "pdf")
            {
                var chars = r.TryGetProperty("total_chars", out var tc) ? tc.GetInt64() : -1L;
                return $"pdf total_chars={chars} (use mode='text_range' to read in chunks)";
            }
            // txt / log / md
            var totalChars = r.TryGetProperty("total_chars", out var tc2) ? tc2.GetInt64() : -1L;
            var totalLines = r.TryGetProperty("total_lines", out var tl) ? tl.GetInt64() : -1L;
            return $"text total_chars={totalChars}" + (totalLines >= 0 ? $" lines={totalLines}" : "");
        }
        catch { return null; }
    }

    public static IReadOnlyList<UploadEntry> ListForUser(long userId, string? sessionId = null) => Catalog.List(userId, sessionId);

    public static bool RemoveForUser(long userId, string fileId) => Catalog.Remove(userId, fileId);

    // ---------------------------------------------------------------- Internals


    private static string KindFromExt(string ext) => ext.ToLowerInvariant() switch
    {
        ".csv" => "csv",
        ".tsv" => "tsv",
        ".json" => "json",
        ".xlsx" or ".xls" => "xlsx",
        ".pdf" => "pdf",
        ".parquet" => "parquet",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "image",
        _ => "txt",
    };

    /// <summary>MIME type for image attachments passed to the vision model.</summary>
    public static string ImageMimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    private static void Cleanup() => Catalog.Cleanup();

    internal static async Task<string> RunPythonAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        var script = LoadEmbeddedScript("file_inspect.py");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            ToolExecutionContext.Current?.CancellationToken ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("FINOPS_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);

        // The helper refuses to open anything outside this root, and fails closed
        // when the variable is missing.
        psi.Environment["FINOPS_UPLOAD_ROOT"] = TempFileHelper.UploadRoot;

        var pipTarget = "/home/site/pip-packages";
        if (Directory.Exists(pipTarget))
        {
            var existing = Environment.GetEnvironmentVariable("PYTHONPATH") ?? "";
            psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing) ? pipTarget : $"{pipTarget}:{existing}";
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(requestJson.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);
            cancellationToken.ThrowIfCancellationRequested();
            ToolExecutionContext.Current?.CancellationToken.ThrowIfCancellationRequested();
            return Json(new { ok = false, error = $"file_inspect timed out after {TimeoutSeconds}s" });
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return Json(new { ok = false, error = "File inspection failed. Verify the file format and narrow the query." });

        return SensitiveContent.Redact(stdout.Trim());
    }

    internal static object? JsonValueToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonValueToObject).ToArray(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(property => property.Name, property => JsonValueToObject(property.Value)),
        _ => null,
    };

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...(truncated)";

    private static string LoadEmbeddedScript(string filename)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = $"AzureFinOps.Dashboard.AI.Tools.Resources.{filename}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
