# GitHub

Setpoint manages one [repository ruleset](https://docs.github.com/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets) per repo, named `setpoint`, active on the default branch (`~DEFAULT_BRANCH`). It never writes classic branch protection, other rulesets or the repo's own settings. Those are read only to report them as unmanaged and to warn when they are stricter than the policy.

## Targets

```yaml
targets:
  github:
    platform: github
    scope: [acme/*]          # required on GitHub
    exclude: [acme/*-sandbox]
    includeForks: false      # forks are skipped unless true
```

- With no `url:`, the target is github.com. Any other `url:` is treated as GitHub Enterprise Server, whose API lives at `<url>/api/v3/`.
- Every call sends `X-GitHub-Api-Version: 2022-11-28`.
- The token comes from `SETPOINT_<TARGET>_TOKEN` (the target name upper-cased, with every character other than a letter or digit replaced by `_`), or from the variable named in `tokenEnv:`. The preflight calls `GET /user` and reports the signed-in account as the identity.
- With `auth: app`, Setpoint signs in as a GitHub App (recommended for organizations). See below.

### GitHub App

```yaml
targets:
  github:
    platform: github
    auth: app
    appId: 123456
    scope: [acme/*]
```

- The App's private key (the PEM file GitHub gives you) goes in `SETPOINT_<TARGET>_APP_KEY`. PKCS#1 and PKCS#8 both work, and so does a key whose newlines a pipeline secret turned into `\n`.
- Setpoint signs an RS256 JWT with the .NET runtime's own cryptography, finds the App's installation on each `scope:` owner (`GET /orgs/{owner}/installation`, or `/users/{owner}/installation`), and mints an installation token for it. A wildcard owner uses every installation (`GET /app/installations`).
- Installation tokens last an hour. Setpoint mints a new one five minutes before expiry, so a long run doesn't fail half-way.
- Repos are listed through `GET /installation/repositories`, so only repos the App was granted are seen.
- The identity in the plan header is the App's bot account, such as `setpoint-governance[bot]`.
- The key, the JWTs and the tokens are registered with the redactor and never appear in logs or `--record` fixtures.
- The App needs Metadata: read and Administration: read for `plan`, Contents: read to check the merge flags, and Administration: write for `apply`.

## Listing

Each `scope:` owner is listed as an organization (`GET /orgs/{owner}/repos`), or as a user when it isn't one. Owners that contain a wildcard list every repo the token can reach (`GET /user/repos`). Only repos in `scope:` and not in `exclude:` are kept.

| Repo | Result |
|---|---|
| Archived or disabled | Skipped, `SP0203` (info) |
| A fork, without `includeForks: true` | Skipped, `SP0205` (info) |
| Empty (no branches) | Skipped, `SP0204` (warning) |
| Private, on GitHub Free | Skipped, `SP3001` (warning) |

A private repo is tested by asking for its rulesets. On GitHub Free that answers 403 and asks to upgrade, whoever owns the repo.

## Mapping

| Rule | `setpoint` ruleset |
|---|---|
| `minimumReviewers: { count, resetOnPush }` | `pull_request.required_approving_review_count`, `dismiss_stale_reviews_on_push` |
| `requiredChecks: [ { name } ]` | `required_status_checks[].context` |
| `requireCommentResolution` | `pull_request.required_review_thread_resolution` |
| `allowedMergeTypes` | `pull_request.allowed_merge_methods` |
| `protectFromForcePushAndDelete` | the `non_fast_forward` and `deletion` rules |
| `github.pullRequest` | `require_code_owner_review`, `require_last_push_approval` |
| `github.requiredChecks` | `strict_required_status_checks_policy`, and `integration_id` per check |
| `github.bypassActors` | `bypass_actors` |

- A `pull_request` rule requires a pull request, so Setpoint only writes one when a rule asks for it.
- Rules the policy leaves unmanaged keep their live values when the ruleset is rewritten, and so do rule types Setpoint doesn't manage, such as `required_signatures`.
- A `setpoint` ruleset that is disabled, or that doesn't target exactly the default branch, reads as carrying no rules. The next apply makes it active on the default branch again.
- The fingerprint leaves out `_links`, `current_user_can_bypass` and `bypass_actors`, because they depend on who reads the ruleset, and plan and apply use different tokens. Any edit still moves `updated_at`, so the stale check catches it.

### Team access

`access` and `github.access` aren't in the ruleset. Each team the policy names is its own object, matched by team slug (case doesn't matter):

```yaml
rules:
  access:
    platform: admin
    payments: write
    contractors: none
github:
  access:
    support: triage
```

| Role | GitHub permission |
|---|---|
| `read` | `pull` |
| `write` | `push` |
| `admin` | `admin` |
| `github.access: triage` or `maintain` | `triage` or `maintain` |
| `none` | the repo is removed from the team |

- Only the teams named are managed. Other teams with access to the repo are listed as unmanaged, and direct collaborators aren't read at all.
- A live `triage`, `maintain` or custom role reads as its own name under `access`, so `platform: write` on a team that has `maintain` is a change, not in sync.
- A team in both `access` and `github.access`, a slug the organization doesn't have, or any team on a repo owned by a user, fails that repo's plan with `SP0207`. Slugs are only checked when the token may list the organization's teams; otherwise a wrong one fails at apply with a 404.
- A lower layer replaces the whole `access` map, like every other rule. A team named only in the baseline is unmanaged on a repo whose override sets `access`, not removed; use `none` to remove it.

### Environments

`environments` and `github.environments` aren't in the ruleset either. Each deployment environment the policy names is its own object, matched by name (case doesn't matter):

