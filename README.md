# Setpoint

Setpoint, repository governance as code for GitHub and Azure DevOps.

Declare pull-request rules once in YAML, in a central policy repo, and Setpoint brings every repository on GitHub, Azure DevOps Services and Azure DevOps Server 2022 in line with them, through `validate`, `plan` and `apply`.

> **Status:** early development. `validate` works; `plan` and `apply` don't do anything yet. Progress is tracked in [the MVP build issue](https://github.com/Quattordici/Setpoint/issues/24).

## Commands

| Command | Does |
|---|---|
| `setpoint validate` | Offline checks of the policy repo against the schema and merge rules |
| `setpoint plan [--out plan.json] [--json] [--exit-code]` | Reads live state and shows the changes apply would make |
| `setpoint apply plan.json` | Applies a saved plan, after checking nothing moved since it was made |
| `setpoint apply --yes` | Re-plans and applies in one step |

## Diagnostics

Every problem has a stable `SPxxxx` code, listed in [docs/diagnostics.md](docs/diagnostics.md). With `--output plain` (the default when output isn't a terminal) each one is a single `file:line:col: severity code: message` line.

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
