using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzureFinOps.Dashboard.Services;

/// <summary>
/// A single piece of persistent organizational knowledge the user has provided
/// about their environment (subscription mappings, cost-center ownership, SLA
/// targets, tagging conventions, analysis instructions, etc.). Articles are
/// scoped to one user and injected into the LLM prompt so the agent applies the
/// user's context automatically across sessions.
/// </summary>
public sealed class KnowledgeArticle
{
    /// <summary>Server-generated 8-char lowercase hex id.</summary>
    public string Id { get; set; } = "";

    /// <summary>Short human label, e.g. "Subscription Mappings".</summary>
    public string Title { get; set; } = "";

    /// <summary>One of <see cref="KnowledgeStore.Categories"/>.</summary>
    public string Category { get; set; } = "custom";

    /// <summary>The knowledge body (markdown, CSV, JSON, or free text).</summary>
    public string Content { get; set; } = "";

    /// <summary>Owning user (Entra OID-derived deterministic id).</summary>
    public long UserId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Soft-delete flag — inactive articles are excluded from context injection.</summary>
    public bool Active { get; set; } = true;
}

/// <summary>
/// Thrown when an article fails validation (size limits, unknown category, bad
/// id). Endpoints translate this into an HTTP 400 with the message.
/// </summary>
public sealed class KnowledgeValidationException : Exception
{
    public KnowledgeValidationException(string message) : base(message) { }
}

/// <summary>
/// Per-user, file-based store for organizational knowledge articles. Each user's
/// articles live in a single JSON array file on the durable <c>/home</c> mount
/// (<c>$COPILOT_HOME/knowledge/{userId}/knowledge.json</c>) so they survive
/// container restarts. Writes are atomic (temp file + move) and serialized per
/// user. <see cref="BuildContextBlock"/> produces the prompt-injection block and
/// applies a per-session de-duplication optimization to keep token cost bounded.
/// </summary>
public static class KnowledgeStore
{
    /// <summary>Maximum number of articles a single user may keep.</summary>
    public const int MaxArticlesPerUser = 20;

    /// <summary>Maximum characters in a single article's content.</summary>
    public const int MaxArticleChars = 10_000;

    /// <summary>Maximum total characters across all of a user's articles.</summary>
    public const int MaxTotalChars = 50_000;

    /// <summary>Maximum length of an article title.</summary>
    public const int MaxTitleChars = 120;

