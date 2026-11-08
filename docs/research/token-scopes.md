# Minimum token permissions for `plan` and `apply`

Research for GitHub issue #19 (Quattordici/Setpoint). Question: for `plan` (read-only) and `apply` (write), what are the minimum token permissions on GitHub and Azure DevOps (ADO) for exactly the calls Setpoint makes? Also: what `System.AccessToken` (the pipeline's build service identity) can do by default, and what it must be granted.

Researched 2026-11-08. Sources:

- **GitHub**: GitHub's own docs source (`github/docs`) and OpenAPI description (`github/rest-api-description`), read from raw.githubusercontent.com because docs.github.com was blocked by the network proxy. The fine-grained PAT and GitHub App tables are generated from `src/github-apps/data/fpt-2022-11-28/*.json` in `github/docs`. Those JSON files are the data behind the published pages, so this doc cites both the published page and the data file.
- **Azure DevOps**: Microsoft Learn (REST reference for api-version 7.0, product docs), read through the Microsoft Learn MCP tools.

Anything marked **Unconfirmed** could not be traced to a primary source in this session.

## Two rules that apply to everything below

1. **A token never grants more than its owner can do.** Choosing a scope or permission only narrows what the identity behind the token can already do.
   - GitHub: a fine-grained PAT can only reach resources that the selected resource owner owns, and an organization can require approval before the PAT works. Until it is approved, the token can only read public resources ([Managing your personal access tokens](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens), [source](https://raw.githubusercontent.com/github/docs/main/content/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens.md)).
   - ADO: "A PAT can't grant more permissions than its owner has" ([Use personal access tokens](https://learn.microsoft.com/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate?view=azure-devops)).
2. So each platform needs **two layers**: a token scope or permission, *and* a role or ACL on the identity. The tables give both.

---

## GitHub

### Role required on the identity

"Anyone with read access to a repository can view the repository's rulesets. People with admin access to a repository, or a custom role with the 'edit repository rules' permission, can create, edit, and delete rulesets for a repository" ([Managing rulesets for a repository](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/managing-rulesets-for-a-repository), [reusable source](https://raw.githubusercontent.com/github/docs/main/data/reusables/repositories/repo-rules-permissions.md)).

### Calls → token permission

FG = fine-grained PAT. App = GitHub App (installation token or user-to-server token). In GitHub's tables, fine-grained PATs and Apps use the same permission names.

| Call | `plan` (read) | `apply` (write) | Notes |
| --- | --- | --- | --- |
| `GET /orgs/{org}/repos` | FG/App: **Metadata: read** | same | [1][2] |
| `GET /users/{username}/repos` | FG/App: **Metadata: read** | same | Lists **public** repos only [3] |
| `GET /user/repos` | FG and App user token: **Metadata: read**. Not available to App installation tokens. | same | `server-to-server: false` in [2] |
| `GET /installation/repositories` | App installation token: no extra permission is listed | same | The App's way to enumerate the repos it can reach [4] |
| `GET /repos/{o}/{r}` (repo, including `allow_*_merge`) | FG/App: **Metadata: read** gets the endpoint, **but** the reference says: "To view merge-related settings, you must have the `contents:read` and `contents:write` permissions." | same | See the gotcha below [1][3] |
| `GET /repos/{o}/{r}/rulesets` and `GET …/rulesets/{id}` | FG/App: **Metadata: read** | same | `bypass_actors` is returned only when the caller has write access to the ruleset [3] |
| `POST /repos/{o}/{r}/rulesets` | none | FG/App: **Administration: write** | [1][2] |
| `PUT /repos/{o}/{r}/rulesets/{id}` | none | FG/App: **Administration: write** | [1][2] |
| `DELETE /repos/{o}/{r}/rulesets/{id}` | none | FG/App: **Administration: write** | [1][2] |

Sources:

- [1] [Permissions required for fine-grained PATs](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens), data: [fine-grained-pat-permissions.json](https://raw.githubusercontent.com/github/docs/main/src/github-apps/data/fpt-2022-11-28/fine-grained-pat-permissions.json)
- [2] [Permissions required for GitHub Apps](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps), data: [server-to-server-permissions.json](https://raw.githubusercontent.com/github/docs/main/src/github-apps/data/fpt-2022-11-28/server-to-server-permissions.json)
- [3] OpenAPI descriptions in [api.github.com.json](https://raw.githubusercontent.com/github/rest-api-description/main/descriptions/api.github.com/api.github.com.json), published as [Repos](https://docs.github.com/en/rest/repos/repos) and [Rules](https://docs.github.com/en/rest/repos/rules)
- [4] [server-to-server-rest.json](https://raw.githubusercontent.com/github/docs/main/src/github-apps/data/fpt-2022-11-28/server-to-server-rest.json) (endpoints available to installation tokens)

Neither data file lists `GET` on repo rulesets under `administration`. The only ruleset reads listed are under `metadata`. The org-level `GET /orgs/{org}/rulesets` is different: it is listed under **Organization administration: write** [1]. Setpoint does not call it, but it matters if `includes_parents` results are ever traced back to their org ruleset.

### Gotcha 1: merge settings need Contents read *and write*

The "Get a repository" description says merge-related settings (`allow_squash_merge`, `allow_merge_commit`, `allow_rebase_merge`, `allow_auto_merge`, `delete_branch_on_merge`, `allow_update_branch`, …) need "`contents:read` and `contents:write`" [3]. These fields are optional in the `full-repository` schema (none are in `required`) [3]. A token without that permission therefore gets a 200 response that silently leaves the fields out, rather than a 403.

- **Unconfirmed**: whether Administration: read or write also unlocks these fields. The docs name only Contents.
- **Unconfirmed**: the classic-PAT equivalent. Probably the user needs push access plus the `repo` scope, but no source states it.

### Gotcha 2: `bypass_actors` is hidden from read-only callers

"To prevent leaking sensitive information, the `bypass_actors` property is only returned if the user making the API request has write access to the ruleset" [3]. A truly read-only `plan` therefore cannot see bypass actors, and would show a false diff if the policy declares any.

- **Unconfirmed**: whether "write access to the ruleset" means the token needs Administration: write, or only that the user holds the admin or edit-rules role. The ruleset schema also has `current_user_can_bypass`, which is returned only on the repo-level endpoint [3].

### Classic PATs (and OAuth apps)

| Need | Classic scope | Status |
| --- | --- | --- |
| Read and write private repos, including settings | `repo`: "Grants full access to public, internal, and private repositories…" [5] | Documented |
| Public repos only | `public_repo` [5] | Documented, but not stated per ruleset endpoint |
| Ruleset `GET`/`POST`/`PUT`/`DELETE` | Not stated in the ruleset endpoint descriptions [3]. Other endpoints say "OAuth app tokens and personal access tokens (classic) need the X scope". Ruleset endpoints carry no such line. | **Unconfirmed**. Inferred: `repo` for private, `public_repo` (write) or no scope (read) for public. |
| `admin:org` | Needed for repo-level rulesets? Not stated. `admin:org` is "Fully manage the organization and its teams, projects, and memberships" [5]. | **Unconfirmed**. Probably needed only for org rulesets, which Setpoint does not call. |
| Listing org repos | No scope needed for public repos (no-scope tokens get "read-only access to public information") [5]. `repo` is needed to see private repos. | Documented |

[5] [Scopes for OAuth apps](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps), [source](https://raw.githubusercontent.com/github/docs/main/content/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps.md). The same page documents the `X-OAuth-Scopes` and `X-Accepted-OAuth-Scopes` response headers. A one-off live call against each ruleset endpoint would settle the unconfirmed rows. For fine-grained PATs and Apps, the matching header is `X-Accepted-GitHub-Permissions` ([source](https://raw.githubusercontent.com/github/docs/main/data/reusables/rest-api/permission-header.md)).

Fine-grained PAT limitations that matter here ([source](https://raw.githubusercontent.com/github/docs/main/data/reusables/user-settings/patv2-limitations.md)):

- Only classic PATs work for outside collaborators.
- Only classic PATs get write access to public repos owned by someone else.
- On GHEC/GHES, only classic PATs automatically get write access to internal repos.

### `GITHUB_TOKEN` (Actions)

The `permissions:` key accepts only: `actions`, `artifact-metadata`, `attestations`, `checks`, `code-quality`, `contents`, `copilot-requests`, `deployments`, `id-token`, `issues`, `discussions`, `packages`, `pages`, `pull-requests`, `security-events`, `statuses`, `vulnerability-alerts`. **There is no `administration`** ([source](https://raw.githubusercontent.com/github/docs/main/data/reusables/actions/github-token-available-permissions.md)). So `GITHUB_TOKEN` can run `plan` against its own repo (Metadata is implicit, and `contents: write` exposes merge settings). It **cannot** run `apply`, because ruleset writes need Administration: write. A GitHub App or a PAT is required for `apply` in CI.

### GitHub summary: minimum sets

| Mode | Fine-grained PAT / GitHub App | Classic PAT (inferred, see above) |
| --- | --- | --- |
| `plan` (rulesets only) | Metadata: read | `repo` (private) / none (public) |
| `plan` (also reads merge settings) | Metadata: read + **Contents: write** | `repo` |
| `plan` (also sees `bypass_actors`) | probably **Administration: write** (**Unconfirmed**) | `repo` + admin role |
| `apply` | Metadata: read + Administration: write (+ Contents: write if merge settings are read) | `repo` + admin role (or a custom role with "edit repository rules") |

---

## Azure DevOps

### Calls → PAT scope (api-version 7.0)

Scopes are taken from the "Security → Scopes" section of each 7.0 REST reference page. The scope names and the inheritance between them come from the [OAuth scopes table](https://learn.microsoft.com/azure/devops/integrate/get-started/authentication/oauth?view=azure-devops#available-scopes):

- `vso.code_write` inherits `vso.code`.
- `vso.code_manage` inherits `vso.code_write`.
- `vso.security_manage` is the only Security scope. There is no read-only security scope.

| Call | Scope listed | `plan` | `apply` | Source |
| --- | --- | --- | --- | --- |
| `GET _apis/git/repositories` | `vso.code` | vso.code | vso.code | [Repositories - List 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/repositories/list?view=azure-devops-rest-7.0) |
| `GET _apis/policy/configurations` | `vso.code` | vso.code | vso.code | [Configurations - List 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/list?view=azure-devops-rest-7.0) |
| `GET _apis/policy/configurations/{id}` | `vso.code` | vso.code | vso.code | [Configurations - Get 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/get?view=azure-devops-rest-7.0) |
| `POST _apis/policy/configurations` | `vso.code_write` | none | vso.code_write | [Configurations - Create 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-7.0) |
| `PUT _apis/policy/configurations/{id}` | `vso.code_write` | none | vso.code_write | [Configurations - Update 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/update?view=azure-devops-rest-7.0) |
| `DELETE _apis/policy/configurations/{id}` | `vso.code_write` | none | vso.code_write | [Configurations - Delete 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/delete?view=azure-devops-rest-7.0) |
| `GET _apis/policy/types` | `vso.code` | vso.code | vso.code | [Types - List 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/types/list?view=azure-devops-rest-7.0) |
| `GET _apis/accesscontrollists/{ns}` (read Git ACLs) | **No OAuth scope listed.** The page shows only "accessToken / basic" (PAT). | `vso.security_manage` (**Unconfirmed**), or full scope on Server 2022 RTM (see below) | same | [Access Control Lists - Query 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/security/access-control-lists/query?view=azure-devops-rest-7.0) |
| `POST _apis/accesscontrolentries/{ns}` | `vso.security_manage` | none | vso.security_manage | [Access Control Entries - Set 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/security/access-control-entries/set-access-control-entries?view=azure-devops-rest-7.0) |
| `DELETE _apis/accesscontrolentries/{ns}` | `vso.security_manage` | none | vso.security_manage | [Access Control Entries - Remove 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/security/access-control-entries/remove-access-control-entries?view=azure-devops-rest-7.0) |
| `GET _apis/build/definitions?name=…` | `vso.build` | vso.build | vso.build | [Definitions - List 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/build/definitions/list?view=azure-devops-rest-7.0) |

`vso.code_manage` is **not** needed. Policy writes are listed under `vso.code_write`, and `_manage` adds only repository create/manage ([OAuth scopes](https://learn.microsoft.com/azure/devops/integrate/get-started/authentication/oauth?view=azure-devops#available-scopes)). `vso.project` is also not needed for any call above, as long as Setpoint already knows the project name. Listing projects (`_apis/projects`) would need it, but that call is out of scope here.

**Server 2022 RTM and granular scopes.** The Azure DevOps Server 2022 **Update 1** release notes say: "Previously, a number of publicly documented Azure DevOps REST APIs were not associated with scopes … that led to customers using full scopes … Now, all public Azure DevOps REST APIs are now associated with and support a granular PAT scope" ([2022.1 release notes, General](https://learn.microsoft.com/azure/devops/server/release-notes/azuredevops2022u1?view=azure-devops#general)). On Server 2022 RTM (api-version 7.0), an API with no scope in its 7.0 page, like ACL Query above, may therefore reject every scoped PAT and require a **Full access** PAT. **Unconfirmed**: whether that holds for ACL Query in particular. It needs a live test on 2022 RTM. The 7.1 page was not checked in this session.

### Permissions required on the identity (Git Repositories namespace)

The namespace is `2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87`. Tokens are `repoV2/{projectId}` and `repoV2/{projectId}/{repoId}` ([Security namespace reference](https://learn.microsoft.com/azure/devops/organizations/security/namespace-reference?view=azure-devops#object-level-namespaces-and-permissions)). The permission bits are `GenericRead`=2, `EditPolicies`=2048 and `ManagePermissions`=8192 ([GitRepositoryPermissions enum](https://learn.microsoft.com/dotnet/api/microsoft.teamfoundation.sourcecontrol.webapi.gitrepositorypermissions?view=azure-devops-dotnet)).

| Operation | Permission needed | Source |
| --- | --- | --- |
| List repos, read policies and types | **Read** (`GenericRead`) on the repo (and project visibility) | [Permissions reference, Git repository](https://learn.microsoft.com/azure/devops/organizations/security/permissions?view=azure-devops#git-repository-object-level) |
| Create/update/delete branch policies | **Edit policies** (`EditPolicies`) on the repo or branch, or Project Administrators membership | [Repository settings, prerequisites](https://learn.microsoft.com/azure/devops/repos/git/repository-settings?view=azure-devops#prerequisites) |
| Set/remove ACEs on repo or branch | **Manage permissions** (`ManagePermissions`) on the repo or branch | same page; [Permissions reference](https://learn.microsoft.com/azure/devops/organizations/security/permissions?view=azure-devops#git-repository-object-level) |
| Read ACLs | **Unconfirmed**. Each namespace declares its own `readPermission` bit (visible via `GET _apis/securitynamespaces/{id}`). For Git this is probably `GenericRead`, but no doc says so. | [Namespace reference](https://learn.microsoft.com/azure/devops/organizations/security/namespace-reference?view=azure-devops) |
| Policies with `isEnterpriseManaged` | "requires 'Manage Enterprise Policies' permission to create, edit, or delete" | [Configurations - Create 7.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-7.0) |
| Look up build definitions by name | View permission on build pipelines (**Unconfirmed** as to the exact name and default) | none found |

By default, only **Project Administrators** have Edit policies and Manage permissions. Readers have Read. Contributors and Build Administrators have Read, Contribute, Create branch, Create tag and Manage notes ([Default Git permissions](https://learn.microsoft.com/azure/devops/organizations/security/default-git-permissions?view=azure-devops)).

The branch-creator nuance: a user who creates a branch on the server used to get Edit policies and Manage permissions on that branch automatically ([Permissions reference](https://learn.microsoft.com/azure/devops/organizations/security/permissions?view=azure-devops#git-repository-object-level)). From Azure DevOps sprint 224, and from **Server 2022.1**, branch creators no longer get Edit policies automatically ([Set Git repository permissions](https://learn.microsoft.com/azure/devops/repos/git/set-git-repository-permissions?view=azure-devops#review-default-repository-permissions)). Server 2022 **RTM** therefore still has the old behaviour.

### ADO summary: minimum sets

| Mode | PAT scopes | Identity permissions |
| --- | --- | --- |
| `plan` | `vso.code` + `vso.build` + `vso.security_manage` (no read-only security scope exists). On Server 2022 RTM, possibly Full access (**Unconfirmed**). | Read on repos; ACL read (**Unconfirmed** bit); view build pipelines |
| `apply` | `vso.code_write` + `vso.build` + `vso.security_manage` | + **Edit policies** + **Manage permissions** on each target repo (or on the top-level "Git repositories" node so that it is inherited) |

If Setpoint drops ACL reads from `plan` (or makes them optional), `plan` drops to `vso.code` + `vso.build`. That pair is the only truly low-privilege set.

---

## System.AccessToken (ADO pipelines)

**What it is.** `System.AccessToken` is "a special variable that carries the security token used by the running build". In YAML it must be mapped explicitly into a step's `env` ([Predefined variables](https://learn.microsoft.com/azure/devops/pipelines/build/variables?view=azure-devops#systemaccesstoken)). It is a **job access token**. Its permissions come from "(a) job authorization scope and (b) the permissions you set on project or collection build service account" ([Job access tokens](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/access-tokens?view=azure-devops)). That page applies to Services, Server 2022 and Server.

**Which identity it runs as** (same page):

- **Collection scope**: `Project Collection Build Service ({OrgName})`. This is the default unless "Limit job authorization scope to current project" is turned on at org or project level.
- **Project scope**: `{Project Name} Build Service ({OrgName})`. Public projects are always project scope.
- For new classic pipelines, the build job authorization scope defaults to **current project**.
- For **YAML**, "If the scope is not restricted at either the organization level or project level, then every job in your YAML pipeline gets a collection scoped job access token."
- **Protect access to repositories in YAML pipelines** (on by default for organizations and projects created after May 2020) further limits the token to repos referenced by a `checkout` or `uses` step. **Unconfirmed**: whether this also affects policy and ACL REST calls on other repos, or only Git access.

**What it can do by default.**

- On Git repos, the project build service "can read from the repo but can't push any changes to it" ([Run Git commands in pipeline scripts](https://learn.microsoft.com/azure/devops/pipelines/scripts/git-commands?view=azure-devops#enable-scripts-to-run-git-commands)).
- Nothing in the defaults grants Edit policies or Manage permissions, which only Project Administrators hold by default ([Default Git permissions](https://learn.microsoft.com/azure/devops/organizations/security/default-git-permissions?view=azure-devops)).
- So by default `System.AccessToken` can at most do the read side of `plan`: list repos and read policies/types, plus probably build definitions (**Unconfirmed**). ACL reads are unknown.

**What it must be granted for `apply`.** Go to Project settings → Repositories → (All repositories or a specific repo) → Security → select the build service *user*, not the "Project Collection Build Service Accounts" group ([git-commands page](https://learn.microsoft.com/azure/devops/pipelines/scripts/git-commands?view=azure-devops#enable-scripts-to-run-git-commands)). Set **Edit policies** and **Manage permissions** to Allow.

- Grant it on the identity that matches the job authorization scope ([Job access tokens, Manage build service account permissions](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/access-tokens?view=azure-devops)).
- The project-scoped identity "will only be created after you run the pipeline once".
- For repos in other projects, the project-scoped identity first needs "View project-level information" in that project (same page).

**Scopes on the job token.** **Unconfirmed**: no page found says whether the job access token is limited by OAuth scopes (like `vso.code_write` / `vso.security_manage`), or only by the identity's ACLs. Microsoft documents it purely in terms of identity permissions and job authorization scope. Treat ACLs as the control and test the `accesscontrolentries` write in a pipeline.

**Server 2022 vs cloud.** The job-access-token page and the git-commands page both state that they apply to Azure DevOps Server 2022. The differences found:

1. The sprint 224 / 2022.1 branch-creator change (above). It does not affect a build identity that never creates branches.
2. The 2022 RTM gap in granular REST scopes (above). This matters for PATs. Whether it affects job tokens is **Unconfirmed**.

---

## Implications for decisions

1. **On GitHub, `plan` is not truly read-only if it must see everything.**
   - Reading rulesets needs only Metadata: read.
   - Reading `allow_*_merge` needs Contents: **write**.
   - Seeing `bypass_actors` needs write access to the ruleset.
   - Options: (a) document a "plan token" that already holds write permissions; (b) have `plan` report "unknown" or "not visible" for merge settings and bypass actors when the response leaves them out. Never treat a missing field as `false` or empty, or plan will show false drift. Option (b) is worth considering either way, because the response is a silent 200 without the fields.
2. **`apply` on GitHub needs a GitHub App or a PAT, not `GITHUB_TOKEN`.** `GITHUB_TOKEN` has no `administration` permission. Recommend a GitHub App with Metadata: read, Administration: write and Contents: write, installed only on the target repos. Enumerate with `GET /installation/repositories` (installation tokens can't call `/user/repos`).
3. **Classic PAT scopes for rulesets are unconfirmed.** Before documenting them, make one live call per endpoint and record `X-Accepted-OAuth-Scopes`.
4. **On ADO, ACL reads force a high-privilege scope even for `plan`.** No read-only Security scope exists, and ACL Query lists no OAuth scope at 7.0. Consider making default-branch-protection (ACL) checks a separate, opt-in part of `plan`, so that the common `plan` needs only `vso.code` + `vso.build`.
5. **ADO `apply` minimum**: `vso.code_write` + `vso.build` + `vso.security_manage`, plus the Edit policies and Manage permissions ACLs. `vso.code_manage` is unnecessary. Plan for a live check on Server 2022 RTM of whether a scoped PAT works for ACL Query at all, or whether Full access is required.
6. **`System.AccessToken` works for `apply` only after an admin grants Edit policies and Manage permissions** to the correct build service identity, which depends on the job authorization scope. Setpoint's docs should say which identity to grant, and should suggest granting it at the "Git repositories" (project) level for inheritance. Setpoint could also detect a 401/403 and print that guidance.
7. **Granting Manage permissions to a build identity is an escalation risk.** A pipeline that can edit Git ACLs can grant itself bypass permissions. Recommend a dedicated, restricted pipeline for `apply`, and say so in the security docs.
