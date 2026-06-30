# Example: Cost center owners

> **Category:** Cost centers
> Copy this into a new Knowledge Base article and replace the placeholders. This
> tells the agent who owns which costs and how to allocate spend for
> chargeback/showback.

## Cost centers

| Cost center | Owner | Department | Email |
| --- | --- | --- | --- |
| CC-1001 | Jordan Lee | Payments Engineering | jordan.lee@example.com |
| CC-1002 | Priya Nair | Marketing | priya.nair@example.com |
| CC-1003 | Sam Okafor | Data & Analytics | sam.okafor@example.com |
| CC-9000 | Platform Team | Shared Services | platform@example.com |

## Allocation rules

- Allocate spend by the **`cost-center`** resource tag.
- Resources tagged with an **`app`** but no `cost-center`: map via the app's
  owning team (see the subscription-mappings article), then to that team's cost
  center.
- **Untagged** resources: assign to **CC-9000 (Shared Services)** and flag them
  in the report so we can chase down the right owner.
- Shared infrastructure (networking, log analytics, backups): split across
  CC-1001/1002/1003 evenly unless I specify a different split.

## Reporting

- Always produce a per-cost-center breakdown when I ask about chargeback.
- Show the untagged/unallocated amount as its own line — never hide it.
