# Microsoft's Copilot analytics dashboards and the agent

Research from 20 November 2026, kept for a later implementation. It records what Microsoft's own AI cost dashboards show and require, why the agent doesn't call their API, and which documented APIs would bring the same insights into the agent. Facts are a snapshot: check the linked Microsoft Learn pages before building on them.

## Summary

- The agent reads Copilot Credits from the documented [Microsoft 365 Copilot Cost Management API](https://learn.microsoft.com/microsoft-365/copilot/extensibility/api/admin-settings/cost-management/overview) in Microsoft Graph (beta). Keep it that way.
- Microsoft's Viva Insights dashboards (Copilot Dashboard, Agent Dashboard, Consumption Dashboard) and Agent 365 Business Analytics are portal experiences with licence minimums. Below 50 assigned Copilot licences, most of them show nothing.
- Those dashboards read the Insights portal's internal API (`api.analysis.insights.svc.cloud.microsoft`). It is undocumented, can change without notice, and only the portal's own sign-in can call it, so a customer's app registration can't get a token for it. The agent must not depend on it (the same conclusion as for the Microsoft 365 admin center's internal consumption API, see Authentication in [the Copilot instructions](../.github/copilot-instructions.md)).
- In a lab tenant billed in USD, the Consumption Dashboard valued 10,000 credits at USD 100, the same USD 0.01 per credit as the Retail Prices API's Pay As You Go Copilot Credit meter (`serviceName 'Microsoft Copilot Studio'`) that the agent uses for credits not billed yet.

## Licence minimums

| Experience | Requirement | Source |
|---|---|---|
| Copilot Dashboard (Viva Insights) | 1 assigned Copilot licence (with the Viva Insights service plan) or 50 Viva Insights licences start data processing, which takes up to seven days. With 1-49 Copilot licences it shows readiness, adoption and impact only: no agent insights, benchmarks, intelligent summaries, sentiment, trend lines or manager views | [Learn](https://learn.microsoft.com/viva/insights/org-team-insights/copilot-dashboard#feature-availability-based-on-licenses) |
| Copilot Dashboard export | 50 assigned Copilot or Viva Insights licences | [Learn](https://learn.microsoft.com/viva/insights/org-team-insights/export-copilot-metrics) |
| Agent Dashboard | 50 assigned Copilot licences and agent activity. Its Agent 365 view also needs Agent 365 licences | [Learn](https://learn.microsoft.com/viva/insights/org-team-insights/agent-dashboard) |
| Agent 365 Business Analytics (Frontier preview) | 50 assigned Copilot licences, spending policies, consumption insights turned on for the viewer's role, and billing turned on in the Microsoft 365 admin center | [Learn](https://learn.microsoft.com/a365-business-analytics/introduction#prerequisites) |
| Consumption Dashboard (Insights) | Learn lists only roles: Insights Analysts with global partition access, managers and Microsoft 365 Global Administrators. In a lab tenant with 2 assigned Copilot licences, the Insights service withheld its data for having fewer than 50 Copilot or Viva Insights licences, and the page said "Your organisation doesn't have consumption data yet" | [Learn](https://learn.microsoft.com/viva/insights/org-team-insights/ai-cost-dashboard) |
| Microsoft 365 admin center, Copilot > Cost Management | No licence minimum on Learn | [Learn](https://learn.microsoft.com/copilot/implement/usage-based-billing-copilot-credits-monitor-spending) |
| Copilot Cost Management API (Graph beta) | Admin consent for its `CopilotCostManagement` permissions. No licence minimum on Learn | [Learn](https://learn.microsoft.com/microsoft-365/copilot/extensibility/api/admin-settings/cost-management/overview) |

New Agent 365 purchases from 1 June 2026 need Microsoft 365 E5 (Business Premium for small and medium businesses, Defender and Purview at F5 level for frontline workers). Microsoft 365 E7 includes Agent 365 ([Partner Center announcement](https://learn.microsoft.com/partner-center/announcements/2026-june#new-licensing-prerequisite-for-agent-365)).

The Consumption Dashboard currently covers only Copilot Cowork and the Work IQ API, plus a preview GitHub Copilot page.

## What Microsoft's dashboards show (documented)

Features worth mirroring with data the agent can already read:

- **Consumption Dashboard** ([Learn](https://learn.microsoft.com/viva/insights/org-team-insights/ai-cost-dashboard)): active users, total credits and sessions; usage trends by group, organization or job function; credits by service; usage intensity by user segment (top 1%, top 5%, top 6-25% and top 26-50% of users, each with total credits, credits per user and per session, sessions per user and active users); spending policies nearing their limit (credits used per user, spending limit, users near or at their limit); Cowork usage by task category.
- **Admin center Cost Management** ([Learn](https://learn.microsoft.com/copilot/implement/usage-based-billing-copilot-credits-monitor-spending)): the Overview tab (refreshed every 4 hours) shows total credits, prepaid capacity pack credits, pay-as-you-go credits (including the Copilot Credit pre-purchase plan, P3), active users, top actions (credit requests, policies and users near their limit) and a spending trend of prepaid against pay-as-you-go use. The Consumption tab (refreshed every 2 hours) shows policies, users, groups, and agents and services. Its Cost column is an estimate in local currency that includes negotiated rates and prepaid use, so it might not match the bill. Exports are point-in-time snapshots.
- **Agent 365 Business Analytics** ([metrics](https://learn.microsoft.com/a365-business-analytics/reference/metrics)): average cost per user and per session, target or limit used, budget runway remaining, "approaching limit" (a user at 85% or more of their limit, or a policy projected to reach its limit), a base forecast (each user's average use over the last seven days, excluding weekends, projected forward) and a growth forecast that raises it when active users or cost per user grow. Its GitHub Copilot page covers overage cost against budget by team, cost center and repository.

Reconciliation facts from the same pages:

- When a running task takes a user past their per-user limit in a group policy, the task completes, but the excess isn't billed and doesn't appear as consumed credits in the Cost Management dashboards.
- When a user moves to another group's policy during a month, earlier credits stay billed at the policy level but no longer appear at the user level.
- In Azure Cost Management, P3 plans appear as a reservation benefit, and the amortized view spreads a P3 purchase over its term by consumption. Prepaid capacity packs never appear there ([Learn](https://learn.microsoft.com/copilot/implement/usage-based-billing-compare-dashboard-views)).

## Backlog (documented APIs only)

1. **Credit trend and spikes:** daily `serviceUsageRecords` and `userUsageRecords` per completed UTC day, within the service's throttling (reuse `CopilotReportCache`, keep to a seven-day window).
2. **Funding split:** `fundingUsageRecords?month=YYYY-MM` (prepaid, pay-as-you-go, unbilled overage, non-billable) beside Azure's billed Microsoft Copilot Studio cost, with P3 purchases and their amortized cost from Cost Management.
3. **Usage intensity:** the share of credits used by the top 1%, top 5%, top 6-25% and top 26-50% of users, computed from `userUsageRecords` (`$orderby=consumedQuantity desc`, `$top=100`, following `@odata.nextLink`).
4. **Limits watch:** users at 85% or more of their per-user limit (`userBalances/{id}/serviceBalances`) and policies projected to reach `periodLimit`, as a scheduled job template.
5. **Forecast:** Business Analytics' base forecast method for Copilot Credits (seven-weekday average per user, projected), shown beside Azure's forecast and called an estimate.
6. **Credit requests and blockers:** `creditRequests` together with `userBalances/{id}` (`isConsumptionAllowed` and its reasons) to explain who is blocked and why.
7. **Local currency and negotiated rates:** the admin center prices credits in local currency with negotiated rates, while the agent prices unbilled credits at the retail meter. Where Cost Management already holds billed Microsoft Copilot Studio rows, consider pricing from the tenant's own effective unit price, and keep calling retail-based figures estimates.
8. **Least privilege:** `CopilotCostManagement-Reports.Read.All` is the least-privileged permission for service and funding reports, but `CopilotCostManagement.Read.All` remains the only one for `getTenantCreditBalance` ([Learn](https://learn.microsoft.com/microsoft-365/copilot/extensibility/api/admin-settings/cost-management/usage-reports-overview#permissions)). Revisit the `copilotcost` tier's scopes if the tenant balance is ever dropped.
9. **Uploaded exports:** for tenants with 50 or more licences, test `QueryUploadedFile` with a [Consumption Dashboard export](https://learn.microsoft.com/viva/insights/org-team-insights/export-ai-cost-metrics).

## Demo talking points

- Microsoft's agent and consumption dashboards need 50 licensed users. The agent reads the Graph API directly, so it shows Copilot Credits for small tenants too.
- The agent prices credits at the same USD 0.01 the Consumption Dashboard used.
- Agent 365 Business Analytics (Frontier) is Microsoft's direction for AI cost, budgets and forecasts across Copilot, agents and GitHub. The agent complements it with Azure and Microsoft Foundry cost, uses only documented APIs, and is open source.
