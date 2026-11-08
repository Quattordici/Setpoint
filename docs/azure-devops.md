# Azure DevOps

Setpoint manages Azure DevOps Services organizations and Azure DevOps Server 2022 (and later) collections. This page grows with the adapter. This build has the preflight and the repo listing. Reading and writing branch policies come next, so for now `plan` lists an Azure DevOps target's repos and reports each one's read as failed (SP1002), and nothing is ever written.

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

`auth: pat` is the default, and it is the only method in this build. Setpoint reads the token from `SETPOINT_<TARGET>_TOKEN`, so `ado-onprem` reads `SETPOINT_ADO_ONPREM_TOKEN`. `tokenEnv:` names a different variable. The token is sent as Basic auth with an empty user name, and it is masked in every log and recording.

A plan only reads, so a plan-stage token needs only **Code (read)** and **Build (read)** (`vso.code`, `vso.build`).

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

## Still to verify on a live server

These are checked against stubs, not yet against a real server:

- that `connectionData` and `OPTIONS _apis/policy` answer without an `api-version` on Server 2022 and on Services;
- that Server 2022 reports `releasedVersion` 7.0 for the policy area, and Server 2022.1 reports 7.1;
- that `deploymentType` comes back as `hosted` or `onPremises`.

Run a plan with a read-only PAT to check them. The plan only reads.