    /// <summary>Allowed category values.</summary>
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "subscriptions", "cost_centers", "instructions", "architecture", "sla", "custom"
    };

    /// <summary>
    /// Below this many characters of active content, the full block is injected.
    /// At or above it, callers may switch to an index + lazy-pull strategy
    /// (see the QueryKnowledge tool) to cap per-turn token cost.
    /// </summary>
    public const int FullInjectionCharBudget = 4_000;

    // Re-inject the full block at least this often (in turns) even when unchanged,
    // so the model doesn't lose it if the SDK truncates/summarizes long histories.
    private const int ReinjectEveryTurns = 10;

    private static readonly string Root = Path.Combine(
        Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(Path.GetTempPath(), "copilot"),
        "knowledge");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // Serializes writes for a single user so concurrent requests can't corrupt the file.
    private static readonly ConcurrentDictionary<long, object> UserLocks = new();

    // Per-session injection state for the Tier-1 token optimization:
    // sessionId -> (hash of active content, turns since last full injection).
    private static readonly ConcurrentDictionary<string, (string Hash, int TurnsSince)> InjectionState = new();

    private static string UserDir(long userId) => Path.Combine(Root, userId.ToString());

    private static string FilePath(long userId) => Path.Combine(UserDir(userId), "knowledge.json");

    /// <summary>True when <paramref name="id"/> is a well-formed 8-char lowercase hex id.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length == 8 && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    /// <summary>Returns all articles for a user (active and inactive), or an empty list.</summary>
    public static List<KnowledgeArticle> ListForUser(long userId)
    {
        var path = FilePath(userId);
        if (!File.Exists(path)) return new List<KnowledgeArticle>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<KnowledgeArticle>>(json, JsonOpts) ?? new List<KnowledgeArticle>();
        }
        catch
        {
            // Corrupt/partial file — treat as empty rather than failing the request.
            return new List<KnowledgeArticle>();
        }
    }

    /// <summary>Gets a single article by id, or null if not found or the id is malformed.</summary>
    public static KnowledgeArticle? Get(long userId, string id)
    {
        if (!IsValidId(id)) return null;
        return ListForUser(userId).FirstOrDefault(a => a.Id == id);
    }

    /// <summary>
    /// Creates a new article. Validates the title/category/content and enforces
    /// the per-user count and character budgets. Returns the persisted article
    /// (with generated id and timestamps).
    /// </summary>
    public static KnowledgeArticle Create(long userId, string title, string category, string content)
    {
        title = (title ?? "").Trim();
        category = NormalizeCategory(category);
        content = content ?? "";
        ValidateFields(title, content);

        return WithUserLock(userId, () =>
        {
            var all = ListForUser(userId);
            if (all.Count >= MaxArticlesPerUser)
                throw new KnowledgeValidationException($"Article limit reached ({MaxArticlesPerUser} per user). Delete one first.");
            EnsureTotalBudget(all, addedContentLength: content.Length);

            var now = DateTime.UtcNow;
            var article = new KnowledgeArticle
            {
                Id = NewId(all),
                Title = title,
                Category = category,
                Content = content,
                UserId = userId,
                CreatedUtc = now,
                UpdatedUtc = now,
                Active = true,
            };
            all.Add(article);
            Persist(userId, all);
            return article;
        });
    }

    /// <summary>
    /// Updates an existing article in place. Any null parameter is left unchanged.
    /// Returns the updated article, or null if the id does not exist.
    /// </summary>
    public static KnowledgeArticle? Update(long userId, string id, string? title, string? category, string? content, bool? active)
    {
        if (!IsValidId(id)) return null;

        return WithUserLock(userId, () =>
        {
            var all = ListForUser(userId);
            var article = all.FirstOrDefault(a => a.Id == id);
            if (article is null) return null;

            var newTitle = title is null ? article.Title : title.Trim();
            var newCategory = category is null ? article.Category : NormalizeCategory(category);
            var newContent = content ?? article.Content;
            ValidateFields(newTitle, newContent);
            EnsureTotalBudget(all, addedContentLength: newContent.Length - article.Content.Length);

            article.Title = newTitle;
            article.Category = newCategory;
            article.Content = newContent;
            if (active.HasValue) article.Active = active.Value;
            article.UpdatedUtc = DateTime.UtcNow;

            Persist(userId, all);
            return article;
        });
    }

    /// <summary>Permanently removes an article. Returns true if one was deleted.</summary>
    public static bool Delete(long userId, string id)
    {
        if (!IsValidId(id)) return false;

        return WithUserLock(userId, () =>
        {
            var all = ListForUser(userId);
            var removed = all.RemoveAll(a => a.Id == id);
            if (removed > 0) Persist(userId, all);
            return removed > 0;
        });
    }

    /// <summary>Deletes a user's entire knowledge directory (called by the janitor on reap).</summary>
    public static void DeleteAllForUser(long userId)
    {
        WithUserLock(userId, () =>
        {
            try
            {
                var dir = UserDir(userId);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort cleanup */ }
            return true;
        });
    }

    /// <summary>
    /// Builds the <c>[ORGANIZATIONAL KNOWLEDGE]</c> block to prepend to the prompt
    /// for this turn, or an empty string when there is nothing to inject.
    ///
    /// Token-cost optimization (Tier 1): the full block is injected on the first
    /// turn of a session and re-injected only when the content changes or every
    /// <see cref="ReinjectEveryTurns"/> turns. On unchanged intervening turns this
    /// returns "" — the model still sees the knowledge via conversation history,
    /// which the provider caches cheaply. When the user has no active articles the
    /// method short-circuits with zero file I/O.
    /// </summary>
    public static string BuildContextBlock(long userId, string sessionId)
    {
        // Zero-cost fast path: no file at all → nothing to inject.
        if (!File.Exists(FilePath(userId)))
        {
            if (!string.IsNullOrEmpty(sessionId)) InjectionState.TryRemove(sessionId, out _);
            return "";
        }

        var active = ListForUser(userId).Where(a => a.Active).ToList();
        if (active.Count == 0)
        {
            if (!string.IsNullOrEmpty(sessionId)) InjectionState.TryRemove(sessionId, out _);
            return "";
        }

        var hash = ComputeHash(active);

        if (!string.IsNullOrEmpty(sessionId) && InjectionState.TryGetValue(sessionId, out var st)
            && st.Hash == hash && st.TurnsSince + 1 < ReinjectEveryTurns)
        {
            // Unchanged and recently injected — skip; history already carries it.
            InjectionState[sessionId] = (hash, st.TurnsSince + 1);
            return "";
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            InjectionState[sessionId] = (hash, 0);
            // Opportunistic guard against unbounded growth of ephemeral state.
            if (InjectionState.Count > 10_000) PruneInjectionState(sessionId);
        }

        // Tier-2 token optimization: when the user's active knowledge is small we
        // inline everything; once it crosses the budget we inject a compact index
        // and let the model lazy-pull full articles via the QueryKnowledge tool.
        var totalChars = active.Sum(a => a.Content.Length);
        return totalChars <= FullInjectionCharBudget
            ? FormatBlock(active)
            : FormatIndexBlock(active, totalChars);
    }

    private static string FormatIndexBlock(List<KnowledgeArticle> active, int totalChars)
    {
        var sb = new StringBuilder();
        sb.Append("[ORGANIZATIONAL KNOWLEDGE INDEX — the user has provided ");
        sb.Append(active.Count);
        sb.Append(" reference article(s) about their environment (");
        sb.Append(totalChars);
        sb.Append(" chars total, too large to inline). Treat these as ground truth. ");
        sb.Append("Call the QueryKnowledge tool to read the full text of any article you need: ");
        sb.Append("mode=\"get\" param=\"<id>\" for one article, mode=\"search\" param=\"<keywords>\" to find relevant ones, or mode=\"list\" for this index again. ");
        sb.Append("Pull an article whenever the user's question touches its topic.]");
        foreach (var a in active.OrderBy(a => a.Category, StringComparer.Ordinal).ThenBy(a => a.Title, StringComparer.Ordinal))
        {
            sb.Append("\n- ");
            sb.Append(a.Id);
            sb.Append(" · ");
            sb.Append(a.Title);
            sb.Append(" (");
            sb.Append(a.Category);
            sb.Append(", ");
            sb.Append(a.Content.Length);
            sb.Append(" chars)");
        }
        return sb.ToString();
    }

    private static string FormatBlock(List<KnowledgeArticle> active)
    {
        var sb = new StringBuilder();
        sb.Append("[ORGANIZATIONAL KNOWLEDGE — reference information the user has provided about their environment. Treat it as ground truth: resolve application/team names to subscriptions, apply their tagging and cost-center conventions, follow their analysis instructions and reporting preferences, and respect their SLA/SLO/RTO/RPO targets and fiscal-calendar definitions. This knowledge persists across sessions.]");
        foreach (var a in active.OrderBy(a => a.Category, StringComparer.Ordinal).ThenBy(a => a.Title, StringComparer.Ordinal))
        {
            sb.Append("\n\n### ");
            sb.Append(a.Title);
            sb.Append(" (");
            sb.Append(a.Category);
            sb.Append(")\n");
            sb.Append(a.Content);
        }
        return sb.ToString();
    }

    private static void PruneInjectionState(string keep)
    {
        InjectionState.Clear();
        // Keep the current session so we don't immediately re-inject for it.
    }

    private static string ComputeHash(List<KnowledgeArticle> active)
    {
        var sb = new StringBuilder();
        foreach (var a in active.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            sb.Append(a.Id);
            sb.Append(':');
            sb.Append(a.UpdatedUtc.Ticks);
            sb.Append('|');
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes, 0, 8);
    }

    // ---- validation helpers ----

    private static string NormalizeCategory(string? category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        if (!Categories.Contains(c))
            throw new KnowledgeValidationException($"Unknown category '{category}'. Allowed: {string.Join(", ", Categories)}.");
        return c;
    }

    private static void ValidateFields(string title, string content)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new KnowledgeValidationException("Title is required.");
        if (title.Length > MaxTitleChars)
            throw new KnowledgeValidationException($"Title exceeds {MaxTitleChars} characters.");
        if (string.IsNullOrWhiteSpace(content))
            throw new KnowledgeValidationException("Content is required.");
        if (content.Length > MaxArticleChars)
            throw new KnowledgeValidationException($"Content exceeds {MaxArticleChars} characters ({content.Length}).");
    }

    private static void EnsureTotalBudget(List<KnowledgeArticle> existing, int addedContentLength)
    {
        var current = existing.Sum(a => a.Content.Length);
        if (current + addedContentLength > MaxTotalChars)
            throw new KnowledgeValidationException($"Total knowledge would exceed {MaxTotalChars} characters. Trim or delete an article.");
    }

    private static string NewId(List<KnowledgeArticle> existing)
    {
        string id;
        do
        {
            id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        }
        while (existing.Any(a => a.Id == id));
        return id;
    }

    private static T WithUserLock<T>(long userId, Func<T> action)
    {
        var gate = UserLocks.GetOrAdd(userId, _ => new object());
        lock (gate)
        {
            return action();
        }
    }

    private static void Persist(long userId, List<KnowledgeArticle> articles)
    {
        Directory.CreateDirectory(UserDir(userId));
        var path = FilePath(userId);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(articles, JsonOpts);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
