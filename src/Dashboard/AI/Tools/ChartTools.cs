using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AzureFinOps.Dashboard.AI.Tools;

public static class ChartTools
{
    private static ILogger? _logger;

    public static IEnumerable<AIFunction> Create(ILogger? logger = null)
    {
        _logger = logger;
        yield return AIFunctionFactory.Create(
            (
                [Description(@"Chart type:
• bar — compare discrete categories side-by-side.
• horizontal_bar — same as bar, categories on y-axis (use for many/long category names; xAxisName=value, yAxisName=category).
• line — trends over time / continuous data.
• pie — composition / parts-of-a-whole (≤6 slices). Click-to-select highlighting + Total subtitle.
• scatter — correlation between two variables.
• funnel — sequential drop-off stages.
• race — animated line with end labels (multi-series racing over time).")] string type,
                [Description("Chart title")] string title,
                [Description("Series name for the legend")] string seriesName,
                [Description(@"Data as JSON array string.
Single series — ALWAYS use the key 'value' for the numeric (do NOT name it after the series, e.g. don't use 'USD'):
  [[""Apple"",100],[""Banana"",200]]
  [{""name"":""A"",""value"":100},{""name"":""B"",""value"":200}]
Multi-series (grouped bar/line) — one extra key per series:
  [{""name"":""D2s_v5"",""East US"":70,""West Europe"":84},{""name"":""D4s_v5"",""East US"":140,""West Europe"":168}]")] string data,
                [Description("X-axis label (optional)")] string? xAxisName,
                [Description("Y-axis label (optional)")] string? yAxisName
            ) =>
            {
                _logger?.LogInformation("RenderChart called: type={Type} title={Title} seriesName={SeriesName} xAxis={XAxis} yAxis={YAxis} dataLen={DataLen}",
                    type, title, seriesName, xAxisName, yAxisName, data?.Length ?? 0);
                _logger?.LogInformation("RenderChart data: {Data}", data?.Length > 2000 ? data[..2000] + "...(truncated)" : data);
                return JsonSerializer.Serialize(new { type, title, seriesName, data, xAxisName, yAxisName });
            },
            "RenderChart",
            "Renders an interactive ECharts chart for single or multi-series data.");

        yield return AIFunctionFactory.Create(
            (
                [Description(@"Full ECharts option object as JSON string. World maps: series type 'map' with map:'world'. Use Natural Earth country names ('United States of America' not 'USA', 'Czechia' not 'Czech Republic'). Frontend auto-registers world map GeoJSON.")] string options
            ) =>
            {
                ValidateAdvancedOptions(options);
                _logger?.LogInformation("RenderAdvancedChart called: optionsLen={OptionsLen}", options?.Length ?? 0);
                _logger?.LogInformation("RenderAdvancedChart options: {Options}", options?.Length > 4000 ? options[..4000] + "...(truncated)" : options);
                return JsonSerializer.Serialize(new { raw = true, options });
            },
            "RenderAdvancedChart",
            @"Renders any ECharts visualization from raw options JSON. Use for world maps, heatmaps, treemaps, radar, gauge, or anything needing full ECharts config.

CRITICAL: use static JSON values only. DOM/CSS/link-bearing options (`extraCssText`, tooltip formatter HTML, links, append targets, remote image symbols) are rejected, and the frontend forces rich-text/canvas tooltips.

WORLD MAP — effectScatter on geo (e.g. Azure region pricing):
- Series: type:'effectScatter', coordinateSystem:'geo'.
- Data point: {name:'East US ($0.192/hr)', value:[lon, lat, price], symbolSize:20, itemStyle:{color:'#27ae60'}, label:{show:true, formatter:'{b}', position:'right', fontSize:9}}.
- Embed price in `name` so tooltips show it without a formatter function.
- symbolSize per point: 12=cheap / 18=mid / 26=expensive. Color via itemStyle.color: GREEN #27ae60 (cheap third), ORANGE #f39c12 (mid third), RED #e74c3c (top third).
- geo: {map:'world', roam:true, itemStyle:{areaColor:'#e0e0e0', borderColor:'#ccc'}, emphasis:{itemStyle:{areaColor:'#ddd'}}}.
- visualMap: {min,max, left:'left', bottom:30, text:['Expensive','Cheap'], calculable:true, inRange:{color:['#27ae60','#f39c12','#e74c3c']}}.
- rippleEffect:{brushType:'stroke', scale:3}, tooltip:{trigger:'item'} — NO formatter.
- Azure region [lon, lat] coordinates: src/Dashboard/AI/Tools/Resources/world-map-coordinates.json (auto-loaded). Approximate fallbacks: eastus≈[-79,37], westeurope≈[5,52], swedencentral≈[18,59], japaneast≈[140,36], australiaeast≈[151,-34].
- Non-pricing maps: uniform blue #0078D4 dots, symbolSize:10, no visualMap.");

    }

    private static void ValidateAdvancedOptions(string options)
    {
        if (string.IsNullOrWhiteSpace(options))
            throw new ArgumentException("Advanced chart options are required.", nameof(options));
        if (options.Length > 100_000)
            throw new ArgumentException("Advanced chart options exceed the 100 KB limit.", nameof(options));

        using var document = JsonDocument.Parse(options);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Advanced chart options must be a JSON object.", nameof(options));
        ValidateNode(document.RootElement, false, 0);
    }

    private static void ValidateNode(JsonElement node, bool isTooltip, int depth)
    {
        if (depth > 30)
            throw new ArgumentException("Advanced chart options exceed the maximum nesting depth.");

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                var name = property.Name;
                if (name.Equals("extraCssText", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("appendTo", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("appendToBody", StringComparison.OrdinalIgnoreCase)
                    // A string `link` is a clickable URL; `axisPointer.link` is an
                    // array of axis bindings and stays allowed. `target` is only a
                    // navigation target next to such a link — in sankey/graph
                    // `links[].target` is a node name and must keep working.
                    || (name.Equals("link", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    || (name.Equals("target", StringComparison.OrdinalIgnoreCase)
                        && node.TryGetProperty("link", out _))
                    || (isTooltip && name.Equals("formatter", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Advanced chart option '{name}' is not allowed.");

                ValidateNode(
                    property.Value,
                    name.Equals("tooltip", StringComparison.OrdinalIgnoreCase),
                    depth + 1);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) ValidateNode(item, isTooltip, depth + 1);
        }
        else if (node.ValueKind == JsonValueKind.String)
        {
            var value = node.GetString() ?? "";
            if (value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("image://", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Advanced chart options cannot contain executable or remote-image URLs.");
        }
    }
}
