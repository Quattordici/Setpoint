# Permissions

Every platform checks two things on each call: what the **token** allows (its scopes or permissions), and what the **identity** behind it may do (its role, or the ACLs on the repo). A token never grants more than its identity has, so each table below gives both.

Use two credentials per target: a read-only one for `plan`, and a write one for `apply`, held only by the apply stage. `validate` contacts nothing and needs no credential at all. Where a credential comes from is in [Signing in](#signing-in) at the end.

## GitHub

### Fine-grained PAT or GitHub App

Fine-grained PATs and GitHub Apps use the same permission names.

| Command | Repository permissions | Role on each repo | Why |
|---|---|---|---|
| `plan` | **Metadata: read** | Read | Lists repos and reads rulesets. |
| `plan`, recommended | + **Contents: read** | Read | Without it GitHub leaves the repo merge flags (`allow_*_merge`) out of its answer, so `SP2102` (no merge method left) can't be checked. |
| `plan`, to see bypass actors | + **Administration: read** | Admin, or a custom role with *Edit repository rules* | GitHub only shows `bypass_actors` to callers with write access to the ruleset. Without it, `github.bypassActors` is reported as unknown (`SP2202`). Unconfirmed whether read is enough. |
| `plan`, for `access` | + **Administration: read**, and **Members: read** on the organization | Read | Lists the repo's teams, and the organization's teams to check slugs (`SP0207`). Without the first, `access` is reported as unknown (`SP2202`). |
| `apply` | **Administration: write** + Metadata: read + Contents: read | Admin, or a custom role with *Edit repository rules* | Creates, updates and deletes the `setpoint` ruleset. |
| `apply`, for `access` | + **Members: read** on the organization | Organization owner, or a maintainer of each named team with admin on the repo | Adds the repo to a team with its role, or removes it. |

A fine-grained PAT's resource owner must be the organization, and the organization may have to approve the token before it works on private repos.

### GitHub App

Install the App on each organization or user in `scope:` and grant it the permissions above. The App's installation is its role: it can write rulesets on every repo it was granted, so grant it only the repos Setpoint manages if you scope `scope:` down.

With `auth: app`, plan and apply use the same App, because `appId:` is part of the policy and the plan file must match it. So the plan stage holds a key that can write: protect it like a write secret. If the plan stage must be strictly read-only, use a pair of fine-grained PATs instead.

### Classic PAT

| Command | Scope | Role |
|---|---|---|
| `plan` | `repo` | Read |
| `apply` | `repo` | Admin, or *Edit repository rules* |

GitHub answers the ruleset calls with `X-Accepted-OAuth-Scopes: repo`. Whether `public_repo` alone works on public repos is unconfirmed. Prefer a fine-grained PAT or an App: `repo` gives full access to every repo the user can reach.

### `GITHUB_TOKEN`

The Actions job token has no Administration permission, so it can't run `apply`. It can run `plan` against the repo it belongs to, which is rarely useful. Use a PAT or an App.

## Azure DevOps

### PAT scopes

| Command | Scopes | Why |
|---|---|---|
| `plan` | **Code (read)** `vso.code` + **Build (read)** `vso.build` | Lists repos, reads policies and policy types, and resolves `requiredChecks` names to pipelines. |
| `plan`, to see force-push protection | + **Identity (read)** `vso.identity` + **Security (manage)** `vso.security_manage` | Reads the default branch's security entries. There is no read-only security scope, so a read-only plan reports `protectFromForcePushAndDelete` as unknown (`SP2202`) instead. |
| `plan`, for `access` | + **Identity (read)** `vso.identity` + **Security (manage)** `vso.security_manage` | Resolves the named groups and users (`SP0207`), and reads the repo's and project's security entries. Without them `access` is reported as unknown (`SP2202`). |
| `apply` | **Code (read & write)** `vso.code_write` + **Build (read)** + **Identity (read)** + **Security (manage)** | Writes policy configurations, the Force push deny and access entries. |

`vso.code_manage` isn't needed. On Azure DevOps Server 2022 before Update 1, some REST APIs had no granular scope and may need a **Full access** PAT; whether the security APIs Setpoint calls are among them is unconfirmed.

### Permissions on the identity

| Command | Permission | Where |
|---|---|---|
| `plan` | **Read** on the repos, and **View builds** on the pipelines `requiredChecks` names | Granted to Readers and Contributors by default. |
| `apply` | + **Edit policies** and **Manage permissions** | Project Settings → Repositories → Security, on the top-level *Git repositories* node so every repo inherits it, or per repo. Project Administrators have both by default. Manage permissions covers both the Force push deny and `access`. |

Policies marked `isEnterpriseManaged` also need *Manage Enterprise Policies*. Setpoint reports them as unmanaged and doesn't write them.

An identity with **Manage permissions** can grant itself a bypass, so give it to a dedicated identity used only by the apply stage.

### `System.AccessToken`

With `auth: accessToken`, Setpoint signs in as the pipeline job's identity: `<Project> Build Service (<org>)`, or `Project Collection Build Service (<org>)` when the pipeline's job authorization scope is the collection. Scopes don't apply; only the identity's permissions do.

| Command | Grant the build service identity |
|---|---|
| `plan` | Nothing. It can read the repos and pipelines in its own project by default. With a project-scoped job token, repos in other projects are not visible: turn off *Limit job authorization scope to current project* or use a PAT. |
| `apply` | **Edit policies** and **Manage permissions** on *Git repositories*, in every project in scope. |

The same identity runs every pipeline in the project, so granting it Manage permissions grants it to all of them. Prefer a PAT, or a dedicated project for the Setpoint pipeline, for `apply`.

### Microsoft Entra ID (Services only)

With `auth: entra`, Setpoint signs in as a service principal or managed identity. Add it to the organization as a user (Basic access is enough), then grant it the identity permissions above: Readers for `plan`, plus Edit policies and Manage permissions for `apply`. Entra tokens carry no Azure DevOps scopes, so only the permissions count. Use two identities, or two service connections, for plan and apply.

### Windows authentication (Server only)

With `auth: integrated`, Setpoint signs in as the Windows account it runs as, such as a self-hosted agent's service account. Grant that account the identity permissions above. Use a different agent pool, or a different service account, for apply.

## Signing in

Secrets only ever come from environment variables, never from policy files.

| Target `auth:` | Variable | Platform |
|---|---|---|
| `pat` (default) | `SETPOINT_<TARGET>_TOKEN`, or the variable `tokenEnv:` names | Both |
| `app` | `SETPOINT_<TARGET>_APP_KEY`: the App's private key (PEM). `appId:` goes in `targets.yml` | GitHub |
| `accessToken` | `SYSTEM_ACCESSTOKEN`: map it in the step, `env: { SYSTEM_ACCESSTOKEN: $(System.AccessToken) }` | Azure DevOps |
| `entra` | Workload identity federation, managed identity or `az login`, through Azure.Identity | Azure DevOps Services |
| `integrated` | None: the process's Windows account | Azure DevOps Server |

`<TARGET>` is the target name upper-cased, with every character other than a letter or digit replaced by `_`: target `ado-onprem` reads `SETPOINT_ADO_ONPREM_TOKEN`.

Every token is masked in logs and `--record` fixtures, and a plan file never holds one. It records only each target's sign-in method and identity.
