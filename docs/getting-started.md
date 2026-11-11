# Getting started

This walks through a first policy repo, from `validate` to a first `apply`. It takes about fifteen minutes, most of it creating tokens.

> **Azure DevOps:** `validate` and `plan` work against Azure DevOps Services and Server 2022 today. Writing to Azure DevOps is still being built ([#36](https://github.com/Quattordici/Setpoint/issues/36)), so in this build `apply` changes GitHub only and reports every Azure DevOps write as not done.

## 1. Install the tool

Setpoint is a .NET tool and needs the .NET 10 runtime. Install a pinned version into a folder, so a pipeline always runs the same Setpoint that made its plan:

```sh
dotnet tool install Setpoint --version 0.1.0-alpha --tool-path ./.setpoint
./.setpoint/setpoint --version
```

Until the package is on nuget.org, build it from this repo instead and add `--add-source artifacts/packages --prerelease` (see [Building](../README.md#building)).

## 2. Create the policy repo

A policy repo is a plain Git repo of YAML files, owned by the platform team. Copy [`samples/policy-repo`](../samples/policy-repo) as a starting point:

```text
policy-repo/
├── targets.yml                          the platforms and which repos on each are managed
├── baseline.yml                         the rules every repo in scope gets
├── groups/
│   └── payments.yml                     extra rules for a set of repos, picked by selectors
└── repos/
    └── github/acme/payments-legacy.yml  one repo's exceptions
```

Each repo's rules are layered: the baseline, then every group whose selectors match the repo, then the repo's own override. A lower layer replaces a rule's whole value, `~` leaves a rule unmanaged from that layer down, and the baseline's `locked:` list names the rules nobody below it may change.

`targets.yml` names each platform endpoint. The names matter: they start every selector and override path, and they decide which environment variable holds each token.

```yaml
version: 1
targets:
  github:                       # token in SETPOINT_GITHUB_TOKEN
    platform: github
    scope: [acme/*]             # required on GitHub
    exclude: [acme/*-sandbox]
  ado:                          # token in SETPOINT_ADO_TOKEN
    platform: azuredevops
    url: https://dev.azure.com/acme
    scope: ["Payments/*", "Platform/*"]
```

Edit `scope:` and `url:` to match your estate. On Azure DevOps Server, `url:` is the collection URL, such as `https://ado.example.com/tfs/DefaultCollection`. Platform details are in [github.md](github.md) and [azure-devops.md](azure-devops.md).

The baseline holds the neutral rules. Setpoint translates each one into a ruleset rule on GitHub and a branch policy or permission on Azure DevOps. Two more apply to the whole repo: `access` gives teams (GitHub) or groups and users (Azure DevOps) a role on the repo (see [GitHub](github.md#team-access) and [Azure DevOps](azure-devops.md#access)), and `environments` sets deployment approvals (see [GitHub](github.md#environments)):

```yaml
version: 1
layer: baseline
locked: [minimumReviewers, protectFromForcePushAndDelete]
rules:
  minimumReviewers:
    count: 1
    resetOnPush: true
  requireCommentResolution: true
  allowedMergeTypes: [squash, rebase]
  protectFromForcePushAndDelete: true
```

Add a schema modeline to each file (the sample has them) and your editor checks it as you type. See [Editor support](../README.md#editor-support).

## 3. Validate

`validate` is offline. It needs no token and contacts nothing, so run it on every pull request to the policy repo.

```sh
setpoint validate --policy ./policy-repo
```

```text
Policy is valid: 2 targets, 1 group, 1 repo override.
```

A problem prints as one line per diagnostic, with its file, line, column and code, and the command exits 2:

```text
groups/payments.yml:8:5: error SP0113: There is no target named 'gh' in targets.yml. Known targets: ado, github.
```

Every code and what to do about it is in [diagnostics.md](diagnostics.md).

## 4. Create read-only tokens and plan

`plan` reads live state and changes nothing, so give it read-only tokens. The minimum for each platform:

- **GitHub:** a fine-grained PAT on the organization with **Metadata: read** and **Contents: read**.
- **Azure DevOps:** a PAT with **Code (read)** and **Build (read)**.

[permissions.md](permissions.md) has the full list, including GitHub Apps, `System.AccessToken` and Entra ID. Put each token in its target's variable and plan:

```sh
export SETPOINT_GITHUB_TOKEN=github_pat_...
export SETPOINT_ADO_TOKEN=...
setpoint plan --policy ./policy-repo --out plan.json
```

The plan header shows what each target turned out to be and who Setpoint signed in as. Then each repo that would change is listed with its changes, the layer each value comes from, and the native object that would be written. In-sync repos are counted, not listed; add `--verbose` to see them. With `--output plain`, or whenever the output isn't a terminal, each line stands alone. An example (your names and counts will differ):

```text
setpoint 0.1.0-alpha plan policy=3f9c2a1 files=5 repos=14
target github detected="github.com" api=2022-11-28 auth=pat identity=platform-bot repos=9
target ado detected="Azure DevOps Services" api=7.0 auth=pat identity=platform-bot@acme.com repos=5
+ github/acme/payments-api minimumReviewers: (none) -> count 1, resetOnPush [baseline] (locked) (create setpoint ruleset (new))
~ ado/Payments/ledger allowedMergeTypes: merge, squash -> squash [payments] (update Require a merge strategy #12 (ado:policy/12))
warning SP2202 ado/Payments/ledger: protectFromForcePushAndDelete: the token can't see the live value, so it is reported as (unknown) and not changed. Grant the token read access to see it.
summary repos_changed=2 create=1 update=1 remove=0 in_sync=11 skipped=1 failed=0 unmanaged=4 warnings=1 max_changes=2/25
plan saved to plan.json
```

The `SP2202` warning is expected with a read-only Azure DevOps token: reading the force-push permission needs a scope that can also change permissions, so a plan token leaves it out on purpose.

Setpoint only ever touches what the policy declares. Anything else on a repo, such as another ruleset or a project-wide policy, is left alone and counted as unmanaged; `--verbose` lists it.

## 5. Apply

`apply` writes, so it needs write tokens: **Administration: write** on GitHub, and on Azure DevOps **Code (read & write)**, **Build (read)**, **Identity (read)** and **Security (manage)**, with **Edit policies** and **Manage permissions** on the repos. Use separate tokens from the plan's.

```sh
export SETPOINT_GITHUB_TOKEN=github_pat_...   # the write token now
export SETPOINT_ADO_TOKEN=...
setpoint apply plan.json --policy ./policy-repo
```

Before its first write, `apply` checks that the policy files and the Setpoint version are the ones the plan was made from, that no more than 25 repos change (`--max-changes`), and that every object it will write is still exactly as planned. If anything moved, it writes nothing and exits 3: plan again. The checks are described in [Applying safely](../README.md#applying-safely).

```text
applied        github/acme/payments-api  1 of 1 written
apply: repos applied=1 failed=0 not_attempted=0 skipped=0 objects_written=1
```

Run `setpoint plan` again afterwards and the applied repos show as in sync.

`setpoint apply --yes` re-plans and applies in one step, without a plan file. That suits a first trial on a test org. In a pipeline, keep the plan and the apply separate, so a person reviews the exact changes before they are written.

## Next

- Run it from a pipeline, with a read-only plan stage and an approved apply stage: [pipelines.md](pipelines.md).
- Check for drift every weekday morning from a scheduled pipeline: [drift.md](drift.md).
- The plan file format, for reports and tooling: [plan-file.md](plan-file.md).
- Every exit code and diagnostic: [diagnostics.md](diagnostics.md).
