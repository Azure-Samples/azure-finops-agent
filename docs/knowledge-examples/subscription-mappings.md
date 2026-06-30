# Example: Subscription mappings

> **Category:** Subscription mappings
> Copy this into a new Knowledge Base article and replace the placeholder values
> with your own. This tells the agent how your app/team/environment names map to
> Azure subscriptions and resource groups, so you can ask "what did Payments
> cost last month?" instead of pasting subscription GUIDs every time.

## Subscriptions

| Name | Environment | Subscription ID |
| --- | --- | --- |
| Core Platform — Prod | Production | 1111aaaa-2222-3333-4444-555555555555 |
| Core Platform — Non-prod | Dev/Test | 6666bbbb-7777-8888-9999-000000000000 |
| Data & Analytics | Production | aaaa1111-bbbb-2222-cccc-333333333333 |
| Sandbox | Sandbox | dddd4444-eeee-5555-ffff-666666666666 |

## Applications → subscription / resource groups

| Application | Subscription | Resource group(s) |
| --- | --- | --- |
| Payments API | Core Platform — Prod | `rg-pay-prod`, `rg-pay-prod-data` |
| Payments API | Core Platform — Non-prod | `rg-pay-staging`, `rg-pay-dev` |
| Marketing site | Core Platform — Prod | `rg-mkt-prod` |
| Reporting | Data & Analytics | `rg-rpt-prod`, `rg-synapse-prod` |

## Notes

- "Prod" always means the Production environment unless I say otherwise.
- Resource groups are prefixed `rg-<app>-<env>`.
- The Sandbox subscription is for experiments only — exclude it from
  cost-trend and chargeback reports unless I ask.
