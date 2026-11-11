# Diagnostics and exit codes

Every problem Setpoint reports has a stable `SPxxxx` code. A code never changes meaning once released, and retired codes are not reused. `--strict` turns every warning into an error.

| Range | Area |
|---|---|
| 01xx | Policy files, found offline by `validate` |
| 02xx | Selection: scope and selectors against live repos |
| 10xx | Reaching targets, reading and writing repos |
| 21xx | Ownership of managed settings |
| 22xx | Matching live objects |
| 30xx | Platform capabilities |
| 40xx | Apply guards: a stale plan and the change limit |

| Code | Default | Title | Meaning | What to do |
|---|---|---|---|---|
| SP0101 | error | Invalid YAML | The file isn't valid YAML, has more than one document, or uses a non-text key. | Fix the YAML at the reported line. Keep one document per file. |
| SP0102 | error | Unknown key | A key the policy format doesn't define. The message suggests the closest valid key. | Correct the key, usually to the suggested one, or remove it. |
| SP0103 | error | Missing or unsupported version | Every policy file must declare `version: 1`. | Add `version: 1` at the top of the file. |
| SP0104 | error | Custom YAML tag | Tags such as `!include` aren't supported. Standard `!!` tags, anchors and aliases are fine. | Remove the tag. Use an anchor and alias to reuse a value. |
| SP0105 | error | Variable interpolation | `${VAR}`, `${{ expr }}` and `$(Var)` are never expanded. Secrets come only from environment variables. | Write the value literally. Put secrets in the target's environment variable instead. |
| SP0106 | error | Invalid value | A value of the wrong type, a value not in the allowed list, or a missing required key. | Change the value to the type or one of the values the message names, or add the missing key. |
| SP0107 | error | Locked rule overridden | A group or repo override declares a rule listed in the baseline's `locked:`. | Remove the rule from the group or override, or take it out of `locked:` in the baseline. |
| SP0108 | error | Policy file out of place | A policy file is missing, misplaced, misnamed or uses `.yaml`, or its `layer:`, `name:` or `repo:` doesn't match its path. | Move or rename the file to the path the message gives, or fix its `layer:`, `name:` or `repo:`. |
| SP0109 | error | Key not allowed in this layer | `locked:` outside the baseline, `name:` or `selectors:` outside a group, or `repo:` outside a repo override. | Move the key to the layer that allows it. |
| SP0110 | error | Unknown rule in locked | `locked:` names something that isn't a neutral rule. | Use a neutral rule name, such as `minimumReviewers`. |
| SP0111 | error | Groups conflict | Two groups that match the same repo set the same rule to different values. Use a repo override, or tighter selectors. | Set the rule in a repo override, or narrow the groups' selectors so only one matches. |
| SP0112 | error | Invalid glob | A selector, `scope:` or `exclude:` glob has the wrong number of parts, an empty part, or unsupported syntax. Only `*` and `?` are wildcards. | Write the glob as `<target>/<owner-or-project>/<repo>` (selectors) or `<owner-or-project>/<repo>` (`scope:`, `exclude:`), using only `*` and `?`. |
| SP0113 | error | Unknown target | A selector or repo override starts with a target name that `targets.yml` doesn't define. | Use one of the target names the message lists, or add the target to `targets.yml`. |
| SP0114 | error | No merge types allowed | `allowedMergeTypes` is empty, which would block every pull request. | List at least one merge type, or leave `allowedMergeTypes` unmanaged with `~`. |
| SP0115 | error | Invalid reviewer count | `minimumReviewers` is enabled without a `count:` of at least 1. | Set `count:` to 1 or more, or set the rule to `false`. |
| SP0116 | error | Too many reviewers for GitHub | More than 10 required reviewers on a layer that reaches a GitHub target. | Lower `count:` to 10 or less for repos on GitHub, in a group or override that only reaches GitHub. |
| SP0117 | error | Auth method not supported here | An `auth:` method the target's platform doesn't support, such as `entra` on Azure DevOps Server or `app` on Azure DevOps. | Pick an `auth:` method the platform supports. See [permissions.md](permissions.md#signing-in). |
| SP0118 | error | Incomplete target | A GitHub target without `scope:`, an Azure DevOps target without `url:`, or a mismatch between `auth: app` and `appId:`. | Add `scope:` (GitHub) or `url:` (Azure DevOps), or pair `auth: app` with `appId:`. |
| SP0119 | warning | Redundant enabled form | A boolean rule written as `{ enabled: ... }`. Write `true` or `false` instead. | Write `true` or `false`. |
| SP0120 | warning | Repo outside target scope | A repo override or exact selector names a repo outside its target's `scope:`, or inside its `exclude:`, so it never applies. | Widen the target's `scope:`, take the repo out of `exclude:`, or delete the override. |
| SP0121 | error | Invalid environment | An `environments` entry can't be written as declared: `waitMinutes` is outside 0 to 43200, or it names more reviewers than the platform allows (6 on GitHub, teams and users together). Reported by `validate` and `plan`. | Bring the wait into range, or name fewer reviewers, for example one team instead of its members. |
| SP0122 | error | Shared setting declared differently | Some settings belong to more than one repo, such as an Azure DevOps environment, which belongs to its project. Repos in the project declare it differently, so neither value can be written. Each repo involved fails with the other repos named. Reported by `plan`. | Declare the environment the same way for every repo in the project, for example in one group that selects the whole project. |
| SP0201 | warning | Selector matches nothing | A selector glob matches no repo in scope. Reported by `plan`. | Check the selector for a typo. Remove it if the repos it was for are gone. |
| SP0202 | error | Repo not found | An exact selector or repo override names a repo that doesn't exist, which is likely a typo or a rename. Reported by `plan`. | Fix the repo name, or delete the override or selector if the repo was renamed or removed. |
| SP0203 | info | Archived repo skipped | An archived (GitHub) or disabled (Azure DevOps) repo in scope. It is skipped. | None. Unarchive or enable the repo to manage it again. |
| SP0204 | warning | Empty repo skipped | An empty repo in scope has no default branch to protect, so it is skipped. | Push a first commit to the repo, or exclude it. |
| SP0205 | info | Fork skipped | A fork in scope is skipped unless the target sets `includeForks: true`. | None, or set `includeForks: true` on the target to manage forks. |
| SP0206 | error | Pipeline not found | A `requiredChecks` name on Azure DevOps matches no pipeline in the repo's project, or more than one. Reported by `plan`. | Use the pipeline's exact name, or rename one of the pipelines so the name is unique in the project. |
| SP0207 | error | Principal not found | An `access` (or `github.access`) entry names a team or group that doesn't exist on the platform, or more than one matches, or it can't be used there: a repo owned by a GitHub user has no teams, and a team can't be in both `access` and `github.access`. Reported by `plan`. | Use the team's slug or the group's exact name, or leave `access` unmanaged (`~`) for those repos. |
| SP1001 | error | Target unreachable | A target can't be reached, or failed its preflight. No plan file is written; `--skip-unreachable` leaves the target out and makes this a warning. | Check the URL, the network path and the token in the message. Use `--skip-unreachable` to plan the other targets meanwhile. |
| SP1002 | error | Repo read failed | Reading one repo's live state failed. The other repos are still planned, and plan exits 1. | Read the message for the HTTP status. A 403 names the missing permission; see [permissions.md](permissions.md). Plan again. |
| SP1003 | error | Write failed | Writing one repo's changes failed, and the rest of that repo was not attempted. The other repos are still applied unless `--fail-fast` is set, and apply exits 1. | Fix the cause in the message, usually a missing write permission, then plan and apply again. Repos already applied show as in sync. |
| SP2101 | warning | Unmanaged setting is stricter | A setting no rule declares is stricter than the policy, so the effective result differs from the declared one. | None required. Declare the rule to manage it, or remove the stricter setting if it isn't wanted. |
| SP2102 | error | No merge method left | Unmanaged merge settings, such as GitHub's repo-level `allow_*_merge` flags or another ruleset, together with `allowedMergeTypes` leave no merge method, so pull requests couldn't complete. | Allow a merge method in `allowedMergeTypes` that the repo's own settings also allow, or change the repo's settings. |
| SP2103 | warning | Force push explicitly allowed | An identity, usually the branch's creator, has an explicit Allow for Force push on the Azure DevOps default branch. The deny Setpoint manages doesn't override it, and Setpoint leaves it alone; remove it in the branch's security settings. | Remove the explicit Allow from the default branch's security settings. |
| SP2201 | warning | Ambiguous live match | More than one live Azure DevOps policy matches a rule, so Setpoint leaves them alone. | Delete the duplicate policies by hand, keeping one, then plan again. |
| SP2202 | warning | Live value unknown | The token can't see a live setting, such as GitHub bypass actors, so plan reports it as unknown instead of as a change. | Grant the token the read permission the message names (see [permissions.md](permissions.md)), or accept that the rule isn't checked. |
| SP3001 | warning | Private repo on GitHub Free skipped | Rulesets don't apply to private repos on GitHub Free, so the repo is skipped. | Make the repo public, upgrade the GitHub plan, or exclude the repo. |
| SP3002 | warning | Rule not available on this repo | The repo's platform plan doesn't offer the rule, so it is skipped for this repo and the rest of the repo is still planned. On GitHub, environment approvals and wait timers on private repos need GitHub Enterprise. | Make the repo public, move it to a plan that has the feature, or leave the rule unmanaged (`~`) for it. |
| SP4001 | error | Plan is stale | The plan file no longer matches: a planned object changed or appeared since the plan, the policy files changed, or another Setpoint version made it. apply writes nothing and exits 3. Plan again. | Plan again and apply the new plan. |
| SP4002 | error | Too many changes | More repos would change than `--max-changes` allows (25 by default). apply writes nothing and exits 4. Raise the limit, or pass `--max-changes 0` to remove it. | Check the plan is intended, then apply with a higher `--max-changes`, or `--max-changes 0`. |

## Exit codes

Pipelines branch on these, so they never change once released.

| Code | Meaning | What to do |
|---|---|---|
| 0 | Success. `plan` found nothing to change, or found changes without `--exit-code`; `drift` found no drift; `apply` wrote everything. | None. |
| 1 | Runtime failure: an unreachable target (`SP1001`) unless `--skip-unreachable` is given, so `drift` can't hide drift behind it, a repo that couldn't be read (`SP1002`), a failed write (`SP1003`), or any other error outside the 01xx and 02xx ranges, including a warning that `--strict` made an error. | Read the diagnostics. After a partial apply, plan again: repos already applied show as in sync. |
| 2 | The policy is invalid, or the command line is wrong: any error in the 01xx or 02xx ranges, which includes everything `validate` catches. | Run `setpoint validate` and fix each reported line. |
| 3 | The plan is stale (`SP4001`). apply wrote nothing. | Plan again and apply the new plan. |
| 4 | A guard tripped: more repos would change than `--max-changes` allows (`SP4002`). apply wrote nothing. | Check the plan is intended, then raise `--max-changes`. |
| 5 | `plan --exit-code` found changes, or `drift` found live state that differs from the policy. | Apply the plan. A pipeline uses this to decide whether an apply stage is needed; see [pipelines.md](pipelines.md). For drift, review the report and apply it, or plan and apply as usual. |
