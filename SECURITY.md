<!-- BEGIN MICROSOFT SECURITY.MD V0.0.9 BLOCK -->

## Security

Microsoft takes the security of our software products and services seriously, which includes all source code repositories managed through our GitHub organizations, which include [Microsoft](https://github.com/Microsoft), [Azure](https://github.com/Azure), [DotNet](https://github.com/dotnet), [AspNet](https://github.com/aspnet) and [Xamarin](https://github.com/xamarin).

If you believe you have found a security vulnerability in any Microsoft-owned repository that meets [Microsoft's definition of a security vulnerability](https://aka.ms/security.md/definition), please report it to us as described below.

## Reporting Security Issues

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please report them to the Microsoft Security Response Center (MSRC) at [https://msrc.microsoft.com/create-report](https://aka.ms/security.md/msrc/create-report).

If you prefer to submit without logging in, send email to [secure@microsoft.com](mailto:secure@microsoft.com). If possible, encrypt your message with our PGP key; please download it from the [Microsoft Security Response Center PGP Key page](https://aka.ms/security.md/msrc/pgp).

You should receive a response within 24 hours. If for some reason you do not, please follow up via email to ensure we received your original message. Additional information can be found at [microsoft.com/msrc](https://aka.ms/security.md/msrc).

Please include the requested information listed below (as much as you can provide) to help us better understand the nature and scope of the possible issue:

- Type of issue (e.g. buffer overflow, SQL injection, cross-site scripting, etc.)
- Full paths of source file(s) related to the manifestation of the issue
- The location of the affected source code (tag/branch/commit or direct URL)
- Any special configuration required to reproduce the issue
- Step-by-step instructions to reproduce the issue
- Proof-of-concept or exploit code (if possible)
- Impact of the issue, including how an attacker might exploit the issue

This information will help us triage your report more quickly.

## Preferred Languages

We prefer all communications to be in English.

## Policy

Microsoft follows the principle of [Coordinated Vulnerability Disclosure](https://aka.ms/security.md/cvd).

<!-- END MICROSOFT SECURITY.MD BLOCK -->

## Application Boundaries

- The agent runtime exposes registered host tools only; native shell, MCP and cross-session memory tools are disabled on both creation and resume. The application container runs as a non-root user.
- ARM writes require a stored, owner/session-bound proposal and explicit acknowledgement in the application. Azure resource deletion and mutating action POSTs remain blocked. Standard Graph consent tiers are read-only.
- Downloads, transcripts, uploads, operation records and outcomes require the caller's application identity. Generated HTML is sandboxed without same-origin access, including direct downloads viewed inline.
- Recognizable credentials are rejected or redacted at chat, job, tool and telemetry boundaries. This is not a guarantee against arbitrary unlabelled secrets or secrets in image pixels. Do not upload credentials. Existing retained transcripts/uploads are not automatically scrubbed; rotate any disclosed credential and apply the deployment's retention policy.
- Host, CLI collector and browser telemetry are separate pipelines. CLI prompt/tool content capture is disabled; browser diagnostic properties and URLs are redacted. Protect the persistent volume and telemetry stores as customer data.
- The current coordination model supports one active app instance. Files on a shared volume do not provide distributed turn gates, approval locking or cooldown coordination.

See [reliability contracts](docs/agent-reliability.md) for retention periods, verification scope and operational limitations.
