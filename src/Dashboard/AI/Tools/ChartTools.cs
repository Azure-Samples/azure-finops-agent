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
- Data format per point: {name:'East US ($0.192/hr)', value:[-79.0, 37.3, 0.192], symbolSize:20, itemStyle:{color:'#27ae60'}, label:{show:true, formatter:'{b}', position:'right', fontSize:9}}.
- IMPORTANT: Set symbolSize as a NUMBER on EACH data point (not on the series, not as a function). Scale it by price: cheap regions symbolSize:12, mid-range symbolSize:18, expensive symbolSize:26.
- Color each dot individually via itemStyle.color: GREEN (#27ae60) = cheap (bottom third of price range), ORANGE (#f39c12) = mid-range (middle third), RED (#e74c3c) = expensive (top third).
- Include the price in the name field like 'East US ($0.192/hr)' so tooltips show the price without needing a formatter function.
- geo config: {map:'world', roam:true, itemStyle:{areaColor:'#e0e0e0', borderColor:'#ccc'}, emphasis:{itemStyle:{areaColor:'#ddd'}}}.
- visualMap: {min:<lowest_price>, max:<highest_price>, left:'left', bottom:30, text:['Expensive','Cheap'], calculable:true, inRange:{color:['#27ae60','#f39c12','#e74c3c']}}.
- rippleEffect: {brushType:'stroke', scale:3} on the series for animated pulsing dots.
- tooltip: {trigger:'item'} — do NOT use a formatter function. The name field already contains the price.
- Azure region coordinates [lon, lat]: eastus=[-79.0,37.3], eastus2=[-78.4,36.7], westus=[-122.0,37.4], westus2=[-119.8,47.2], westus3=[-112.1,33.4], centralus=[-93.6,41.6], northcentralus=[-87.6,41.9], southcentralus=[-98.5,29.4], westeurope=[4.9,52.4], northeurope=[-6.3,53.3], uksouth=[-0.1,51.5], ukwest=[-3.2,51.5], francecentral=[2.3,46.6], francesouth=[3.0,43.6], germanywestcentral=[8.7,50.1], germanynorth=[9.7,53.6], norwayeast=[10.7,59.9], norwaywest=[5.3,60.4], swedencentral=[18.1,59.3], swedensouth=[13.0,55.6], switzerlandnorth=[8.5,47.4], switzerlandwest=[6.1,46.2], polandcentral=[21.0,52.2], italynorth=[9.2,45.5], spaincentral=[-3.7,40.4], austriaeast=[16.4,48.2], denmarkeast=[12.6,55.7], belgiumcentral=[4.4,50.8], southeastasia=[103.8,1.3], eastasia=[114.2,22.3], japaneast=[139.7,35.7], japanwest=[135.5,34.7], koreacentral=[127.0,37.6], koreasouth=[129.1,35.2], australiaeast=[151.2,-33.9], australiasoutheast=[144.9,-37.8], australiacentral=[149.1,-35.3], centralindia=[73.9,18.5], southindia=[80.2,12.9], westindia=[72.9,19.1], canadacentral=[-79.4,43.7], canadaeast=[-71.2,46.8], brazilsouth=[-46.6,-23.5], brazilsoutheast=[-43.2,-22.9], southafricanorth=[28.2,-25.7], southafricawest=[18.4,-33.9], uaenorth=[55.3,25.3], uaecentral=[54.4,24.5], qatarcentral=[51.4,25.3], israelcentral=[34.8,31.3], mexicocentral=[-99.1,19.4], chilecentral=[-70.7,-33.5], newzealandnorth=[174.8,-36.8], taiwannorth=[121.5,25.0], indonesiacentral=[106.8,-6.2], malaysiawest=[101.7,3.1].
- For non-pricing maps (just showing region locations), use uniform blue (#0078D4) dots with symbolSize:10 on the series level and no visualMap.");

    }
}
