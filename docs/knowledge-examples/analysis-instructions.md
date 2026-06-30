# Example: Analysis instructions

> **Category:** Analysis instructions
> Copy this into a new Knowledge Base article and adjust to your preferences.
> These are standing instructions the agent follows whenever it analyzes cost or
> makes recommendations.

## Currency & formatting

- Show all costs in **EUR** (€), rounded to whole euros for summaries.
- Use thousands separators (e.g. €1,250,000).
- When comparing periods, always show both the absolute change and the percent
  change.

## How to group & analyze

- Default grouping: by **`cost-center`** tag, then by service.
- Always exclude the **Sandbox** subscription from trend and chargeback reports.
- Treat any month-over-month increase **above 15%** on a service as an anomaly
  worth flagging, with the likely driver.

## Recommendation preferences

- Recommend **reservations** only for steady-state, always-on workloads (VMs,
  SQL) with predictable usage; otherwise prefer **savings plans** or
  autoscaling.
- For idle/orphaned resources, generate a **review script** rather than assuming
  deletion — we approve cleanups manually.
- Prioritize recommendations by **annualized savings**, highest first, and note
  the effort/risk for each.

## Reporting style

- Lead with the headline number and the top 3 actions.
- Keep executive summaries to a few sentences; put detail in tables below.
