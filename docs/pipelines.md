# Running Setpoint from a pipeline

A platform team usually runs Setpoint from the policy repo's own pipeline, in two stages:

1. **Plan**, on every pull request and every push to `main`. It validates the policy, plans with **read-only** credentials, and publishes `plan.json` as an artifact.
2. **Apply**, only on `main` and only when the plan found changes. It waits for a person to approve, then applies that exact `plan.json` with **write** credentials.

Splitting them means the write credentials are only ever exposed to a job a person approved, and the person approves the changes they saw: `apply` refuses the plan, writing nothing, if anything moved since it was made (exit 3).

Two ready-to-copy pipelines implement this:

| Platform | File | Copy it to |
|---|---|---|
| Azure Pipelines | [`samples/pipelines/azure-pipelines.yml`](../samples/pipelines/azure-pipelines.yml) | `azure-pipelines.yml` in the policy repo |
| GitHub Actions | [`samples/pipelines/github-actions.yml`](../samples/pipelines/github-actions.yml) | `.github/workflows/setpoint.yml` in the policy repo |

Both assume the targets are named `github` and `ado`, as in [the sample policy repo](../samples/policy-repo). Rename the `SETPOINT_<TARGET>_TOKEN` variables to match your `targets.yml`. The permissions each token needs are in [permissions.md](permissions.md).

## How both pipelines work

- **The Setpoint version is pinned** (`1.0.0`) and installed the same way in both stages. `apply` refuses a plan made by another version, so a floating version could break an apply that was approved between releases.
- **`validate --strict`** runs first, so a policy mistake fails fast with a file and line, before any token is used.
- **`plan --exit-code`** exits 5 when there are changes. The step turns that into an output, `hasChanges`, so the apply stage is skipped, and nobody is asked to approve, when everything is in sync. Any other non-zero code fails the stage: 1 for a runtime failure such as an unreachable target, 2 for an invalid policy. See [diagnostics.md](diagnostics.md#exit-codes).
- **The apply stage checks out the same commit.** `apply` hashes the policy files and refuses the plan if they changed, so it needs them exactly as the plan saw them. Both platforms check out the triggering commit in every stage, which is what you want.
- **The plan artifact holds no secrets.** It records each target's sign-in method and identity, never a token, so it is safe to keep and to show reviewers.
- **Drift has its own pipeline.** A separate scheduled pipeline runs `setpoint drift` with the same read-only secrets and fails when live state no longer matches the policy. See [drift.md](drift.md).
- `--max-changes` keeps its default of 25 repos. A plan that changes more stops at apply with exit 4. Raise it with `--max-changes N` on the apply step when a large change is intended.

## Azure Pipelines

### Setup

1. **Variable groups.** Under *Pipelines → Library*, create two variable groups and mark every value secret:
    - `setpoint-plan` with `githubPlanToken` and `adoPlanToken`, the read-only tokens;
    - `setpoint-apply` with `githubApplyToken` and `adoApplyToken`, the write tokens.

   Give the pipeline access to both. Secret variables are never passed to scripts on their own, which is why each step maps them into `env:`.
2. **Environment and approval.** Under *Pipelines → Environments*, create an environment named `setpoint` and add an *Approvals* check naming the people who approve applies. Only the apply stage targets it, so only that stage waits.
3. **Restrict the write secrets.** In the `setpoint-apply` variable group's *Pipeline permissions*, allow only this pipeline. Consider an *Approvals and checks* entry on the group itself as well, so no other pipeline that gains access can use it unapproved.
4. **Pull requests.** The `pr:` trigger applies to policy repos on GitHub. For a policy repo in Azure Repos, add a *Build validation* branch policy on `main` that runs this pipeline instead. Either way the apply stage never runs for a pull request.

### Variants

- **`System.AccessToken` instead of PATs.** Set `auth: accessToken` on the Azure DevOps target and map `SYSTEM_ACCESSTOKEN: $(System.AccessToken)` in both steps instead of `SETPOINT_ADO_TOKEN`. The plan and apply stages then share the build service identity, so the plan stage is no longer read-only. See [permissions.md](permissions.md#systemaccesstoken).
- **Entra ID on Azure DevOps Services.** Set `auth: entra` and run each Setpoint step inside `AzureCLI@2` with an Azure Resource Manager service connection that uses workload identity federation. Use one service connection per stage, each bound to its own identity.
- **Azure DevOps Server with Windows authentication.** Set `auth: integrated`, use a self-hosted Windows agent pool and drop the token mappings. Run the apply stage on a pool whose agent account has the write permissions, and the plan stage on one whose account doesn't.

## GitHub Actions

### Setup

1. **Environments.** Under *Settings → Environments*, create two environments:
    - `setpoint-plan`, with no reviewers, holding the read-only tokens as environment secrets `SETPOINT_GITHUB_TOKEN` and `SETPOINT_ADO_TOKEN`;
    - `setpoint-apply`, with *Required reviewers* and a deployment branch rule allowing only `main`, holding the write tokens under the same two names.

   Each job sees only its own environment's secrets, so the same secret names give the plan job read tokens and the apply job write tokens. Don't add repository-level secrets with these names.
2. **Plans.** Required reviewers on environments are available on public repos, and on private repos on GitHub Team and Enterprise plans. On GitHub Free with a private policy repo, run apply by hand from a trusted machine, or host the policy repo where approvals are available.
3. **Pull requests from forks** get no secrets, so their plan job fails at the plan step. That is expected for a public policy repo: validate still runs, and a maintainer re-runs the plan after merging.

### Variants

- **A GitHub App for GitHub targets.** Set `auth: app` and `appId:` on the target, and store the App's private key as `SETPOINT_GITHUB_APP_KEY` instead of `SETPOINT_GITHUB_TOKEN`. The plan job then holds a key that can write, because plan and apply use the same App. See [permissions.md](permissions.md#github-app).
- **`GITHUB_TOKEN` can't apply.** It has no Administration permission. Use a PAT or an App.

## Running apply by hand

With no approvals available, the same split works by hand: let the pipeline plan and publish `plan.json`, download it, review it, and apply it from a machine that holds the write tokens:

```sh
git checkout <the commit the plan was made from>
setpoint apply plan.json
```
