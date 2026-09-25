using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Reads Azure Blob Storage blobs (CSV cost exports) using the user's delegated storage token.
/// Designed for reading FOCUS-format cost export data from scheduled exports.
/// </summary>
public class StorageQueryTools
{
    private readonly UserTokens _tokens;

    public StorageQueryTools(UserTokens tokens) => _tokens = tokens;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(ListCostExportBlobs, "ListCostExportBlobs", @"Lists at most 50 blobs in an Azure Storage container to discover cost export files. Use before ReadCostExportBlob. Scheduled exports usually write `{exportName}/{YYYYMMDD-YYYYMMDD}/{file}.csv`. Pass the narrowest known export/date prefix so the service filters results; do not list the container root when the requested export or period is known. Returns XML with blob names, sizes, and last-modified timestamps. A nonempty NextMarker means this listing is partial; refine the prefix instead of claiming all exports were inspected.");

        yield return AIFunctionFactory.Create(ReadCostExportBlob, "ReadCostExportBlob", @"Returns at most 512000 characters of CSV from one exact Azure Storage blob. This is an output limit, not server-side row filtering or a download-byte limit. Select the smallest relevant date-partitioned blob through a prefix-filtered ListCostExportBlobs call first. Never compute whole-export totals from a truncated sample. For full analysis, request an upload for QueryUploadedFile; for a requested repeatable download/analysis workflow, call GenerateScript directly with complete user-run Azure CLI or PowerShell code using the user's own login (`az login` and `--auth-mode login` for Azure CLI). Never request or embed bearer tokens or claim the generated script was executed.

FOCUS columns: BilledCost, EffectiveCost, ServiceCategory, ServiceName, SubAccountName, ResourceId, Region, ChargeCategory, PricingModel, etc.");
    }

    private async Task<string> ListCostExportBlobs(
        [Description("Storage account name (e.g. 'mystorageaccount')")] string storageAccount,
        [Description("Container name (e.g. 'exports' or 'cost-exports')")] string container,
        [Description("Blob prefix/path used by Storage to filter results (e.g. 'monthly-export/202604'). Omit only when neither export name nor period is known; at most 50 matches are returned.")] string? prefix = null)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("ListCostExportBlobs");
        activity?.SetTag("storage.account", storageAccount);
        activity?.SetTag("storage.container", container);
        activity?.SetTag("storage.prefix", prefix);

        var token = _tokens.StorageToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("StorageToken", activity, "storage");

        if (string.IsNullOrWhiteSpace(storageAccount) || string.IsNullOrWhiteSpace(container))
        {
            activity?.SetTag("storage.result", "invalid_input");
            return "HTTP 400 BadRequest\nBoth storageAccount and container are required.";
        }

        var url = $"https://{Uri.EscapeDataString(storageAccount)}.blob.core.windows.net/{Uri.EscapeDataString(container)}?restype=container&comp=list&maxresults=50";
        if (!string.IsNullOrWhiteSpace(prefix))
            url += $"&prefix={Uri.EscapeDataString(prefix)}";

        return await HttpHelper.SendWithRetryAsync(
            url, token, activity, "storage",
            extraHeaders: new Dictionary<string, string> { ["x-ms-version"] = "2026-02-06" },
            includeTimestamp: true);
    }

    private async Task<string> ReadCostExportBlob(
        [Description("Storage account name")] string storageAccount,
        [Description("Container name")] string container,
        [Description("Exact blob path/name from a prefix-filtered listing. Select only the requested export/date partition, not a container root or a SAS URL.")] string blobPath)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("ReadCostExportBlob");
        activity?.SetTag("storage.account", storageAccount);
        activity?.SetTag("storage.container", container);
        activity?.SetTag("storage.blob", blobPath);

        var token = _tokens.StorageToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("StorageToken", activity, "storage");

        if (string.IsNullOrWhiteSpace(storageAccount) || string.IsNullOrWhiteSpace(container) || string.IsNullOrWhiteSpace(blobPath))
        {
            activity?.SetTag("storage.result", "invalid_input");
            return "HTTP 400 BadRequest\nstorageAccount, container, and blobPath are all required.";
        }

        var url = $"https://{Uri.EscapeDataString(storageAccount)}.blob.core.windows.net/{Uri.EscapeDataString(container)}/{blobPath}";

        return await HttpHelper.SendWithRetryAsync(
            url, token, activity, "storage",
            extraHeaders: new Dictionary<string, string> { ["x-ms-version"] = "2026-02-06" },
            includeTimestamp: true,
            maxResponseChars: 512_000); // 500KB limit — use Python for full file
    }
}
