# Checking for drift

Drift is when live state stops matching the policy: someone lowers a reviewer count by hand, deletes a build validation policy, or turns off force-push protection. `setpoint drift` finds it, and a scheduled pipeline turns it into a failed run that notifies the platform team.

## What drift checks

`setpoint drift` plans exactly like `setpoint plan`: it validates the policy, reaches every target, reads every repo in scope and diffs the managed settings against the policy. It never writes.

It checks only what Setpoint manages. On GitHub that is the `setpoint` ruleset; on Azure DevOps it is the branch policies matched by type and default branch, and the force-push ACE. A setting Setpoint leaves alone (`~` in the policy, or anything outside the neutral rules and provider blocks) is not drift. [github.md](github.md) and [azure-devops.md](azure-devops.md) say exactly what is managed on each platform.

```sh
setpoint drift --strict --report drift.json --summary drift.md
```

| Option | Does |
|---|---|
| `--report <file>` | Saves the report, in the [plan file](plan-file.md) format. |
| `--json` | Prints the report to standard output; everything else goes to standard error. |
| `--summary <file>` | Appends a short Markdown summary for a pipeline's run page. |
| `--strict` | Makes every warning an error, so a new warning fails the check too. |
| `--skip-unreachable` | Leaves out a target that can't be reached, with a warning, instead of failing. |
| `--verbose`, `--output` | As for `plan`. |

## Exit codes

| Code | Means |
|---|---|
| 0 | No drift: every repo in scope matches the policy. |
| 5 | Drift: at least one repo differs. The output and the report name each repo and setting. |
| 1 | The check failed: a target couldn't be reached or a repo couldn't be read, or `--strict` made a warning an error. Something that wasn't read could be hiding drift, so this never passes as 0. |
| 2 | The policy is invalid. No target was contacted. |

All codes are listed in [diagnostics.md](diagnostics.md#exit-codes).

## A hand edit or an unapplied change?

Setpoint keeps no state between runs, so drift can't tell a setting someone changed by hand from a policy change that was merged but not applied yet. Both show as a difference.

Run drift against `main`, and apply every change merged to `main`, as the [two-stage pipeline](pipelines.md) does. Then a difference on a weekday morning means someone changed a setting outside the policy repo. If drift fires right after a merge, check whether the apply stage is still waiting for approval.

## Putting things back

Two ways, both through the usual apply guards:

- **Apply the report.** The report is a plan file, so after reviewing it, `setpoint apply drift.json` writes exactly the settings it lists and nothing else. It refuses, writing nothing, if anything moved since the report was made (exit 3), so apply it soon after the check. It needs the same commit of the policy repo and the same Setpoint version, and it needs write credentials, so run it where your apply stage runs, not in the drift job.
- **Plan and apply.** Run the normal pipeline on `main`, or `setpoint plan --out plan.json` and `setpoint apply plan.json`. This is the way to go when the report is old.

A report from a run that failed (exit 1) is incomplete: what couldn't be read is missing from it. Plan again before applying anything.

If the hand edit was intended, change the policy instead: make a pull request on the policy repo, so the new value is reviewed and stays.

## Scheduled pipelines

Two ready-to-copy pipelines run drift on weekday mornings with read-only credentials:

| Platform | File | Copy it to |
|---|---|---|
| Azure Pipelines | [`samples/pipelines/drift-azure-pipelines.yml`](../samples/pipelines/drift-azure-pipelines.yml) | `drift-azure-pipelines.yml` in the policy repo, as a second pipeline |
| GitHub Actions | [`samples/pipelines/drift-github-actions.yml`](../samples/pipelines/drift-github-actions.yml) | `.github/workflows/setpoint-drift.yml` in the policy repo |

They reuse the read-only secrets from the [plan stage setup](pipelines.md): the `setpoint-plan` variable group on Azure Pipelines and the `setpoint-plan` environment on GitHub Actions. Neither ever sees a write token.

Each run:

1. validates the policy with `--strict`;
2. runs `drift --strict --report drift.json --summary ...`, which fails the run on any non-zero exit code, so the platform's usual failed-run notifications reach the team;
3. shows the Markdown summary on the run page (`$GITHUB_STEP_SUMMARY` on GitHub, `##vso[task.uploadsummary]` on Azure Pipelines) and adds an error annotation that says drift was found;
4. publishes `drift.json` as an artifact, even when the check failed.

The schedule is in UTC. Azure Pipelines runs it only on `main` (`always: true`, since drift happens outside the repo and `main` may not have changed); GitHub runs scheduled workflows on the default branch. Both can be started by hand too.
