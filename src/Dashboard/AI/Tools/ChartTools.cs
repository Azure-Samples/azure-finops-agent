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
                [Description(@"Chart type — choose based on purpose:
• bar — compare discrete categories (e.g. cost by service, VM sizes). Best for side-by-side comparison.
• horizontal_bar — same as bar but with bars running left→right (categories on the y-axis). Best when you have many categories or long category names. xAxisName should describe the value (e.g. ""USD""), yAxisName the category (e.g. ""Service"").
• line — show trends over time (e.g. daily spend, monthly growth). Best for continuous data.
• pie — show composition/proportions (e.g. cost breakdown by service). Best for parts-of-a-whole. Solid pie with click-to-select slice highlighting and a Total subtitle.
• scatter — show correlation between two variables (e.g. CPU vs cost). Best for distribution analysis.
• funnel — show drop-off stages (e.g. pipeline conversion). Best for sequential stage data.
• race — animated line chart with end labels showing final values (e.g. cost trends by service over months, regional spend over time). Best for comparing how multiple series evolve and race against each other.")] string type,
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
            "Renders an interactive ECharts chart. Use for straightforward single-series or multi-series data visualization.");

        yield return AIFunctionFactory.Create(
            (
                [Description(@"Full ECharts option object as JSON string.
For world maps: use series type 'map' with map:'world'.
Use Natural Earth country names (e.g. 'United States of America' not 'USA', 'Czechia' not 'Czech Republic').
The frontend auto-registers world map GeoJSON.")] string options
            ) =>
            {
                _logger?.LogInformation("RenderAdvancedChart called: optionsLen={OptionsLen}", options?.Length ?? 0);
                _logger?.LogInformation("RenderAdvancedChart options: {Options}", options?.Length > 4000 ? options[..4000] + "...(truncated)" : options);
                return JsonSerializer.Serialize(new { raw = true, options });
            },
            "RenderAdvancedChart",
            @"Renders any ECharts visualization using raw options JSON. Use for world maps, heatmaps, treemaps, radar, gauge, or charts needing full ECharts config.

CRITICAL: The options JSON is parsed with JSON.parse(). Do NOT include JavaScript functions — they cannot be serialized in JSON and will be ignored. Use only static values.

WORLD MAP — effectScatter on geo (Azure region pricing):
- Series: type:'effectScatter', coordinateSystem:'geo'.
- Data format per point: {name:'East US ($0.192/hr)', value:[lon, lat, price], symbolSize:20, itemStyle:{color:'#27ae60'}, label:{show:true, formatter:'{b}', position:'right', fontSize:9}}.
- Include the price in the name field so tooltips show it without a formatter function.
- Set symbolSize as a NUMBER per data point (12=cheap / 18=mid / 26=expensive). Color per point via itemStyle.color: GREEN #27ae60 (cheap third), ORANGE #f39c12 (mid third), RED #e74c3c (top third).
- geo: {map:'world', roam:true, itemStyle:{areaColor:'#e0e0e0', borderColor:'#ccc'}, emphasis:{itemStyle:{areaColor:'#ddd'}}}.
- visualMap: {min,max, left:'left', bottom:30, text:['Expensive','Cheap'], calculable:true, inRange:{color:['#27ae60','#f39c12','#e74c3c']}}.
- series rippleEffect: {brushType:'stroke', scale:3}. tooltip: {trigger:'item'} — no formatter functions.
- Azure region [lon, lat] coordinates are loaded by the frontend from src/Dashboard/AI/Tools/Resources/world-map-coordinates.json — reference any Azure region by its ARM name (eastus, westeurope, swedencentral, etc.) and look it up there. Modern LLMs also know approximate lon/lat for major cities — eastus≈[-79,37], westeurope≈[5,52], swedencentral≈[18,59], japaneast≈[140,36], australiaeast≈[151,-34] — these are accurate enough for a world map.
- For non-pricing maps (just region locations), use uniform blue #0078D4 dots with symbolSize:10 on the series level and no visualMap.");

    }
}
