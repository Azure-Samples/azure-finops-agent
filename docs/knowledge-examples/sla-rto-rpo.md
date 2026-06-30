# Example: SLA / RTO / RPO

> **Category:** SLA / RTO / RPO
> Copy this into a new Knowledge Base article and replace with your own targets.
> The agent references these when assessing risk, right-sizing, or recommending
> changes (e.g. it won't suggest removing redundancy that your RTO requires).

## Service tiers & targets

| Application | Tier | Availability SLA | RTO | RPO |
| --- | --- | --- | --- | --- |
| Payments API | Tier-1 | 99.95% | 15 min | 1 min |
| Identity | Tier-1 | 99.95% | 15 min | 5 min |
| Reporting | Tier-2 | 99.9% | 4 hours | 1 hour |
| Marketing site | Tier-3 | 99.5% | 24 hours | 24 hours |
| Internal tools | Tier-3 | best effort | 48 hours | 24 hours |

## What the tiers imply

- **Tier-1:** active/active or active/passive across regions; do **not**
  recommend removing geo-redundancy, zone redundancy, or DR replicas to save
  cost.
- **Tier-2:** single region with zone redundancy is acceptable; backups must
  meet the 1-hour RPO.
- **Tier-3:** cost optimization takes priority over redundancy; auto-shutdown
  and lower SKUs are fine.

## Use when

- Right-sizing: respect the tier — never trade below the SLA.
- Reliability findings: rank gaps by tier (Tier-1 first).
