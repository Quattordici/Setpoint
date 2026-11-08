# Plan files

`setpoint plan --out plan.json` saves a plan, and `setpoint apply plan.json` applies it. `setpoint plan --json` prints the same document to standard output. The schema is [`schema/v1/plan.json`](../schema/v1/plan.json), generated from the C# model; run the tests with `SETPOINT_UPDATE_SCHEMA=1` to regenerate it.

A plan never holds a secret. Targets record their sign-in method (`auth`) and identity, never a token.

## Top level

| Field | Meaning |
|---|---|
| `$schema`, `formatVersion` | The format. This Setpoint reads and writes version 1. |
| `setpointVersion` | The Setpoint that made the plan. `apply` refuses a plan from another version. |
| `policy.hash` | SHA-256 over every policy file's path and text, with line endings normalised. `apply` refuses the plan if the policy changed. |
| `policy.commit` | The policy repo commit, read from `.git`, when there is one. |
| `options` | `strict`, `skipUnreachable` and `allowUnsupportedServer` as the plan ran. |
| `targets[]` | Each target reached: what the preflight detected, the API version, auth method, identity and repo count. |
| `repos[]` | Every repo in scope, including in-sync, skipped and failed ones. |
| `diagnostics[]` | Run-level diagnostics, such as an unreachable target. |
| `summary` | Totals. |

## Repos

Each repo has a `status` (`changes`, `insync`, `skipped`, `error`), the `layers` that apply to it, and:

- `objects[]`: the native objects `apply` writes, such as an Azure DevOps policy configuration or the `setpoint` ruleset. Each has its `key`, its `operation` (`create`, `update`, `delete`), its `matchKey`, the full `payload` apply sends as-is, and its `fingerprint`.
- `changes[]`: the neutral view for people and reports, with `rule`, `action`, `from`, `to`, `layer`, `locked` and the `object` it belongs to. `apply` never reads it.
- `unmanaged[]` and `unmanagedCount`: the live settings Setpoint leaves alone.
- `diagnostics[]`: warnings and notes about the repo.

## Fingerprints

A fingerprint is SHA-256 over the live object's canonical JSON (keys sorted), taken at plan time. A create fingerprints "nothing matches" its match key instead, so an object made by hand before `apply` makes the plan stale. `apply` reads each object again and refuses the whole run before its first write if any fingerprint, the policy hash or the Setpoint version no longer matches (exit 3).
