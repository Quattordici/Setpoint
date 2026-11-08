# Azure DevOps branch policy APIs for the candidate neutral rule set

Research for GitHub issue #3 (Quattordici/Setpoint). Question: for each rule in the candidate neutral set, which Azure DevOps (ADO) REST endpoints and policy type ids implement it, and from which api-version and Azure DevOps Server release is each available? Also: how a client detects the server's API version at start-up, and what `Microsoft.TeamFoundationServer.Client` covers compared with raw REST.

Researched 2026-11-08. Sources are Microsoft Learn (REST reference, product docs, release notes, .NET API browser), NuGet.org package metadata, and Microsoft-owned source on GitHub (`Azure/azure-devops-cli-extension`, `microsoft/terraform-provider-azuredevops`, `microsoft/azure-devops-node-api`). Anything marked **Unconfirmed** could not be traced to a primary source in this session.

Note on access: `devblogs.microsoft.com` could not be reached from this session (blocked by the network proxy), so the official blog post on Git ACL tokens was not read directly. The token format below comes from Microsoft's own Terraform provider source instead.

## Background: api-version to Server release mapping

The REST API reference publishes this mapping. A REST version works on the Server release listed **and on every newer release** ([REST API reference, "API and TFS version mapping"](https://learn.microsoft.com/en-us/rest/api/azure/devops/?view=azure-devops-rest-7.1#api-and-tfs-version-mapping)):

| Server release | REST api-version | Build (>=) |
| --- | --- | --- |
| Azure DevOps Server vNext | 7.2 | (none listed) |
| Azure DevOps Server 2022.1 | 7.1 | 19.225.34309.2 |
| Azure DevOps Server 2022 | 7.0 | 19.205.33122.1 |
| Azure DevOps Server 2020 | 6.0 | 18.170.30525.1 |
| Azure DevOps Server 2019 | 5.0 | 17.143.28621.4 |
| TFS 2018 Update 2 / 3 | 4.1 | 16.131.x |
| TFS 2017 Update 2 | 3.2 | 15.117.26714.0 |

- The current unversioned "Azure DevOps Server" (RC released 2025-10-07) is newer than 2022.1. Its release notes describe a Repos API change as "available on the API versions 7.2-preview and higher" ([Azure DevOps Server release notes](https://learn.microsoft.com/azure/devops/server/release-notes/azuredevopsserver?view=azure-devops)). The 26H2 notes link the Git policy-configurations API at `view=azure-devops-rest-7.2` ([26H2 release notes](https://learn.microsoft.com/azure/devops/server/release-notes/azuredevopsserver26h2?view=azure-devops#repos)). **Unconfirmed**: whether the released version of this Server is exactly `7.2` or only `7.2-preview`. The mapping table still says "vNext → 7.2".
- Server 2019 Update 1 is missing from the table. **Unconfirmed**: which api-version it maps to (probably 5.1).

## Per-rule summary

All four PR-gate rules use one family of endpoints, the **Policy Configurations** API. Each rule is a different policy *type* (a GUID) with its own `settings` JSON:

- `POST/GET https://{instance}/{project}/_apis/policy/configurations[/{id}]?api-version=…`
- `PUT` and `DELETE` on `…/configurations/{configurationId}`

Each configuration's `settings.scope[]` holds `repositoryId`, `refName` (for example `refs/heads/main`) and `matchKind` (`exact` or `prefix`) ([Configurations - Create, 7.1](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-7.1)). The same endpoint and examples appear in the 5.0 reference, which corresponds to Server 2019 ([Configurations - Create, 5.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-5.0)). `isEnterpriseManaged` appears on the object in 7.1 but not in 5.0 (same two pages).

The fifth rule, default-branch protection, is not a policy. It is a Git **security ACL** set through the Security API.

### 1. Minimum reviewers

| Item | Value |
| --- | --- |
| Endpoint | `POST {instance}/{project}/_apis/policy/configurations` ([ref](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-7.1)) |
| Policy type id | `fa4e907d-c16b-4a4c-9dfa-4906e5d171dd`, display name "Minimum approval count" ([ref example](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-7.1); also [az CLI source](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/policy.py) and [Terraform provider `common.go`](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/service/policy/branch/common.go)) |
| Key settings | `minimumApproverCount`, `creatorVoteCounts` (in the REST example); `allowDownvotes`, `resetOnSourcePush` (az CLI); `resetRejectionsOnSourcePush`, `requireVoteOnLastIteration`, `requireVoteOnEachIteration`, `blockLastPusherVote` ([Terraform `resource_branchpolicy_min_reviewers.go`](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/service/policy/branch/resource_branchpolicy_min_reviewers.go)) |
| Earliest api-version | Documented at 5.0 ([ref 5.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-5.0)). The policy itself predates that. "Reset votes on push" arrived in TFS 2018 ([TFS 2018 notes](https://learn.microsoft.com/azure/devops/server/release-notes/tfs2018-relnotes?view=azure-devops)). |
| Earliest Server | Azure DevOps Server 2019 or earlier (works on all target releases) |
| Caveat | **Unconfirmed**: which of the newer flags (`requireVoteOnLastIteration`, `blockLastPusherVote`, `requireVoteOnEachIteration`) exist on Server 2019 and 2020. None of the primary sources found gives a per-flag introduction version. Probe live state, or treat an unknown setting as unsupported. |

### 2. Build validation and status checks

These are two separate policy types.

**Build validation**

| Item | Value |
| --- | --- |
| Endpoint | `POST {instance}/{project}/_apis/policy/configurations` |
| Policy type id | `0609b952-1397-4640-95ec-e00a01b2c241`, display name "Build" ([ref example](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-7.1); [az CLI source](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/policy.py)) |
| Key settings | `buildDefinitionId`, `queueOnSourceUpdateOnly`, `manualQueueOnly`, `displayName`, `validDuration`, `filenamePatterns` ([az CLI source](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/policy.py); [Terraform build validation](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/service/policy/branch/resource_branchpolicy_build_validation.go)) |
| Earliest api-version | Documented at 5.0 ([ref 5.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-5.0)). Multiple builds per branch and manual triggers arrived in TFS 2017 Update 2 ([notes](https://learn.microsoft.com/azure/devops/server/release-notes/tfs2017-update2?view=azure-devops)). Path filters arrived in TFS 2018 Update 2 ([notes](https://learn.microsoft.com/azure/devops/server/release-notes/tfs2018-update2?view=azure-devops#code)). |
| Earliest Server | Server 2019 or earlier |
| Caveat | Needs an ADO pipeline definition id. It cannot point at an arbitrary external CI check. |

**Status check (external service)**

| Item | Value |
| --- | --- |
| Endpoint | `POST {instance}/{project}/_apis/policy/configurations`. Statuses themselves are posted through the PR Status API ([Configure a branch policy for an external service](https://learn.microsoft.com/azure/devops/repos/git/pr-status-policy?view=azure-devops)) |
| Policy type id | `cbdc66da-9728-4af8-aada-9a5a32e4a226` ([Terraform `common.go`, `StatusCheck`](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/service/policy/branch/common.go)). Not in the Learn REST examples and not in the az CLI. **Unconfirmed on Learn**; verify at runtime through `GET _apis/policy/types`. |
| Key settings | `statusName`, `statusGenre`, `authorId`, `invalidateOnSourceUpdate`, `defaultDisplayName`, `filenamePatterns`, `policyApplicability` (1 = conditional) ([Terraform `resource_branchpolicy_status_check.go`](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/service/policy/branch/resource_branchpolicy_status_check.go)) |
| Earliest api-version | Feature introduced in TFS 2018 Update 2, which is REST 4.1 ([TFS 2018.2 notes, "Pull request status extensibility"](https://learn.microsoft.com/azure/devops/server/release-notes/tfs2018-update2?view=azure-devops#code)) |
| Earliest Server | Server 2019 has it (inherits from TFS 2018.2) |

### 3. Comment resolution

| Item | Value |
| --- | --- |
| Endpoint | `POST {instance}/{project}/_apis/policy/configurations` |
| Policy type id | `c6a1889d-b943-4856-b76f-9e46bb6b0df2`, display name "Comment requirements" ([az CLI source, `create_policy_comment_required`](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/policy.py); [Terraform `common.go`](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/service/policy/branch/common.go); display name from the [branch policies doc](https://learn.microsoft.com/azure/devops/repos/git/branch-policies?view=azure-devops#require-comment-resolution)). The GUID is not in the Learn REST examples. |
| Key settings | Only `scope` (the CLI sends no other settings). Required versus optional is set through `isBlocking`. |
| Earliest api-version | Feature introduced in TFS 2017 Update 2, which is REST 3.2 ([TFS 2017.2 notes, "New policy for no active comments"](https://learn.microsoft.com/azure/devops/server/release-notes/tfs2017-update2?view=azure-devops)) |
| Earliest Server | Server 2019 has it |

### 4. Limit merge types

| Item | Value |
| --- | --- |
| Endpoint | `POST {instance}/{project}/_apis/policy/configurations` |
| Policy type id | `fa4e907d-c16b-4a4c-9dfa-4916e5d171ab`, display name "Require a merge strategy" ([ref example](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-7.1)). It differs from the min-reviewers GUID in only two characters, so do not mix them up. |
| Key settings | Current: `allowSquash`, `allowRebase` (rebase and fast-forward), `allowNoFastForward` (basic merge), `allowRebaseMerge` (semi-linear) ([Terraform merge types](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/service/policy/branch/resource_branchpolicy_merge_types.go); [az CLI](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/policy.py); [branch policies doc](https://learn.microsoft.com/azure/devops/repos/git/branch-policies?view=azure-devops#limit-merge-types)). Legacy: `useSquashMerge` (the only setting in the REST example, at both 5.0 and 7.1), which the CLI marks deprecated. |
| Earliest api-version | Squash-only (`useSquashMerge`) is in the 5.0 docs. The four-way "Limit merge types" arrived in Services Sprint 150 and **Azure DevOps Server 2019 Update 1** ([2019.1 notes](https://learn.microsoft.com/azure/devops/server/release-notes/azuredevops2019u1?view=azure-devops#repos); [Sprint 150](https://learn.microsoft.com/azure/devops/release-notes/2019/sprint-150-update#azure-repos)). |
| Earliest Server | 2019 RTM: squash-only. 2019.1 and later: all four strategies. |
| Caveat | Existing squash-only policies are still enforced and need editing to use the new strategies ([2019.1 notes](https://learn.microsoft.com/azure/devops/server/release-notes/azuredevops2019u1?view=azure-devops#repos)). A reader must understand both settings shapes. |

### 5. Default-branch protection (block force-push and delete)

ADO has no "protect branch" policy. Force-push and branch deletion are both governed by **one** Git permission, so a single deny covers both rules.

| Item | Value |
| --- | --- |
| Endpoint | `POST {instance}/_apis/accesscontrolentries/{securityNamespaceId}` with body `{token, merge, accessControlEntries:[{descriptor, allow, deny}]}` ([Set Access Control Entries, 5.0](https://learn.microsoft.com/en-us/rest/api/azure/devops/security/access-control-entries/set-access-control-entries?view=azure-devops-rest-5.0)). Read with Access Control Lists - Query. Collection-level, not project-level. |
| Namespace | **Git Repositories**, id `2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87` ([Security namespace reference](https://learn.microsoft.com/azure/devops/organizations/security/namespace-reference?view=azure-devops#object-level-namespaces-and-permissions)) |
| Bit | **`ForcePush` = 8**, shown in the UI as "Force push (rewrite history, delete branches and tags)". It "Can force an update to a branch, delete a branch, and modify the commit history of a branch" ([permissions reference](https://learn.microsoft.com/azure/devops/organizations/security/permissions?view=azure-devops#git-repository-object-level); bit values from [`GitRepositoryPermissions` enum](https://learn.microsoft.com/dotnet/api/microsoft.teamfoundation.sourcecontrol.webapi.gitrepositorypermissions?view=azure-devops-dotnet)) |
| Not the bit | `RemoveOthersLocks` (4096) is about branch *locks*, not deletion. There is no separate "delete branch" bit. |
| Related bits to govern | `EditPolicies` 2048, `ManagePermissions` 8192, `PullRequestBypassPolicy` 32768 ("Bypass policies when completing pull requests"), `PolicyExempt` 128 ([enum](https://learn.microsoft.com/dotnet/api/microsoft.teamfoundation.sourcecontrol.webapi.gitrepositorypermissions?view=azure-devops-dotnet); [permissions reference](https://learn.microsoft.com/azure/devops/organizations/security/permissions?view=azure-devops#git-repository-object-level)). **Unconfirmed / inferred**: "Bypass policies when pushing" is stored as `PolicyExempt` (128). The docs say "Exempt from policy enforcement" was split into the two bypass permissions, but the snippet does not name the bit for the push one. |
| Token | `repoV2/{projectId}/{repoId}/refs/heads/{encodedBranch}`. Each `/`-separated segment of the branch name is hex-encoded as **UTF-16LE**, so `main` becomes `6d00610069006e00` ([Terraform `resource_git_permissions.go`, `createGitToken`](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/service/permissions/resource_git_permissions.go); [`converter.EncodeUtf16HexString`](https://github.com/microsoft/terraform-provider-azuredevops/blob/main/azuredevops/internal/utils/converter/converter.go)). Learn defers branch-level tokens to a devblogs post that was not reachable here. |
| Earliest api-version | Documented at 5.0. The namespace and `ForcePush` bit long predate it. Granular Git permissions date from 2017 ([namespace reference](https://learn.microsoft.com/azure/devops/organizations/security/namespace-reference?view=azure-devops#object-level-namespaces-and-permissions)). |
| Earliest Server | Server 2019 or earlier |
| Caveats | (a) Branch creators get explicit Allow on Contribute, Force push, Manage permissions and Remove others' locks for the branch they create. These explicit ACEs override inheritance, so `main` may already carry an Allow for its creator that a repo-level deny will not beat ([Set branch permissions](https://learn.microsoft.com/azure/devops/repos/git/branch-permissions?view=azure-devops#set-permissions)). (b) A deny is per identity descriptor, so "nobody may force-push" means writing ACEs for chosen groups (for example Contributors, Project Administrators). |

## Discovering policies and type ids at runtime

- `GET {instance}/{project}/_apis/policy/types` lists the policy types available ([Policy overview](https://learn.microsoft.com/rest/api/azure/devops/policy/?view=azure-devops-rest-7.1)). Use it to confirm that the hard-coded GUIDs exist on a given server, especially Status `cbdc66da-…`.
- `GET {instance}/{project}/_apis/git/policy/configurations?repositoryId=&refName=&policyType=` returns the configurations that *apply* to a repo or branch, with paging through `x-ms-continuationtoken` ([Git Policy Configurations - Get, 7.1](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/policy-configurations/get?view=azure-devops-rest-7.1)). This is the natural "read live state" call for one repo. **Unconfirmed**: its earliest version. The 5.0 URL serves the 7.1 page. The 26H2 Server notes describe improvements to it ([26H2 notes](https://learn.microsoft.com/azure/devops/server/release-notes/azuredevopsserver26h2?view=azure-devops#repos)). Fall back to project-wide `GET _apis/policy/configurations` and filter `settings.scope` on the client when it is missing.

## Detecting the server's API version at start-up

1. **Per-area `OPTIONS` (what the official clients do).** Send `OPTIONS {collectionUrl}/_apis/{area}`, for example `_apis/policy`, `_apis/git` or `_apis/accesscontrolentries`. It returns `value[]` of `ApiResourceLocation`, each with `id`, `area`, `resourceName`, `routeTemplate`, `minVersion`, `maxVersion`, `releasedVersion` and `resourceVersion` ([ApiResourceLocation](https://learn.microsoft.com/javascript/api/azure-devops-extension-api/apiresourcelocation)). The Node client does exactly this in `beginGetAreaLocations`. It then negotiates: it uses the requested version if it is ≤ `releasedVersion`, and otherwise downgrades to `maxVersion` or `maxVersion-preview.{resourceVersion}` ([azure-devops-node-api `VsoClient.ts`](https://github.com/microsoft/azure-devops-node-api/blob/master/api/VsoClient.ts)). The .NET `VssHttpClientBase` has an equivalent `NegotiateRequestVersionAsync` (the symbol is present in `Microsoft.VisualStudio.Services.WebApi.dll` 20.256.2, checked from the NuGet package). Its behaviour is assumed to match the Node client. **Unconfirmed**: not read in source.
2. **`GET {collectionUrl}/_apis/connectionData`** returns `ConnectionData`, including `deploymentType` (Hosted or OnPremises), instance id and the authenticated user ([Node `WebApi.connect()`](https://github.com/microsoft/azure-devops-node-api/blob/master/api/WebApi.ts); [`LocationsInterfaces.ts`](https://github.com/microsoft/azure-devops-node-api/blob/master/api/interfaces/LocationsInterfaces.ts)). It is exposed in .NET as `LocationHttpClient.GetConnectionDataAsync`. Use it to tell cloud from Server. **Unconfirmed**: it is not on Learn's REST reference as a documented endpoint, and whether it exposes the server build number was not verified.
3. **Pin by mapping.** Once `maxVersion` or `releasedVersion` for the `policy` area is known, map it to the Server release with the table above ([REST API reference](https://learn.microsoft.com/en-us/rest/api/azure/devops/?view=azure-devops-rest-7.1#api-and-tfs-version-mapping)). Send that version in `?api-version=` or `Accept: application/json;api-version=…` ([REST API versioning](https://learn.microsoft.com/azure/devops/integrate/concepts/rest-api-versioning?view=azure-devops)). Preview versions are deprecated 12 weeks after release (same page), so do not pin `-preview` against Services.
4. URL shape: Services uses `https://dev.azure.com/{org}`. Server uses `https://{server}[:port]/tfs/{collection}` (or a custom virtual directory) ([REST API reference](https://learn.microsoft.com/en-us/rest/api/azure/devops/?view=azure-devops-rest-7.1)).

## Official .NET client libraries versus raw REST

- **Packages.** `Microsoft.TeamFoundationServer.Client` (Git, Policy, Build, Core and more) depends on `Microsoft.VisualStudio.Services.Client` (platform: identity, security, location) ([.NET client libraries](https://learn.microsoft.com/en-us/azure/devops/integrate/concepts/dotnet-client-libraries?view=azure-devops)). The `20.256.2` nupkg contains `Microsoft.TeamFoundation.Policy.WebApi.dll` (`PolicyHttpClient`: `GetPolicyTypesAsync`, `Create/Update/DeletePolicyConfigurationAsync`) and `Microsoft.TeamFoundation.SourceControl.WebApi.dll` (`GitHttpClient`, including `GetPolicyConfigurationsAsync`). The Services.Client package has `SecurityHttpClient` (`SetAccessControlEntriesAsync`, `QueryAccessControlListsAsync`) and `LocationHttpClient` (`GetConnectionDataAsync`) in `Microsoft.VisualStudio.Services.WebApi.dll`. All of this was checked by listing and string-scanning the NuGet packages. See also [PolicyHttpClient.CreatePolicyConfigurationAsync](https://learn.microsoft.com/dotnet/api/microsoft.teamfoundation.policy.webapi.policyhttpclient.createpolicyconfigurationasync?view=azure-devops-dotnet) and [GitHttpClientBase.GetPolicyConfigurationsAsync](https://learn.microsoft.com/dotnet/api/microsoft.teamfoundation.sourcecontrol.webapi.githttpclientbase.getpolicyconfigurationsasync?view=azure-devops-dotnet-preview).
- **Coverage for this rule set.** The client covers everything needed: policy CRUD and types, Git policy lookup and the Security ACEs. It does **not** type the per-policy settings. `PolicyConfiguration.Settings` is a Newtonsoft `JObject` ([Configurations - Create, definitions](https://learn.microsoft.com/en-us/rest/api/azure/devops/policy/configurations/create?view=azure-devops-rest-7.1)), so Setpoint writes the settings shapes itself either way. Many Policy methods are labelled "[Preview API]" in the .NET docs (same links).
- **Versions and targets (NuGet).** Stable versions are `16.170.x` (Server 2020), `16.205.x` (Server 2022), `19.225.x` and `20.256.2` (published 2026-03-11). Previews run up to `20.281.0-preview` (2026-10-16). Targets are `.NETStandard2.0` plus `.NETFramework4.7.2` (4.6.2 for 16.x). It depends on `Newtonsoft.Json` ≥ 13.0.3 and `Microsoft.AspNet.WebApi.Client` ([NuGet registration for Microsoft.TeamFoundationServer.Client](https://api.nuget.org/v3/registration5-gz-semver2/microsoft.teamfoundationserver.client/index.json); [package page](https://www.nuget.org/packages/Microsoft.TeamFoundationServer.Client/)). Learn's mapping says to use 16.170.x for Server 2020 and later, and 16.205.x for Server 2022 and later ([.NET client libraries](https://learn.microsoft.com/en-us/azure/devops/integrate/concepts/dotnet-client-libraries?view=azure-devops)). **Unconfirmed**: whether the 20.x client negotiates down cleanly against Server 2019 or 2020. Negotiation (above) suggests it should, but this was not tested.
- **Trade-offs for a .NET 10 CLI.** The library is netstandard2.0 and Newtonsoft-based. **Unconfirmed**: trimming and NativeAOT compatibility were not checked (nothing found documents it). Raw REST with `HttpClient` plus `System.Text.Json` needs only: one `OPTIONS` call per area, four policy endpoints, the policy-types endpoint, and two Security endpoints. All of them are small, stable and documented back to 5.0.
- **Not an option for Server.** The Azure DevOps CLI (`az repos policy …`) states "Azure DevOps CLI commands aren't supported for Azure DevOps Server" ([branch policies doc](https://learn.microsoft.com/azure/devops/repos/git/branch-policies?view=azure-devops#set-a-minimum-reviewer-policy)).

## Implications for decisions

1. **Floor at Azure DevOps Server 2019 (REST 5.0).** All five rules work there. The only gap is limit-merge-types, which is squash-only before 2019 Update 1. Either set the floor at 2019.1 or report "only squash restriction supported" on 2019 RTM.
2. **One policy adapter, four type GUIDs.** Minimum reviewers, build validation, status check, comment resolution and merge types share the Policy Configurations endpoint. Model each rule as `(typeId, settings shape)`. Check the GUIDs against `GET _apis/policy/types` at start-up rather than trusting constants, especially Status `cbdc66da-…`, which is sourced only from Microsoft's Terraform provider.
3. **"Build validation and status checks" is two ADO types.** Build validation needs an ADO pipeline id. External checks map to the Status policy (`genre/name`). The neutral rule needs a way to say which one.
4. **Branch protection is ACLs, not policy.** The rule becomes "deny `ForcePush` (bit 8) on `repoV2/{proj}/{repo}/refs/heads/{utf16le-hex}` in namespace `2e9eb7ed-…` for group X". This one bit covers both force-push and delete, so the neutral rule cannot express "block delete but allow force-push" on ADO. Branch creators' explicit ACEs need detecting or overwriting.
5. **Negotiate the version, don't hard-code it.** Do one `OPTIONS _apis/{area}` per area at start-up and pick `min(our max, releasedVersion)`. Use `connectionData` to tell cloud from Server.
6. **Raw REST looks cheaper than the SDK** for this narrow surface (System.Text.Json, no Newtonsoft, AOT-friendly), and the SDK does not type the settings anyway. Before committing, prototype both against a Server 2019 or 2020 instance.
7. **Read old and new settings shapes.** For merge types, handle both `useSquashMerge` (legacy) and the four `allow*` flags. For minimum reviewers, treat unknown flags as unsupported on older servers until verified.
