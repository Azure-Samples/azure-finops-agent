using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.Auth;
using Microsoft.Extensions.AI;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Publishes useful public Q&As as SEO-indexable FAQ pages.
/// The LLM calls PublishFAQ after answering a public FinOps question.
/// Requires Azure authentication — anonymous users are blocked at the tool layer.
/// </summary>
public class FaqTools
{
    private readonly UserTokens _tokens;

    public FaqTools(UserTokens tokens) => _tokens = tokens;

    private static readonly string FaqDir = Path.Combine(Path.GetTempPath(), "finops-agent-faq");
    private static readonly string FaqFile = Path.Combine(FaqDir, "dynamic-faqs.json");
    private static readonly ConcurrentDictionary<string, FaqEntry> DynamicFaqs = new(StringComparer.OrdinalIgnoreCase);

    // Set FAQ_AUTO_APPROVE=true to publish + index FAQs without manual review (legacy behavior).
    // Default: false — entries are saved as pending, NOT linked from sitemap or pinged to IndexNow.
    private static readonly bool AutoApprove =
        string.Equals(Environment.GetEnvironmentVariable("FAQ_AUTO_APPROVE"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly string ApprovalKey = Environment.GetEnvironmentVariable("FAQ_APPROVAL_KEY") ?? "";
    private static readonly string IndexNowKey = Environment.GetEnvironmentVariable("INDEXNOW_KEY") ?? "finopsagent2026";

    static FaqTools()
    {
        Directory.CreateDirectory(FaqDir);
        Load();
    }

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(PublishFAQ);
    }

    [Description("Publish a useful public FinOps Q&A as an SEO page. Call this ONLY for questions about Azure pricing, cost optimization, or FinOps best practices that would be useful to other users. Do NOT publish tenant-specific or private data.")]
    private string PublishFAQ(
        [Description("The question (e.g. 'How much does a D4s_v5 VM cost per month?')")] string question,
        [Description("A concise, factual answer (1-3 sentences with specific numbers)")] string answer,
        [Description("SEO page title (e.g. 'Azure D4s_v5 VM Pricing by Region')")] string title)
    {
        // Hard auth gate — anon users (all-null tokens) must not write to the public FAQ surface.
        if (_tokens.AzureToken is null)
            return "PublishFAQ requires authentication. Please use the **Connect Azure** button to sign in before publishing FAQ entries to the public site.";

        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer) || string.IsNullOrWhiteSpace(title))
            return "Error: question, answer, and title are all required.";

        var slug = GenerateSlug(question);
        // Avoid silent overwrite of existing distinct entries
        if (DynamicFaqs.TryGetValue(slug, out var existing) && existing.Question != question)
            slug = $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";

        var entry = new FaqEntry
        {
            Slug = slug,
            Title = title,
            Question = question,
            Answer = answer,
            CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Approved = AutoApprove,
        };

        DynamicFaqs[slug] = entry;
        Save();

        if (entry.Approved)
            _ = PingIndexNowAsync(slug);

        return JsonSerializer.Serialize(new
        {
            published = entry.Approved,
            pending_review = !entry.Approved,
            url = $"/faq/{slug}",
            note = entry.Approved ? null : "Saved as pending review — won't appear in sitemap or be indexed until an admin approves it."
        });
    }

    /// <summary>Returns only approved entries — safe for public listings (sitemap, index page).</summary>
    public static IReadOnlyDictionary<string, FaqEntry> GetAll() =>
        (IReadOnlyDictionary<string, FaqEntry>)DynamicFaqs
            .Where(kv => kv.Value.Approved)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns ALL entries including pending — for admin/moderation use only.</summary>
    public static IReadOnlyDictionary<string, FaqEntry> GetAllIncludingPending() => DynamicFaqs;

    /// <summary>Approves a pending entry. Requires FAQ_APPROVAL_KEY env var to match the supplied key.</summary>
    public static bool TryApprove(string slug, string key)
    {
        if (string.IsNullOrEmpty(ApprovalKey) || !string.Equals(key, ApprovalKey, StringComparison.Ordinal))
            return false;
        if (!DynamicFaqs.TryGetValue(slug, out var entry)) return false;
        if (entry.Approved) return true;
        entry.Approved = true;
        Save();
        _ = PingIndexNowAsync(slug);
        return true;
    }

    public static bool TryGet(string slug, out FaqEntry entry)
    {
        if (DynamicFaqs.TryGetValue(slug, out entry!) && entry.Approved) return true;
        entry = null!;
        return false;
    }

    private static string GenerateSlug(string text)
    {
        var slug = text.ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"\s+", "-");
        slug = slug.Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug;
    }

    private static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(DynamicFaqs.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FaqFile, json);
        }
        catch { }
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(FaqFile))
            {
                var json = File.ReadAllText(FaqFile);
                var entries = JsonSerializer.Deserialize<List<FaqEntry>>(json);
                if (entries is not null)
                    foreach (var e in entries)
                        DynamicFaqs[e.Slug] = e;
            }
        }
        catch { }
    }

    private static async Task PingIndexNowAsync(string slug)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = JsonSerializer.Serialize(new
            {
                host = "azure-finops-agent.com",
                urlList = new[] { $"https://azure-finops-agent.com/faq/{slug}" },
                key = IndexNowKey
            });
            await http.PostAsync("https://api.indexnow.org/indexnow",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        }
        catch { }
    }

    public class FaqEntry
    {
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public bool Approved { get; set; } = false;
    }
}
