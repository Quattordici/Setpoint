# Azure DevOps

Setpoint manages Azure DevOps Services organizations and Azure DevOps Server 2022 (and later) collections. It plans and applies with every sign-in method: it reads branch policies and the default branch's Force push entries, shows the changes, and `apply` writes them.

## Targets

```yaml
targets:
  ado-onprem:
    platform: azuredevops
    url: https://ado.corp.example/tfs/DefaultCollection
  ado-cloud:
    platform: azuredevops
    url: https://dev.azure.com/contoso
    scope: ["Payments/*"]
    exclude: ["*/*-sandbox"]
```

- `url:` is the organization or collection URL, and it is required.
- `scope:` is optional. Without it, every repo in the collection or organization is managed, across all its projects. Globs match `<project>/<repo>`.
- You don't declare whether a target is Services or Server, or which release of Server it is. The preflight finds out (see below).

## Signing in

Secrets only ever come from environment variables, and every token is masked in logs and recordings. The plan header shows who Setpoint signed in as, whatever the method.

| `auth:` | Where | How |
| --- | --- | --- |
| `pat` (default) | Services and Server | A personal access token from `SETPOINT_<TARGET>_TOKEN` (`ado-onprem` reads `SETPOINT_ADO_ONPREM_TOKEN`), or the variable `tokenEnv:` names. Sent as Basic auth with an empty user name. |
| `accessToken` | Services and Server, in Azure Pipelines | The job's token from `SYSTEM_ACCESSTOKEN`, sent as a bearer token. Pipelines don't expose it to scripts by default, so map it in the step: `env: { SYSTEM_ACCESSTOKEN: $(System.AccessToken) }`. The project's build service identity needs the permissions below. |
| `integrated` | Server only | Windows authentication (Negotiate or NTLM) as the account Setpoint runs as, such as a self-hosted agent's service account. Setpoint reads no secret. |
| `entra` | Services only | A Microsoft Entra ID token for the Azure DevOps resource, through Azure.Identity. Tried in order: workload identity federation (an Azure Resource Manager service connection with `AzureCLI@2`, or the `AZURE_*` variables it sets), managed identity (`AZURE_CLIENT_ID` picks a user-assigned one), then the Azure CLI's `az login`. Nothing interactive. The token is reused until five minutes before it expires. |

`validate` rejects `entra` on a Server URL and `integrated` on a Services URL (SP0117).

A plan only reads, so a plan-stage token needs only **Code (read)** and **Build (read)** (`vso.code`, `vso.build`). With just those, plan can't read the default branch's security entries, so it reports `protectFromForcePushAndDelete` as unknown (SP2202) instead of as a change. Reading it also needs **Identity (read)** and **Security (manage)** (`vso.identity`, `vso.security_manage`). The latter can change permissions, so give it to the apply token, not the plan token.

## The preflight

Before any repo is listed, Setpoint takes these steps against the target. A failure in any of them makes the target unreachable (SP1001), and `--skip-unreachable` leaves it out instead.

1. **Sign-in and identity.** `GET _apis/connectionData` gives the signed-in identity, which the plan shows. If Azure DevOps doesn't accept the token, it answers with its sign-in page (203) or 401, or signs the request in anonymously. Setpoint reports each of these as a refused token.
2. **Services or Server.** The same response's `deploymentType` tells hosted (Services) from on-premises (Server). The URL decides only when it is missing: `dev.azure.com` and `*.visualstudio.com` are Services.
3. **Server release.** On Server, `OPTIONS _apis/policy` lists the REST versions the policy API has. The highest released one is the release, from Microsoft's mapping: 7.0 is Server 2022 and 7.1 is Server 2022.1. Anything older than 7.0 is refused, naming the release, unless `--allow-unsupported-server` is set. With the flag, Setpoint marks the target "(unsupported)" and uses the server's own version, because the older server refuses 7.0.
4. **Policy types.** `GET {project}/_apis/policy/types` must list each type Setpoint manages: Minimum number of reviewers, Build, Status, Comment requirements and Require a merge strategy. The Status type's id comes from Microsoft's Terraform provider rather than the REST reference, so the server is asked. A collection with no projects has nothing to manage, so this check is skipped.

Every REST call pins `api-version=7.0`. The two discovery calls in steps 1 and 3 carry no version, because they run before Setpoint knows which versions the server has. Pinning 7.0 on them would make a Server 2020 fail with "version out of range" instead of being named and refused.

## Listing repos

`GET _apis/git/repositories` lists every repo in the collection or organization. The repos in scope are listed in order of project and name, and some are skipped:

| Repo | Code | Severity |
| --- | --- | --- |
| Disabled, or in maintenance | SP0203 | info |
| A fork, unless the target sets `includeForks: true` | SP0205 | info |
| Empty (no default branch) | SP0204 | warning |

## What Setpoint owns

