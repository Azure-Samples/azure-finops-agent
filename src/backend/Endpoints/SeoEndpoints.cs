using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace AzureFinOps.Dashboard.Endpoints;

/// <summary>
/// Server-rendered SEO HTML for FAQ pages and the sitemap. Public, no auth.
/// </summary>
public static class SeoEndpoints
{
    private static readonly Dictionary<string, (string Title, string Question, string Answer, string Prompt)> StaticFaqs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["azure-vm-pricing"] = (
            "Azure VM Pricing by Region — D4s_v5 Monthly Cost Comparison",
            "How much does a D4s_v5 VM cost per month on Azure?",
            "A Standard_D4s_v5 VM (4 vCPUs, 16 GB RAM) on Azure costs approximately $98–$168/month depending on the region. The cheapest regions are typically US Central, Central India, and US West. Pricing is calculated at the hourly pay-as-you-go rate × 730 hours/month. Use Azure Reserved Instances for 30–65% savings on steady-state workloads.",
            "Compare the monthly cost of a D4s_v5 VM across the 10 cheapest Azure regions. Show a bar chart."),
            ["spot-vs-on-demand"] = (
            "Azure Spot vs On-Demand VM Pricing — Savings Comparison",
            "What is the difference between Azure spot and on-demand VM pricing?",
            "Azure spot VMs offer up to 80% discount compared to on-demand pricing, but can be evicted when Azure needs capacity. For example, a D4s_v5 costs ~$0.192/hr on-demand vs ~$0.038/hr spot (80% savings). Spot is ideal for fault-tolerant workloads like batch processing, CI/CD, and dev/test environments.",
            "Compare spot vs on-demand pricing for D4s_v5, D8s_v5, and NC24ads_A100_v4 in East US."),
            ["reserved-instances"] = (
            "Azure Reserved Instances vs Pay-As-You-Go — Pricing Comparison",
            "How do Azure Reserved Instances compare to pay-as-you-go?",
            "Azure Reserved Instances offer 30–40% savings for 1-year commitments and 55–65% savings for 3-year commitments compared to pay-as-you-go pricing. They're best for predictable, steady-state workloads running 24/7. Savings Plans offer similar discounts with more flexibility across VM families and regions.",
            "Compare pay-as-you-go vs 1-year vs 3-year reserved pricing for a D4s_v5 VM in East US."),
            ["finops-azure"] = (
            "What is FinOps? — Azure Cloud Financial Management Guide",
            "What is FinOps and how does it apply to Azure?",
            "FinOps is a cloud financial management discipline combining finance, technology, and business to optimize cloud spending. On Azure, key FinOps practices include: right-sizing VMs based on Advisor recommendations, using reservations and savings plans for committed workloads, cleaning up idle resources (unattached disks, unused IPs), implementing cost allocation tags, setting budgets with alerts, and running regular cost reviews.",
            "Conduct a FinOps maturity assessment of my Azure environment."),
            ["idle-resources"] = (
            "Find Idle Azure Resources — Cost Optimization Guide",
            "How can I find idle or unused Azure resources to reduce costs?",
            "Common idle resources include: unattached managed disks, unused public IPs, empty resource groups, VMs with consistently low CPU (<5%), App Service plans with no apps, orphaned NICs, and NSGs not attached to subnets. Azure Advisor provides cost recommendations, and Azure FinOps Agent can scan all subscriptions simultaneously to quantify the total waste.",
            "Find all idle or underutilized VMs, disks, public IPs, and App Service plans across my subscriptions."),
            ["storage-tier-pricing"] = (
            "Azure Blob Storage Tier Pricing — Hot, Cool, Cold, Archive Comparison",
            "How do Azure Blob Storage tiers compare in pricing?",
            "Azure Blob Storage tiers from most to least expensive for storage: Hot (~$0.018/GB), Cool (~$0.01/GB, 30-day minimum), Cold (~$0.0036/GB, 90-day minimum), Archive (~$0.00099/GB, 180-day minimum). Access costs are inverse — Archive has the highest retrieval fees and hours-long rehydration. Use lifecycle management policies to automatically tier data based on access patterns.",
            "Compare Azure Blob Storage costs for 10 TB across Hot, Cool, Cold, and Archive tiers in East US."),
            ["aks-vs-container-apps"] = (
            "AKS vs Container Apps vs Azure Functions — Cost Comparison",
            "What is the cost of running microservices on AKS vs Container Apps vs Functions?",
            "AKS charges only for underlying VMs (control plane is free). Container Apps has consumption billing per vCPU-second and GB-second. Functions consumption plan charges per execution and GB-second. For 20 microservices: AKS is cheapest at scale (~$300–600/mo), Container Apps is simplest for moderate traffic (~$200–800/mo), Functions is cheapest for bursty/infrequent workloads (~$50–300/mo).",
            "Compare cost of running 20 microservices on AKS vs Azure Container Apps vs Azure Functions."),
            ["gpu-training-cost"] = (
            "Azure GPU Training Cost — A100 vs H100 Pricing Comparison",
            "How much does GPU training cost on Azure (A100 vs H100)?",
            "The ND96asr_v4 (8× A100 80GB) costs ~$27–32/hr on-demand. The NC80adis_H100_v5 (8× H100) costs ~$32–38/hr. Spot pricing reduces costs 60–80% when available. A 4-node training cluster costs $80K–110K/month on-demand, or $16K–30K with spot instances. H100 offers ~2× training throughput vs A100, making cost-per-training-step often comparable.",
            "Compare monthly cost of 4x A100 (ND96asr_v4) vs 4x H100 (NC80adis_H100_v5) on-demand."),
        };

    public static void MapSeoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/faq/{slug}", (string slug, HttpContext ctx) =>
        {
            string title, question, answer, prompt, date;
            if (StaticFaqs.TryGetValue(slug, out var page))
            {
                title = page.Title; question = page.Question; answer = page.Answer; prompt = page.Prompt; date = "2026-03-29";
            }
            else if (FaqTools.TryGet(slug, out var dynamic))
            {
                title = dynamic.Title; question = dynamic.Question; answer = dynamic.Answer; prompt = question; date = dynamic.CreatedUtc;
            }
            else
            {
                return Results.NotFound("FAQ page not found") as IResult;
            }

            return Results.Content(RenderFaqHtml(BaseUrl(ctx), slug, title, question, answer, prompt, date), "text/html; charset=utf-8");
        });

        app.MapGet("/faq", (HttpContext ctx) => Results.Content(RenderFaqIndex(BaseUrl(ctx)), "text/html"));

        app.MapGet("/sitemap.xml", (HttpContext ctx) => Results.Content(RenderSitemap(BaseUrl(ctx)), "application/xml"));
    }

    // Public base URL for canonical links, sitemap, and structured data — derived from the
    // incoming request so the deployed hostname is used automatically. Works on any azd
    // deployment host, a custom domain, or localhost, with no hardcoded domain.
    private static string BaseUrl(HttpContext ctx) => $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    private static string RenderFaqHtml(string baseUrl, string slug, string title, string question, string answer, string prompt, string date)
    {
        Func<string?, string> e = System.Net.WebUtility.HtmlEncode!;
        var desc = answer.Length > 155 ? answer[..155] + "..." : answer;
        var faqUrl = $"{baseUrl}/faq/{slug}";
        var isoDate = date + "T00:00:00+00:00";
        var jsonLd = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "QAPage",
            ["mainEntity"] = new Dictionary<string, object>
            {
                ["@type"] = "Question",
                ["name"] = question,
                ["text"] = question,
                ["answerCount"] = 1,
                ["dateCreated"] = isoDate,
                ["datePublished"] = isoDate,
                ["author"] = new Dictionary<string, object>
                {
                    ["@type"] = "Organization",
                    ["name"] = "Azure FinOps Agent",
                    ["url"] = baseUrl
                },
                ["acceptedAnswer"] = new Dictionary<string, object>
                {
                    ["@type"] = "Answer",
                    ["text"] = answer,
                    ["dateCreated"] = isoDate,
                    ["datePublished"] = isoDate,
                    ["upvoteCount"] = 1,
                    ["url"] = faqUrl,
                    ["author"] = new Dictionary<string, object>
                    {
                        ["@type"] = "Organization",
                        ["name"] = "Azure FinOps Agent",
                        ["url"] = baseUrl
                    }
                }
            }
        });
        return "<!DOCTYPE html><html lang=\"en\"><head>"
            + "<meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\">"
            + "<title>" + e(title) + "</title>"
            + "<meta name=\"description\" content=\"" + e(desc) + "\">"
            + "<meta name=\"robots\" content=\"index, follow\">"
            + "<link rel=\"canonical\" href=\"" + faqUrl + "\">"
            + "<meta property=\"og:type\" content=\"article\">"
            + "<meta property=\"og:title\" content=\"" + e(title) + "\">"
            + "<meta property=\"og:url\" content=\"" + faqUrl + "\">"
            + "<script type=\"application/ld+json\">" + jsonLd + "</script>"
            + "<style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:800px;margin:0 auto;padding:2rem 1rem;color:#1a1a2e;line-height:1.7}h1{font-size:1.6rem;color:#0078d4}h2{font-size:1.2rem;margin-top:2rem}.answer{background:#f0f6ff;border-left:4px solid #0078d4;padding:1rem 1.5rem;border-radius:0 8px 8px 0;margin:1.5rem 0}.cta{display:inline-block;background:#0078d4;color:#fff;padding:0.75rem 1.5rem;border-radius:8px;text-decoration:none;margin-top:1.5rem;font-weight:600}.cta:hover{background:#106ebe}footer{margin-top:3rem;font-size:0.85rem;color:#888}</style>"
            + "</head><body>"
            + "<h1>" + e(title) + "</h1>"
            + "<h2>" + e(question) + "</h2>"
            + "<div class=\"answer\"><p>" + e(answer) + "</p></div>"
            + "<p>Want a live, interactive answer with real-time Azure pricing data and charts?</p>"
            + "<a class=\"cta\" href=\"/?q=" + Uri.EscapeDataString(prompt) + "\">Ask the FinOps Agent</a>"
            + "<footer><p>Azure FinOps Agent &mdash; AI-powered cloud cost optimization. <a href=\"/\">Back to home</a></p>"
            + "<p>Pricing data from <a href=\"https://prices.azure.com\">Azure Retail Prices API</a>. Prices may vary.</p></footer>"
            + "</body></html>";
    }

    private static string RenderFaqIndex(string baseUrl)
    {
        Func<string?, string> e = System.Net.WebUtility.HtmlEncode!;
        var listItems = string.Join("", StaticFaqs.Select(kv =>
            "<li><a href=\"/faq/" + kv.Key + "\">" + e(kv.Value.Question) + "</a></li>"));
        listItems += string.Join("", FaqTools.GetAll().Select(kv =>
            "<li><a href=\"/faq/" + kv.Key + "\">" + e(kv.Value.Question) + "</a> <small style=\"color:#888\">(community)</small></li>"));

        return "<!DOCTYPE html><html lang=\"en\"><head>"
            + "<meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\">"
            + "<title>Azure FinOps FAQ &mdash; Cloud Cost Optimization Questions &amp; Answers</title>"
            + "<meta name=\"description\" content=\"Frequently asked questions about Azure cloud cost optimization, VM pricing, reserved instances, FinOps best practices, and cost management.\">"
            + "<meta name=\"robots\" content=\"index, follow\">"
            + "<link rel=\"canonical\" href=\"" + baseUrl + "/faq\">"
            + "<style>body{font-family:Segoe UI,system-ui,sans-serif;max-width:800px;margin:0 auto;padding:2rem 1rem;color:#1a1a2e;line-height:1.7}h1{color:#0078d4}ul{padding-left:1.2rem}li{margin:0.75rem 0}a{color:#0078d4}.cta{display:inline-block;background:#0078d4;color:#fff;padding:0.75rem 1.5rem;border-radius:8px;text-decoration:none;margin-top:1.5rem;font-weight:600}</style>"
            + "</head><body>"
            + "<h1>Azure FinOps FAQ</h1>"
            + "<p>Common questions about Azure cloud cost optimization, answered with real pricing data.</p>"
            + "<ul>" + listItems + "</ul>"
            + "<a class=\"cta\" href=\"/\">Try the FinOps Agent</a>"
            + "<footer style=\"margin-top:3rem;font-size:0.85rem;color:#888\"><p>Azure FinOps Agent &mdash; AI-powered cloud cost optimization. <a href=\"/\">Home</a></p></footer>"
            + "</body></html>";
    }

    private static string RenderSitemap(string baseUrl)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var urls = "<url><loc>" + baseUrl + "/</loc><lastmod>" + today + "</lastmod><changefreq>weekly</changefreq><priority>1.0</priority></url>"
            + "<url><loc>" + baseUrl + "/faq</loc><lastmod>" + today + "</lastmod><changefreq>weekly</changefreq><priority>0.9</priority></url>";

        foreach (var kv in StaticFaqs)
            urls += "<url><loc>" + baseUrl + "/faq/" + kv.Key + "</loc><lastmod>" + today + "</lastmod><changefreq>monthly</changefreq><priority>0.8</priority></url>";

        foreach (var kv in FaqTools.GetAll())
            urls += "<url><loc>" + baseUrl + "/faq/" + kv.Key + "</loc><lastmod>" + kv.Value.CreatedUtc + "</lastmod><changefreq>monthly</changefreq><priority>0.7</priority></url>";

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" + urls + "</urlset>";
    }
}
