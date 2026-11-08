# PROTOTYPE: plan output (ticket #22)

Throwaway. This answers "what does `plan` look like?" for the sample estate in `../PROTOTYPE-policy-schema`, with invented live state. It contains hard-coded data and no real logic.

```
dotnet run docs/prototypes/PROTOTYPE-plan-output/plan-output.cs -- a   # tree (default)
dotnet run docs/prototypes/PROTOTYPE-plan-output/plan-output.cs -- b   # table per repo
dotnet run docs/prototypes/PROTOTYPE-plan-output/plan-output.cs -- c   # plain, one line per change
```

Add `--verbose` to list in-sync repos, or `--html` to export the rendering. Every run also writes `plan.json` to the current directory.

## What the sample shows

| Repo | Shows |
|---|---|
| ado-onprem/Payments/payments-api | Two neutral changes landing on one native policy (#17), a create, and an update. Values come from two layers, and one value is locked. |
| ado-onprem/Payments/legacy-ledger | A removal (`enabled: false` in the repo override) |
| ado-onprem/Platform/build-tools | In sync, plus an ambiguous-match warning (SP2201) |
| ado-cloud/Web/storefront | In sync, so it's hidden unless `--verbose` is set |
| github/quattordici/docs-site | Five neutral rules creating one `setpoint` ruleset, plus a stricter unmanaged ruleset (SP2101) |
| github/quattordici/payments-gateway | A private repo on GitHub Free, skipped (SP3001) |

Screenshots: `plan-a.png`, `plan-b.png`, `plan-c.png`.

## Plan file shape (`plan.json`)

- The top level holds `$schema`, `formatVersion`, `setpointVersion`, the policy `hash` and `commit`, the options the plan was made with (`strict`, `skipUnreachable`, `maxChanges`), and the detected targets.
- Each entry in `repos[]` has a `status` (`changes` | `insync` | `skipped`), the layers that applied, an `unmanagedCount` and its `warnings`.
- `objects[]` lists the **native objects** apply will write: an ADO policy configuration or the GitHub `setpoint` ruleset. Each object has its `operation`, its fingerprint at plan time, and the full desired `payload` that apply sends. A create records the fingerprint of "nothing matches", so a hand-made policy appearing between plan and apply makes the plan stale.
- `changes[]` lists the **neutral** view (rule, from, to, layer, locked) and points at its object. It is for humans and reports, and apply never reads it.
- Secrets are never included.
