# Setpoint

Setpoint, repository governance as code for GitHub and Azure DevOps.

Declare pull-request rules once in YAML, in a central policy repo, and Setpoint brings every repository on GitHub, Azure DevOps Services and Azure DevOps Server 2022 in line with them, through `validate`, `plan` and `apply`.

> **Status:** early development. `validate`, `plan` and `apply` work against GitHub; Azure DevOps can be planned, and writing to it lands next. Progress is tracked in [the MVP build issue](https://github.com/Quattordici/Setpoint/issues/24).

## Documentation

| Page | For |
|---|---|
| [Getting started](docs/getting-started.md) | A first policy repo, `validate`, `plan` and `apply` |
| [Permissions](docs/permissions.md) | The token scopes and the roles or ACLs each command needs, per platform and sign-in method |
| [Pipelines](docs/pipelines.md) | Two-stage Azure Pipelines and GitHub Actions examples: plan read-only, apply behind an approval |
| [Diagnostics and exit codes](docs/diagnostics.md) | Every `SPxxxx` code and exit code, and what to do about it |
| [GitHub](docs/github.md), [Azure DevOps](docs/azure-devops.md) | How each platform is mapped, listed and written |
| [Plan files](docs/plan-file.md) | The `plan.json` format |
| [Live gates](docs/live-gates.md) | For contributors: the test environments, the gate script and recording fixtures |

## Commands

| Command | Does |
|---|---|
| `setpoint validate` | Offline checks of the policy repo against the schema and merge rules |
| `setpoint plan [--out plan.json] [--json] [--exit-code]` | Reads live state and shows the changes apply would make |
| `setpoint drift [--report drift.json] [--json]` | Checks that live state still matches the policy; exits 5 when it doesn't |
| `setpoint apply plan.json` | Applies a saved plan, after checking nothing moved since it was made |
| `setpoint apply --yes` | Re-plans and applies in one step |

## Applying safely

`apply` checks everything before its first write, and writes nothing if any check fails:

1. The plan was made by this Setpoint version from the policy files as they are now (otherwise exit 3, `SP4001`).
2. It changes no more repos than `--max-changes` allows, 25 by default; `--max-changes 0` removes the limit (otherwise exit 4, `SP4002`).
3. With `--strict`, the plan has no warnings.
4. Every target is reachable. `--skip-unreachable` leaves an unreachable target's repos out with a warning.
5. Every planned object is still exactly as planned: its fingerprint matches, it still exists, and nothing has appeared where the plan creates something (otherwise exit 3, `SP4001`). Plan again.

Then each repo is written in plan order. A failed write stops that repo and the others carry on, so apply exits 1 with `SP1003`; `--fail-fast` stops the whole run at the first failure. apply prints one line per repo and a summary line:

```text
applied        github/acme/payments-api  2 of 2 written
failed         github/acme/web  0 of 1 written; the rest not attempted
apply: repos applied=1 failed=1 not_attempted=0 skipped=0 objects_written=2
```

The plan file format is described in [docs/plan-file.md](docs/plan-file.md).

## Checking for drift

`setpoint drift` plans exactly like `plan` and never writes. It is meant for a scheduled pipeline:

- It exits 0 when every repo in scope matches the policy, and 5 when any repo differs.
- It exits 1 when a target can't be reached or a repo can't be read, because what wasn't read could be hiding drift. `--skip-unreachable` leaves an unreachable target out with a warning instead.
- `--report drift.json` saves the report, and `--json` prints it. The report is a plan file, so after review `setpoint apply drift.json` puts the drifted settings back, with the same checks as any apply. A report from a run with errors is incomplete: plan again before applying.

Setpoint keeps no state, so drift can't tell a setting someone changed by hand from a policy change that hasn't been applied yet. Run it against the policy as it is on `main`, where every merged change is expected to be applied.

## Diagnostics

Every problem has a stable `SPxxxx` code, listed with every exit code in [docs/diagnostics.md](docs/diagnostics.md). With `--output plain` (the default when output isn't a terminal) each one is a single `file:line:col: severity code: message` line.

## Editor support

JSON Schemas for policy files are published under [`schema/v1/`](schema/v1/). Add a modeline as the first line of a policy file and VS Code (with the YAML extension) or Rider will check it as you type:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/Quattordici/Setpoint/main/schema/v1/layer.json
version: 1
layer: baseline
```

Use `targets.json` for `targets.yml` and `layer.json` for every other policy file. The schemas are generated from the C# model; if you change the model, regenerate them with `SETPOINT_UPDATE_SCHEMA=1 dotnet test --solution Setpoint.slnx`.

## Building

You need the .NET 10 SDK.

```sh
dotnet build Setpoint.slnx
dotnet test --solution Setpoint.slnx
```

To try the tool from a local package:

```sh
dotnet pack src/Setpoint.Cli -c Release -o artifacts/packages
dotnet tool install Setpoint --tool-path artifacts/tool --add-source artifacts/packages --prerelease
artifacts/tool/setpoint --help
```

## Layout

| Project | Role |
|---|---|
| `src/Setpoint.Core` | The neutral model and all the thinking: policy loading and merge, diff, plan, fingerprints |
| `src/Setpoint.Adapters.GitHub` | Translates between the neutral model and the GitHub rulesets API |
| `src/Setpoint.Adapters.AzureDevOps` | Translates between the neutral model and the Azure DevOps policy and security APIs |
| `src/Setpoint.Cli` | The `setpoint` dotnet tool (NuGet id `Setpoint`): command parsing and terminal output |
| `tests/*` | Unit tests per project, plus architecture tests that keep the core/adapter split honest |

## Licence

[MIT](LICENSE)
