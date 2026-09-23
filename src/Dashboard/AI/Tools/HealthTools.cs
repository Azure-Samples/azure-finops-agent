using System.ComponentModel;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

public static class HealthTools
{
    private static readonly HttpClient Http = new();

    public static IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetAzureServiceHealth, "GetAzureServiceHealth",
            "Returns current Azure service health status and active incidents from the public Azure Status RSS feed.");
    }

    private static async Task<string> GetAzureServiceHealth()
    {
        var url = "https://azure.status.microsoft/en-us/status/feed/";
        var xml = await Http.GetStringAsync(url);

        var doc = XDocument.Parse(xml);
        var channel = doc.Root?.Element("channel");

        var items = channel?.Elements("item")
            .Select(item => new
            {
                title = item.Element("title")?.Value,
                description = item.Element("description")?.Value,
                pubDate = item.Element("pubDate")?.Value,
                link = item.Element("link")?.Value
            })
            .ToList() ?? [];

        var result = new
        {
            status = items.Count == 0 ? "AllGood" : "ActiveIncidents",
            summary = items.Count == 0
                ? "All Azure services are operating normally. No active incidents."
                : $"{items.Count} active incident(s) reported.",
            activeIncidents = items
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        return $"Current UTC time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\nSource: {url}\n{json}";
    }
}
