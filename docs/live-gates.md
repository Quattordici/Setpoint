# Live gates

Pull-request CI runs unit tests and WireMock tests only. The **live gates** run the real tool against real test environments, by hand or nightly, and the recordings they make become the WireMock fixtures. This page is the runbook: what the environments are, how to set them up once, how to run a gate, how to reset the VM, how to record fixtures and how to rotate the tokens.

## The environments

| Environment | What | Gate | Writes |
|---|---|---|---|
| `ado-server` | An **Azure DevOps Server 2022 Express** VM on Hyper-V, restored from a checkpoint before each run | **Phase 2 gate** | Yes |
| `ado-cloud` | A free **Azure DevOps Services** org | Phase 1 gate (Services) | Yes |
| `github` | A throwaway **GitHub Free** org with public repos only | Phase 1 gate (GitHub) | Yes |
| `ado-employer` | Mattias's employer's Azure DevOps Server 2022 | Plan only | **Never** |

The employer's server is plan-only, with a read-only PAT (`vso.code` + `vso.build`). The gate script has no code path that applies to it and refuses to record it, its URL is passed on the command line and never committed, and the nightly workflow never runs against it.

Every environment uses the same names, so one gate policy fits all of them:

- the target is named `live`, so its token is always `SETPOINT_LIVE_TOKEN`;
- the GitHub org and the Azure DevOps project are both named `setpoint-live`;
- each holds three repos with at least one commit on `main`: `gate-basic`, `gate-payments` and `gate-legacy`;
- on Azure DevOps, the `setpoint-live` project has a pipeline named `gate-ci` (any YAML pipeline in `gate-payments` will do), because the gate requires it as a check.

If `setpoint-live` is taken as a GitHub org name, pick another and change it in `tests/live/targets/github.yml`, `tests/live/policies/gate/groups/payments.yml` and the override's path and `repo:` under `tests/live/policies/gate/repos/`.

## What a gate checks

`tests/live/Invoke-LiveGate.ps1` builds Setpoint from the checkout, then, in a fresh output folder:

| Step | Command | Expects |
|---|---|---|
| `validate-gate`, `validate-reset` | `validate --strict` on [the gate policy](../tests/live/policies/gate) and [the reset policy](../tests/live/policies/reset) | 0 |
| `reset` | `apply --yes` with the reset policy, which removes what the gate sets | 0 |
| `plan` | `plan --out plan.json --exit-code` with the gate policy | 5: changes found |
| `apply` | `apply plan.json` | 0 |
| `replan-in-sync` | `plan --exit-code` again | 0: everything in sync, so apply did exactly what plan said |
| `apply-stale-plan` | `apply plan.json` again | 3: the stale-plan guard refuses it, because the objects changed |
| `drift-in-sync` | `drift --strict` | 0: no drift right after apply |
| `hand-edit` | The script raises `gate-basic`'s minimum reviewer count from 1 to 3 through the platform's own API, not through Setpoint | - |
| `drift-found` | `drift --strict --report drift.json` | 5: drift, and the report names `gate-basic` and `minimumReviewers.count` and no other repo |
| `apply-drift-report` | `apply drift.json` | 0: the report applies like a plan and puts the count back |
| `drift-restored` | `drift --strict` | 0: no drift again |

The last five steps are **gate 3**: drift runs unattended and finds a hand edit. In the nightly workflow the drift summaries go to the run page (`GITHUB_STEP_SUMMARY`), and `drift.json` is uploaded with the rest of the output. Gate 3 passes when one nightly run is green on `github` and on `ado-cloud`; the Azure DevOps drift steps need writes, so they run once `LIVE_GATE_ADO_APPLY` is `true`.

The gate policy uses every neutral rule, a group, a repo override, a removal and both provider blocks, so one run exercises every mapping. A step that doesn't exit as expected stops the run. The output folder, `tests/live/out/<environment>-<timestamp>/`, holds each step's output, the plan file, a transcript and any recordings. Git ignores it.

`-PlanOnly` stops after `validate-gate`, `plan` and a read-only `drift`, and the last two accept 0 or 5. `ado-employer` is always plan-only, with no hand edit.