```yaml
rules:
  environments:
    production:
      reviewers: [platform, release-managers]   # team slugs
      waitMinutes: 15
      branches: default
    staging:
      enabled: false
github:
  environments:
    production:
      users: [mona]
      preventSelfReview: true
```

| Field | GitHub |
|---|---|
| `reviewers` | required reviewers of type `Team`, by slug |
| `github.environments.<name>.users` | required reviewers of type `User`, by login |
| `github.environments.<name>.preventSelfReview` | `prevent_self_review` |
| `waitMinutes` | `wait_timer`, 0 to 43200 |
| `branches: default` | `deployment_branch_policy: { protected_branches: true }`: only branches with branch protection or a ruleset may deploy |
| `branches: any` | no deployment branch policy |

- A field the policy leaves out keeps its live value. Setting only `reviewers` keeps an environment's user reviewers, its wait and its custom branch policies. A live custom branch policy reads as `branches: custom`, which the policy can't set but can replace.
- A missing environment is created with what the policy declares. `enabled: false` clears the reviewers, the wait and the branch policy and keeps the environment; a disabled environment that doesn't exist isn't created. Setpoint never deletes an environment, and never touches its secrets, variables or custom deployment protection rules.
- Environments the policy doesn't name are listed as unmanaged.
- A team the organization doesn't have, a user GitHub doesn't have, or any team on a repo owned by a user, fails the repo's plan with `SP0207`. More than six reviewers, teams and users together, is `SP0121`.
- Required reviewers and wait timers on private repos need GitHub Enterprise, and a token can't tell an Enterprise plan from the others. So on a private repo, environments are skipped with `SP3002` and the rest of the repo is planned. (Private repos on GitHub Free are already skipped whole, with `SP3001`.)
- A lower layer replaces the whole `environments` map, like every other rule.

## Apply

`apply` writes the `setpoint` ruleset: `POST /repos/{owner}/{repo}/rulesets` to create it, `PUT .../rulesets/{id}` to update it, and `DELETE .../rulesets/{id}` when the policy leaves it no rule. It writes a named team's access with `PUT /orgs/{org}/teams/{slug}/repos/{owner}/{repo}` and the planned `{ "permission": ... }`, or removes it with `DELETE` on the same path. It writes an environment with `PUT /repos/{owner}/{repo}/environments/{name}`, which creates it or replaces its reviewers, wait timer and branch policy, and never deletes one. The planned payload is sent exactly as the plan holds it, and nothing is recomputed. The adapter refuses any planned object that is neither, so other rulesets, classic branch protection and repo settings are never written.

A created ruleset has no bypass actors unless the policy declares `github.bypassActors`. An existing ruleset keeps the bypass actors it has while the policy leaves them undeclared, because they are reported as unmanaged rather than removed silently.

A failed write fails that repo, and the repo's later writes are not attempted. A 403 names the permission the token needs: Administration: write, or the `repo` scope on a classic token. Team access also needs Members: read on the organization (`read:org` on a classic token), and an account that is an organization owner or a maintainer of the team with admin on the repo.

## Unmanaged settings

- **Other rulesets** that apply to the default branch, including organization rulesets, are read from `GET /repos/{owner}/{repo}/rules/branches/{branch}`. Each rule is reported under the neutral rule it bears on, or as `github.<type>` when it has none. A stricter one raises `SP2101`.
- **Repo merge settings** (`allow_merge_commit`, `allow_squash_merge`, `allow_rebase_merge`) are reported when they don't allow every method. When they and `allowedMergeTypes` leave no method in common, plan fails with `SP2102`, because no pull request could complete.

## What a read-only token can't see

GitHub leaves some fields out of a 200 response rather than refusing the call:

- `bypass_actors` are only shown to callers with write access. Without them, `github.bypassActors` is reported as unknown (`SP2202`) and not changed.
- The repo merge flags are only shown with Contents read. Without them there is nothing to compare, so `SP2102` can't be checked.
- A repo's teams may not be listable with a read-only token (403). Then `access` and `github.access` are reported as unknown (`SP2202`) and not changed.
- The same goes for a repo's environments: if the token may not list them, `environments` and `github.environments` are unknown.

## Token permissions

| Command | Fine-grained PAT or App | Classic PAT |
|---|---|---|
| `plan` | Metadata: read. Add Contents: read to check the merge flags, Administration: read for bypass actors and the repo's teams (unconfirmed, see below), and Members: read on the organization to check team slugs. | `repo` per `X-Accepted-OAuth-Scopes`, plus `read:org` for team slugs; `public_repo` is unconfirmed for public repos |
| `plan`, for `environments` | Actions: read or Environments: read to list environments (unconfirmed which), and Members: read on the organization to resolve team reviewers | `repo`, plus `read:org` for team reviewers |
| `apply` | Administration: write, plus a role that may edit repository rules. For `access`, also Members: read, and an organization owner or team maintainer. For `environments`, also Members: read | `repo`, plus `read:org` for `access` and `environments` |

Calls made on 2026-11-08 against `Quattordici/Setpoint` answered `X-Accepted-GitHub-Permissions: metadata=read` and `X-Accepted-OAuth-Scopes: repo` for `GET /repos/{owner}/{repo}`, `/rulesets` and `/rules/branches/{branch}`. Two questions need a live call with a dedicated token:

1. Does a classic PAT with only `public_repo` read rulesets on a public repo?
2. Does a fine-grained PAT with Administration: read (and no write) see `bypass_actors`?
3. Which fine-grained permission lists environments, and does `PUT .../environments/{name}` with `reviewers: []` and `deployment_branch_policy: null` clear them, as Setpoint assumes for `enabled: false`?
