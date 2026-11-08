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
- `auth: app` arrives in a later build.

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

## Apply

`apply` writes only the `setpoint` ruleset: `POST /repos/{owner}/{repo}/rulesets` to create it, `PUT .../rulesets/{id}` to update it, and `DELETE .../rulesets/{id}` when the policy leaves it no rule. The planned payload is sent exactly as the plan holds it, and nothing is recomputed. The adapter refuses any planned object that isn't the `setpoint` ruleset, so other rulesets, classic branch protection and repo settings are never written.

A created ruleset has no bypass actors unless the policy declares `github.bypassActors`. An existing ruleset keeps the bypass actors it has while the policy leaves them undeclared, because they are reported as unmanaged rather than removed silently.

A failed write fails that repo, and the repo's later writes are not attempted. A 403 names the permission the token needs: Administration: write, or the `repo` scope on a classic token.

## Unmanaged settings

- **Other rulesets** that apply to the default branch, including organization rulesets, are read from `GET /repos/{owner}/{repo}/rules/branches/{branch}`. Each rule is reported under the neutral rule it bears on, or as `github.<type>` when it has none. A stricter one raises `SP2101`.
- **Repo merge settings** (`allow_merge_commit`, `allow_squash_merge`, `allow_rebase_merge`) are reported when they don't allow every method. When they and `allowedMergeTypes` leave no method in common, plan fails with `SP2102`, because no pull request could complete.

## What a read-only token can't see

GitHub leaves some fields out of a 200 response rather than refusing the call:

- `bypass_actors` are only shown to callers with write access. Without them, `github.bypassActors` is reported as unknown (`SP2202`) and not changed.
- The repo merge flags are only shown with Contents read. Without them there is nothing to compare, so `SP2102` can't be checked.

## Token permissions

| Command | Fine-grained PAT or App | Classic PAT |
|---|---|---|
| `plan` | Metadata: read. Add Contents: read to check the merge flags, and Administration: read for bypass actors (unconfirmed, see below). | `repo` per `X-Accepted-OAuth-Scopes`; `public_repo` is unconfirmed for public repos |
| `apply` | Administration: write, plus a role that may edit repository rules | `repo` |

Calls made on 2026-11-08 against `Quattordici/Setpoint` answered `X-Accepted-GitHub-Permissions: metadata=read` and `X-Accepted-OAuth-Scopes: repo` for `GET /repos/{owner}/{repo}`, `/rulesets` and `/rules/branches/{branch}`. Two questions need a live call with a dedicated token:

1. Does a classic PAT with only `public_repo` read rulesets on a public repo?
2. Does a fine-grained PAT with Administration: read (and no write) see `bypass_actors`?