> Azure DevOps writes landed with [#36](https://github.com/Quattordici/Setpoint/issues/36), but they have only run against stubs. Run the `ado-*` gates by hand without `-PlanOnly` once on the Express VM (from its `clean` checkpoint) before turning them on in the nightly workflow: it keeps them plan-only until the repository variable `LIVE_GATE_ADO_APPLY` is `true`.

## One-time setup

You need PowerShell 7.2 or later and the .NET 10 SDK on the machine that runs the gates.

### The Express VM (`ado-server`)

1. In Hyper-V, create a VM named `setpoint-ado` (Windows Server 2022, 2 vCPUs, 8 GB RAM, 80 GB disk) on a network your machine can reach.
2. Install **Azure DevOps Server 2022** with the latest 2022 update, and pick the **Express** configuration. It installs SQL Server Express and creates `DefaultCollection`. Keep the public URL as `http://setpoint-ado/`. If you change the host name or add `/tfs`, change `url:` in `tests/live/targets/ado-server.yml` to match.
3. Create the `setpoint-live` project, its three repos (initialise each with a README) and the `gate-ci` pipeline.
4. Create a PAT for a Project Administrator of `setpoint-live` with **Code (read & write)**, **Build (read)**, **Identity (read)** and **Security (manage)**, and the longest expiry the server allows. Store it in your password manager as *Setpoint live: ado-server*.
5. Run a plan to check the VM answers: set `SETPOINT_LIVE_TOKEN` to the PAT and run `./tests/live/Invoke-LiveGate.ps1 -Environment ado-server -PlanOnly`.
6. Shut the VM down and take a **standard** checkpoint named `clean`:

   ```powershell
   Stop-VM -Name setpoint-ado
   Checkpoint-VM -Name setpoint-ado -SnapshotName clean
   ```

   The PAT lives in the server's database, so it is part of the checkpoint. Every restore brings it back, valid until its expiry date.

### The Azure DevOps Services org (`ado-cloud`)

1. Create a free organization at <https://dev.azure.com>, named `setpoint-live` if it's free. Otherwise change `url:` in `tests/live/targets/ado-cloud.yml`.
2. Create the `setpoint-live` project, its three repos and the `gate-ci` pipeline.
3. Create a PAT with the same four scopes as on the VM, scoped to this organization only. Store it as *Setpoint live: ado-cloud*.

### The GitHub org (`github`)

1. Create a free organization named `setpoint-live`, and the three repos as **public** repos, each with a README. Rulesets don't work on private repos on GitHub Free.
2. Create a fine-grained PAT with resource owner `setpoint-live`, access to the three `gate-*` repos only, and **Administration: read and write**, **Contents: read** and **Metadata: read**. Store it as *Setpoint live: github*.

### The employer's server (`ado-employer`)

Create a PAT with **Code (read)** and **Build (read)** only, in your own account on that server. Never give it more. Keep it off GitHub: this gate runs only by hand.

### Nightly runs

The [Live gate workflow](../.github/workflows/live-gate.yml) runs nightly at 02:00 UTC and by hand (*Actions → Live gate → Run workflow*), on a self-hosted runner. It is off until you turn it on.

1. Register a self-hosted runner on your machine (*Settings → Actions → Runners → New self-hosted runner*), with the extra label `setpoint-live`. For `-ResetVm`, the runner's account must be in the **Hyper-V Administrators** group.
2. Create the environment `live-gate` (*Settings → Environments*), with a deployment branch rule allowing only `main`, and the secrets `LIVE_GITHUB_TOKEN`, `LIVE_ADO_CLOUD_TOKEN` and `LIVE_ADO_SERVER_TOKEN`.
3. Set the repository variable `LIVE_GATE_ENABLED` to `true` (*Settings → Secrets and variables → Actions → Variables*). Set `LIVE_GATE_ADO_APPLY` to `true` once an Azure DevOps gate has passed by hand with writes.

The workflow runs only on `schedule` and `workflow_dispatch`, never on pull requests, so code from a fork can't reach the runner or the tokens. It runs the environments one at a time, restores the VM before `ado-server`, and uploads each run's output as an artifact for 14 days.

## Running a gate by hand

```powershell
$env:SETPOINT_LIVE_TOKEN = '<the token for this environment>'

./tests/live/Invoke-LiveGate.ps1 -Environment github
./tests/live/Invoke-LiveGate.ps1 -Environment ado-cloud -PlanOnly
./tests/live/Invoke-LiveGate.ps1 -Environment ado-server -ResetVm -PlanOnly

# Plan only, never apply, never record. -EmployerScope is required, so a run never reads the whole collection.
$env:SETPOINT_LIVE_TOKEN = '<the read-only employer PAT>'
./tests/live/Invoke-LiveGate.ps1 -Environment ado-employer -EmployerUrl https://tfs.example.com/DefaultCollection -EmployerScope 'Platform/*'
```

The script exits 0 when every step passed and 1 otherwise, naming the step and its output file. Clear the token afterwards: `Remove-Item Env:SETPOINT_LIVE_TOKEN`.

Don't commit anything from an employer run. Its plan file names the employer's repos and identities.

## Resetting the VM

The gate's `-ResetVm` restores the `clean` checkpoint, starts the VM and waits up to 15 minutes for `_apis/connectionData` to answer. By hand:

```powershell
Restore-VMCheckpoint -VMName setpoint-ado -Name clean -Confirm:$false
Start-VM -Name setpoint-ado
```

To change the clean state (a server update, a new repo, a new PAT), restore `clean`, make the change, shut down, delete the old checkpoint and take a new one with the same name:

```powershell
Remove-VMCheckpoint -VMName setpoint-ado -Name clean
Checkpoint-VM -Name setpoint-ado -SnapshotName clean
```

The other environments have no snapshot. The gate's `reset` step applies the [reset policy](../tests/live/policies/reset) instead, which removes what the gate sets. It can't empty `allowedMergeTypes`, so that rule is left as it is; if a hand edit leaves a test repo in a state the reset can't undo, delete the `setpoint` ruleset (GitHub) or the repo's branch policies (Azure DevOps) by hand.

## Recording fixtures

Recordings are redacted WireMock.Net mappings, one file per HTTP exchange, in the format the adapter tests replay. Record against the VM, the cloud org or the GitHub org, never against the employer's server.

1. Run the gate with `-Record`. Each step records under `out/<run>/recordings/<step>/`, and the script fails if any recording contains the token or anything shaped like a credential.
2. Pick the exchanges a test needs. Rename the live names to the fixture names the tests use: the host to `ado.contoso.local` (Server), `dev.azure.com/contoso` (Services) or the `ghe` test target (GitHub), and the org, project, repo and user names to the ones in the test. Remove anything personal, such as display names and email addresses.
3. Copy them under the adapter's `tests/<adapter project>/Fixtures/<area>/`, next to a `README.md` that says where and when they were recorded.
4. Run `dotnet test --solution Setpoint.slnx`. `FixtureSecretTests` scans every committed fixture for credential shapes and fails the build if it finds one.

## Rotating the test tokens

Rotate each token before it expires, and at once if one may have leaked (for example, a recording scan failed after a run that was pushed).

| Token | Where it lives | Rotate |
|---|---|---|
| ado-server PAT | The VM's database (inside the `clean` checkpoint), your password manager, the `LIVE_ADO_SERVER_TOKEN` secret | Restore `clean`, create a new PAT, revoke the old one, retake the checkpoint (see above), then update the password manager and the secret |
| ado-cloud PAT | Your password manager, `LIVE_ADO_CLOUD_TOKEN` | *User settings → Personal access tokens*: regenerate, then update both. Services caps PATs at one year |
| github fine-grained PAT | Your password manager, `LIVE_GITHUB_TOKEN` | *Settings → Developer settings → Fine-grained tokens*: regenerate, then update both |
| ado-employer PAT | Your password manager only | Regenerate on the employer's server. Never store it in GitHub |

After rotating, run each gate once by hand (or the workflow with *Run workflow*) to check the new token works.
