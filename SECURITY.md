# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through GitHub instead: on the repository's **Security** tab, choose **Report a vulnerability** ([direct link](https://github.com/hurshah7862/RagDotNet/security/advisories/new)). Only the maintainer can see the report.

Include what you can of:

- the package and version (for example `RagDotNet.Core 1.0.0-preview.1`);
- what an attacker can do, and what they need (for example, a tenant reading another tenant's documents);
- steps or a small program that shows it.

You can expect a first reply within a week. Once a fix is released, the advisory is published with credit to you, unless you would rather stay anonymous.

## Supported versions

RagDotNet is in preview. Fixes go into the latest release only; there are no backports to earlier previews.

## Scope

In scope: the code in this repository and the `RagDotNet.*` packages on nuget.org, including access scopes and tenant partitions leaking documents across tenants, unsafe handling of untrusted documents by the readers, and anything in the release pipeline.

Out of scope: vulnerabilities in the models, vector stores or other third-party packages you connect to RagDotNet (report those to their maintainers), and prompt injection through documents you ingest, which RagDotNet does not claim to prevent.
