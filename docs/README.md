# Setpoint documentation

Setpoint keeps pull-request rules, repository access and deployment approvals the same across Azure DevOps Server, Azure DevOps Services and GitHub, from one policy repo.

## Using Setpoint

| Page | Read it when |
|---|---|
| [Getting started](getting-started.md) | You are setting up a first policy repo and running `validate`, `plan` and `apply` |
| [Permissions](permissions.md) | You are creating the tokens, service accounts or apps Setpoint signs in with |
| [Pipelines](pipelines.md) | You are running Setpoint from Azure Pipelines or GitHub Actions: plan read-only, apply behind an approval |
| [Drift](drift.md) | You want a scheduled check that live settings still match the policy |
| [Diagnostics and exit codes](diagnostics.md) | A run printed an `SPxxxx` code or exited non-zero |

## Platforms

| Page | Covers |
|---|---|
| [Azure DevOps](azure-devops.md) | Server 2022 and later and Services: branch policies, repository ACLs, environment Approval checks, sign-in |
| [GitHub](github.md) | The `setpoint` ruleset, team access, environment protection, sign-in |

## Reference

| Page | Covers |
|---|---|
| [Plan files](plan-file.md) | The `plan.json` format that `plan --out`, `plan --json` and `drift --report` write |
| [Policy schema](../schema/v1/) | The JSON Schemas for policy files, for editors; frozen for 1.x |
| [Changelog](../CHANGELOG.md) | What changed in each release |

## Contributing

| Page | Covers |
|---|---|
| [Live gates](live-gates.md) | The test environments, the gate script and recording fixtures |
| [Releasing](releasing.md) | How a version is numbered, tagged and published to nuget.org |