Rules apply to each repo's default branch. Setpoint owns a policy configuration only when its one scope is exactly this repo's default branch (`matchKind: Exact`). It matches the policy by type and that scope:

| Rule | Policy | Match |
| --- | --- | --- |
| `minimumReviewers` (+ `azuredevops.minimumReviewers`) | Minimum number of reviewers: `minimumApproverCount`, `resetOnSourcePush`, `creatorVoteCounts`, `allowDownvotes`, `blockLastPusherVote` | one per branch |
| `requiredChecks` (+ `validDuration`, `pathFilters`) | Build, one per pipeline: the check name is the pipeline's name, resolved to its `buildDefinitionId` | one per pipeline |
| `azuredevops.requiredChecks.statusChecks` | Status, one per check: `statusName`, `statusGenre` | one per genre/name |
| `requireCommentResolution` | Comment requirements | one per branch |
| `allowedMergeTypes` (+ `allowRebaseMerge`) | Require a merge strategy: `allowNoFastForward` (merge), `allowSquash`, `allowRebase`, `allowRebaseMerge`. The legacy `useSquashMerge` is read too: true is squash only, false a merge commit only | one per branch |
| `protectFromForcePushAndDelete` | A Force push deny (bit 8 in the Git Repositories namespace) for the project's **Project Valid Users** group on the default branch's token. One bit covers force-pushing and deleting | the group's entry |

- A disabled or optional (not blocking) policy is owned but carries no rules, so a plan turns it back on.
- Settings Setpoint doesn't manage, such as `requireVoteOnLastIteration`, keep their live values when a policy is updated.
- A required check that names no pipeline in the project, or more than one, fails the repo's plan with SP0206.
- Two owned policies with the same match are ambiguous (SP2201). Setpoint leaves that rule alone and never writes it.
- Policies with a wider scope (project-wide, a branch prefix, shared with other repos) and policy types Setpoint doesn't manage are **unmanaged**. Plan lists them, and SP2101 warns when one is stricter than the policy.
- Another identity's Force push deny on the branch is unmanaged. An explicit **Allow** for Force push, which a branch's creator gets, isn't overridden by the deny. Plan names the identity in SP2103 and leaves the entry alone.

Policies are read with the project-wide `GET {project}/_apis/policy/configurations`, which is released at 7.0, and filtered by scope in Setpoint. A project's policies, pipelines and Project Valid Users group are read once per run, however many of its repos are planned.

## Applying

`apply` writes each repo's planned objects in plan order, sending each payload exactly as the plan file holds it:

| Planned | Request |
| --- | --- |
| Create a policy | `POST {project}/_apis/policy/configurations` |
| Update a policy | `PUT {project}/_apis/policy/configurations/{id}`, the whole configuration. Settings Setpoint doesn't manage were copied from the live policy at plan time, and the plan's fingerprint makes sure they haven't changed since |
| Delete a policy | `DELETE {project}/_apis/policy/configurations/{id}` |
| Force push entry | `POST _apis/accesscontrolentries/2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87` with `merge: false`, which replaces Project Valid Users' entry on the default branch's token and leaves every other entry alone. The entry is the live one with only the Force push bit changed. Merging can't be used, because it ORs bits in and could never take the deny off |

- Before sending anything, Setpoint checks that the object is about this repo's default branch: a policy must be scoped to exactly that branch, and the Force push entry must be on its token. Anything else in a plan file is refused without a request.
- A create is refused as stale (exit 3) when an object with the same match now exists, or when several do, so an ambiguous match is never written.
- The first failed write stops that repo, and its later objects aren't attempted. Other repos carry on unless `--fail-fast` is set.
- A 401 or 403 names what is missing: for policies, Code (read & write) (`vso.code_write`) and the Edit policies permission; for the Force push entry, Security (manage) (`vso.security_manage`) and Manage permissions. A 404 on an update or delete means the policy is gone: plan again.

POSTs aren't retried after a server error, because the first one may have half-worked; plan again to see what was written.

## Still to verify on a live server

These are checked against stubs, not yet against a real server:

- that `connectionData` and `OPTIONS _apis/policy` answer without an `api-version` on Server 2022 and on Services;
- that Server 2022 reports `releasedVersion` 7.0 for the policy area, and Server 2022.1 reports 7.1;
- that `deploymentType` comes back as `hosted` or `onPremises`;
- the branch security token's encoding, and that `[Project]\Project Valid Users` is found by a general identity search on Server and on Services (`vssps.dev.azure.com`).

- that `merge: false` on Access Control Entries - Set replaces only the listed identity's entry, as the REST reference says;
- that a policy update's `PUT` accepts the configuration as Setpoint reads it, with `settings.scope` as the only scope.

Run a plan with a read-only PAT to check the first four; the plan only reads. The last two need an apply on a server you may change, such as the Express VM. Recorded fixtures replace the stubs in [#75](https://github.com/Quattordici/Setpoint/issues/75).
