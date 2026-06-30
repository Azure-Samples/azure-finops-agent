using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Services;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Read-only access to the signed-in user's persistent organizational knowledge
/// articles (subscription mappings, cost-center owners, SLAs, analysis rules,
/// etc.). When a user's knowledge is small it is inlined into the prompt and the
/// model rarely needs this tool; once it grows past the injection budget only a
/// compact index is injected and the model uses <c>QueryKnowledge</c> to pull the
/// full text of the articles relevant to the current question.
/// </summary>
public sealed class KnowledgeTools
{
    private readonly long _userId;

    public KnowledgeTools(long userId) => _userId = userId;

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(QueryKnowledge, "QueryKnowledge", @"Reads the user's persistent ORGANIZATIONAL KNOWLEDGE (their own notes about their Azure environment: subscription/app mappings, cost-center owners, tagging conventions, SLA/RTO/RPO targets, fiscal calendar, analysis instructions). Read-only.
Use it whenever the prompt shows an [ORGANIZATIONAL KNOWLEDGE INDEX] and the user's question relates to one of the listed articles — pull the full text before answering.
modes:
- list    : returns the index of all active articles (id, title, category, size). param ignored.
- search  : param = keywords; returns full text of articles whose title or body matches.
- get     : param = an 8-char article id from the index; returns that article's full text.");
    }

    private string QueryKnowledge(
        [Description("One of: list, search, get.")] string mode,
        [Description("For 'search': keywords. For 'get': the 8-char article id. Ignored for 'list'.")] string? param = null)
    {
        var active = KnowledgeStore.ListForUser(_userId).Where(a => a.Active).ToList();
        if (active.Count == 0)
            return "No organizational knowledge has been saved by this user.";

        switch ((mode ?? "").Trim().ToLowerInvariant())
        {
            case "get":
            {
                var id = (param ?? "").Trim();
                var a = active.FirstOrDefault(x => x.Id == id);
                if (a is null)
                    return $"No active article with id '{id}'. Use mode=list to see valid ids.";
                return $"### {a.Title} ({a.Category}) [id {a.Id}]\n{a.Content}";
            }

            case "search":
            {
                var q = (param ?? "").Trim();
                if (q.Length == 0) return "Provide search keywords in 'param'.";
                var terms = q.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var matches = active.Where(a =>
                        terms.Any(t =>
                            a.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                            a.Content.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                            a.Category.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(a => a.Category, StringComparer.Ordinal)
                    .ThenBy(a => a.Title, StringComparer.Ordinal)
                    .ToList();
                if (matches.Count == 0)
                    return $"No articles matched '{q}'. Use mode=list to see all articles.";
                var sb = new StringBuilder();
                foreach (var a in matches)
                {
                    sb.Append("### ");
                    sb.Append(a.Title);
                    sb.Append(" (");
                    sb.Append(a.Category);
                    sb.Append(") [id ");
                    sb.Append(a.Id);
                    sb.Append("]\n");
                    sb.Append(a.Content);
                    sb.Append("\n\n");
                }
                return sb.ToString().TrimEnd();
            }

            case "list":
            default:
            {
                var sb = new StringBuilder();
                sb.Append("Active organizational knowledge articles:\n");
                foreach (var a in active
                    .OrderBy(a => a.Category, StringComparer.Ordinal)
                    .ThenBy(a => a.Title, StringComparer.Ordinal))
                {
                    sb.Append("- ");
                    sb.Append(a.Id);
                    sb.Append(" · ");
                    sb.Append(a.Title);
                    sb.Append(" (");
                    sb.Append(a.Category);
                    sb.Append(", ");
                    sb.Append(a.Content.Length);
                    sb.Append(" chars)\n");
                }
                sb.Append("Use mode=get param=<id> to read one, or mode=search param=<keywords>.");
                return sb.ToString();
            }
        }
    }
}
