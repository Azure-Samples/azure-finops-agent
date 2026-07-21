# Permissions required to run `azd up`

To deploy this repo with `azd up`, the signed-in identity needs permissions on three planes: the Azure subscription, the Microsoft Entra tenant, and (implicitly) the resource providers used by the Bicep templates.

## 1. Azure subscription

The Bicep in [infra/main.bicep](../infra/main.bicep) targets **subscription scope** (it creates the resource group) and assigns RBAC roles to the Web App's managed identity, so the deployer needs both resource-management rights and role-assignment rights.

**Recommended:**

- **Owner** on the target subscription — satisfies everything below in one role.

**Minimum (least privilege):**

- **Contributor** on the subscription — to create the RG and all resources (ACR, App Service Plan + Web App, Azure OpenAI / Cognitive Services account + model deployment, Log Analytics workspace, Application Insights), **plus**
- **User Access Administrator** (or **Role Based Access Control Administrator**) on the subscription — required because [infra/modules/roles.bicep](../infra/modules/roles.bicep) and [infra/modules/roles-aoai.bicep](../infra/modules/roles-aoai.bicep) create role assignments (`AcrPull` on ACR and `Cognitive Services User` on the AOAI account) for the Web App's system-assigned managed identity.

You also need available **quota** for:

- Azure OpenAI in `aoaiLocation` (default `swedencentral`, 30K TPM for `gpt-5.6-sol`).
- The chosen App Service Plan SKU in `location` (default `B1`).

## 2. Microsoft Entra ID (tenant)

The preprovision hook ([infra/scripts/preprovision.ps1](../infra/scripts/preprovision.ps1)) calls [src/Dashboard/setup-entra-app.ps1](../src/Dashboard/setup-entra-app.ps1) to create a multi-tenant app registration and client secret. That requires one of:

- The **Application Administrator** or **Cloud Application Administrator** directory role, **or**
- The tenant setting **"Users can register applications" = Yes** (default in many tenants), in which case any user can register apps.

If your tenant blocks app registration and you don't have those roles, pre-create the app yourself and seed the values before running `azd up`:

```powershell
azd env set AZURE_ENTRA_APP_ID '<your-app-id>'
azd env set AZURE_ENTRA_CLIENT_SECRET '<your-app-secret>'
```

The preprovision hook detects these values and skips Entra creation.

## 3. Resource providers

The first `azd up` in a fresh subscription will register the following providers (Contributor is sufficient):

- `Microsoft.ContainerRegistry`
- `Microsoft.Web`
- `Microsoft.CognitiveServices`
- `Microsoft.OperationalInsights`
- `Microsoft.Insights`
- `Microsoft.ManagedIdentity`
- `Microsoft.Authorization`

## TL;DR

Easiest working combination: **Owner on the subscription** + ability to **create app registrations in the tenant**. Everything else (image build via `az acr build` in the postdeploy hook, redirect-URI patching, role assignments) inherits from those.
