# Diagnostics

Every problem Setpoint reports has a stable `SPxxxx` code. A code never changes meaning once released, and retired codes are not reused. `--strict` turns every warning into an error.

| Range | Area |
|---|---|
| 01xx | Policy files, found offline by `validate` |
| 02xx | Selection: scope and selectors against live repos |
| 10xx | Reaching targets and reading repos |
| 21xx | Ownership of managed settings |
| 22xx | Matching live objects |
| 30xx | Platform capabilities |

| Code | Default | Title | Meaning |
|---|---|---|---|
| SP0101 | error | Invalid YAML | The file isn't valid YAML, has more than one document, or uses a non-text key. |
| SP0102 | error | Unknown key | A key the policy format doesn't define. The message suggests the closest valid key. |
| SP0103 | error | Missing or unsupported version | Every policy file must declare `version: 1`. |
| SP0104 | error | Custom YAML tag | Tags such as `!include` aren't supported. Standard `!!` tags, anchors and aliases are fine. |
| SP0105 | error | Variable interpolation | `${VAR}`, `${{ expr }}` and `$(Var)` are never expanded. Secrets come only from environment variables. |
| SP0106 | error | Invalid value | A value of the wrong type, a value not in the allowed list, or a missing required key. |
| SP0107 | error | Locked rule overridden | A group or repo override declares a rule listed in the baseline's `locked:`. |
| SP0108 | error | Policy file out of place | A policy file is missing, misplaced, misnamed or uses `.yaml`, or its `layer:`, `name:` or `repo:` doesn't match its path. |
| SP0109 | error | Key not allowed in this layer | `locked:` outside the baseline, `name:` or `selectors:` outside a group, or `repo:` outside a repo override. |
| SP0110 | error | Unknown rule in locked | `locked:` names something that isn't a neutral rule. |
| SP0111 | error | Groups conflict | Two groups that match the same repo set the same rule to different values. Use a repo override, or tighter selectors. |
| SP0112 | error | Invalid glob | A selector, `scope:` or `exclude:` glob has the wrong number of parts, an empty part, or unsupported syntax. Only `*` and `?` are wildcards. |
| SP0113 | error | Unknown target | A selector or repo override starts with a target name that `targets.yml` doesn't define. |
| SP0114 | error | No merge types allowed | `allowedMergeTypes` is empty, which would block every pull request. |
| SP0115 | error | Invalid reviewer count | `minimumReviewers` is enabled without a `count:` of at least 1. |
| SP0116 | error | Too many reviewers for GitHub | More than 10 required reviewers on a layer that reaches a GitHub target. |
| SP0117 | error | Auth method not supported here | An `auth:` method the target's platform doesn't support, such as `entra` on Azure DevOps Server or `app` on Azure DevOps. |
| SP0118 | error | Incomplete target | A GitHub target without `scope:`, an Azure DevOps target without `url:`, or a mismatch between `auth: app` and `appId:`. |
| SP0119 | warning | Redundant enabled form | A boolean rule written as `{ enabled: ... }`. Write `true` or `false` instead. |
| SP0120 | warning | Repo outside target scope | A repo override or exact selector names a repo outside its target's `scope:`, or inside its `exclude:`, so it never applies. |
| SP0201 | warning | Selector matches nothing | A selector glob matches no repo in scope. Reported by `plan`. |
| SP0202 | error | Repo not found | An exact selector or repo override names a repo that doesn't exist, which is likely a typo or a rename. Reported by `plan`. |
| SP0203 | info | Archived repo skipped | An archived (GitHub) or disabled (Azure DevOps) repo in scope. It is skipped. |
| SP0204 | warning | Empty repo skipped | An empty repo in scope has no default branch to protect, so it is skipped. |
| SP0205 | info | Fork skipped | A GitHub fork in scope is skipped unless the target sets `includeForks: true`. |
| SP1001 | error | Target unreachable | A target can't be reached, or failed its preflight. No plan file is written; `--skip-unreachable` leaves the target out and makes this a warning. |
| SP1002 | error | Repo read failed | Reading one repo's live state failed. The other repos are still planned, and plan exits 1. |
| SP2101 | warning | Unmanaged setting is stricter | A setting no rule declares is stricter than the policy, so the effective result differs from the declared one. |
| SP2201 | warning | Ambiguous live match | More than one live Azure DevOps policy matches a rule, so Setpoint leaves them alone. |
| SP2202 | warning | Live value unknown | The token can't see a live setting, such as GitHub bypass actors, so plan reports it as unknown instead of as a change. |
| SP3001 | warning | Private repo on GitHub Free skipped | Rulesets don't apply to private repos on GitHub Free, so the repo is skipped. |
