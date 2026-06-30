# Knowledge Authoring Guide

> How to write organizational knowledge so the Azure FinOps Agent understands
> your environment and applies your conventions automatically.

The **Knowledge Base** lets you tell the agent about your environment **once**.
After that, every conversation — now and in the future — starts with that
context already loaded. No more re-explaining which subscription is "Production"
or who owns the `mkt-*` resource groups.

> **Where to find it:** connect Azure, then open the **Knowledge Base** section
> in the left sidebar. Click **+ Add knowledge** to write an article, or
> **↑ Import file** to upload a `.csv` / `.txt` / `.json` / `.md`.

> **Note:** the Knowledge Base requires signing in with Microsoft Entra ID
> (it's tied to your account so your notes follow you). It isn't available for
> anonymous chat sessions.

---

## The six categories

Pick the category that best fits each article — it helps the agent reason about
what the content is for.

| Category | Use it for | Example title |
| --- | --- | --- |
| **Subscription mappings** | Mapping app/team/environment names to subscription IDs and resource groups | "Production subscription map" |
| **Cost centers** | Who owns what; chargeback/showback allocation rules | "Cost center owners" |
| **Analysis instructions** | How you want the agent to analyze and report | "Reporting preferences" |
| **Architecture** | How your workloads are built; key dependencies | "Payments platform architecture" |
| **SLA / RTO / RPO** | Availability, recovery-time, and recovery-point targets | "Tier-1 app SLAs" |
| **Custom** | Anything else — fiscal calendar, naming conventions, tagging policy | "Fiscal calendar FY26" |

See [`knowledge-examples/`](knowledge-examples/) for a ready-to-copy article in
each category.

---

## What makes a good article

**Be concrete and structured.** The agent reads plain text and markdown well.
Tables, short lists, and `key: value` lines work best.

✅ Good:

```markdown
| App | Subscription | Subscription ID |
| --- | --- | --- |
| Payments | Production | 1111aaaa-... |
| Payments | Staging | 2222bbbb-... |
| Marketing site | Production | 1111aaaa-... |
```

❌ Avoid vague prose:

> "Our production stuff is mostly in the main subscription but some marketing
> things are elsewhere and staging is separate."

**One topic per article.** Keep "subscription mappings" and "cost-center owners"
in separate articles. It's easier to maintain and lets you toggle or update one
without touching the other.

**Write instructions as instructions.** For the _Analysis instructions_
category, address the agent directly:

```markdown
- Always show costs in EUR.
- Group spend by the `cost-center` tag, then by service.
- Treat any month-over-month increase above 15% as an anomaly worth flagging.
- When recommending savings, prefer reservations only for steady-state VMs.
```

---

## Keep it token-efficient

Everything in your knowledge base may be sent to the model as context, which
costs tokens. A few habits keep that cheap **and** make answers sharper:

- **Trim the fat.** Delete boilerplate, long IDs you don't need, and duplicated
  rows. Aim for the minimum that's unambiguous.
- **Prefer tables over paragraphs.** They're denser and the model parses them
  reliably.
- **Disable instead of delete** when something is temporarily irrelevant — use
  the **On/Off** toggle so it stops being injected but you keep the text.
- **Don't paste huge exports.** If you import a big CSV, prune it to the rows
  that matter. The per-article limit is 10,000 characters; the agent works best
  well under that.

> Under the hood the agent injects your knowledge **once per conversation** and
> only re-sends it when you change it — and if your knowledge base gets large,
> it switches to an index and pulls only the relevant articles on demand. You
> don't have to do anything for this; writing concise, single-topic articles
> just makes it even more efficient.

---

## Limits

| Limit | Value |
| --- | --- |
| Articles per user | 20 |
| Characters per article | 10,000 |
| Total characters | 50,000 |
| Title length | 120 |

If you hit a limit, consolidate or delete an article. Hitting 50K total is a
sign the knowledge base is doing too much — keep it to the high-value context
the agent can't infer on its own.

---

## Examples

The [`knowledge-examples/`](knowledge-examples/) folder has a starter article
for each category. Copy one, replace the placeholder values with your own, and
paste it into a new article (or import the file directly):

- [subscription-mappings.md](knowledge-examples/subscription-mappings.md)
- [cost-centers.md](knowledge-examples/cost-centers.md)
- [analysis-instructions.md](knowledge-examples/analysis-instructions.md)
- [architecture.md](knowledge-examples/architecture.md)
- [sla-rto-rpo.md](knowledge-examples/sla-rto-rpo.md)
- [fiscal-calendar.md](knowledge-examples/fiscal-calendar.md)

---

## Privacy

Your knowledge is stored privately under your account, encrypted at rest, and is
never shared with other users or tenants. It persists across sessions (that's
the point) and is **not** deleted when you log out. You can delete any article
at any time from the sidebar. For the full security model, see
[knowledge-base.md](knowledge-base.md#6-security--privacy).
