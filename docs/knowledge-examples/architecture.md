# Example: Architecture

> **Category:** Architecture
> Copy this into a new Knowledge Base article and replace with your own
> workloads. This helps the agent reason about dependencies and cost drivers
> when you ask about a specific application.

## Payments platform

- **Tier:** Tier-1 (business critical).
- **Compute:** AKS cluster `aks-pay-prod` (3 system + 6 user nodes,
  `Standard_D8s_v5`), plus 2 VM scale sets for batch settlement.
- **Data:** Azure SQL (Business Critical, 2 vCore Hyperscale), Cosmos DB
  (serverless) for the ledger, Redis (Premium P1) for session cache.
- **Messaging:** Service Bus Premium (1 MU).
- **Networking:** App Gateway WAF v2, ExpressRoute via the hub subscription.
- **Region:** Primary `westeurope`, DR in `northeurope`.

## Key cost drivers (so you know where to look)

- AKS user node pool and the Hyperscale SQL are the two biggest line items.
- The settlement VMSS only needs to run **18:00–02:00** — flag it if it's
  running 24/7.
- Cosmos serverless cost scales with request units; spikes usually mean a
  noisy upstream caller.

## Dependencies

- Payments depends on the shared **Identity** service (separate subscription)
  and the central **Log Analytics** workspace `law-central-prod`.
