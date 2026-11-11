# Releasing

For maintainers. A release is a git tag: pushing `v1.2.3` runs [the release workflow](../.github/workflows/release.yml), which builds, tests and publishes Setpoint 1.2.3 to nuget.org and creates the GitHub release.

## Versions

[MinVer](https://github.com/adamralph/minver) takes the version from the latest `v*` tag on the commit's history:

| Commit | Version |
|---|---|
| Tagged `v1.0.0` | `1.0.0` |
| 3 commits after `v1.0.0` | `1.0.1-alpha.0.3` |
| Before the first tag | `1.0.0-alpha.0.N` |

Plan files record the full version, and `apply` refuses a plan made by another one, so every build that isn't a tag is a prerelease that no pipeline should pin.

What counts as a breaking change is the policy format (schema v1), the plan file format, the exit codes and the `SPxxxx` codes. Schema v1 is frozen: `ReleasedSchemaTests` compares `schema/v1/` with the copies released in 1.0 under `tests/Setpoint.Cli.Tests/Policy/Released/v1/` and fails if a change could reject a policy file 1.0 accepted. A change like that needs schema v2.

## Cutting a release

1. Move the entries under `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md) to a new `## [1.2.3]` section, and add its link at the bottom. The workflow uses that section as the release notes and fails without it.
2. Update the pinned version in [getting-started.md](getting-started.md), [pipelines.md](pipelines.md) and the [sample pipelines](../samples/pipelines/) to `1.2.3`. The tests fail on the tag if they pin anything else.
3. Merge that to `main`, then tag the merge commit and push the tag:

   ```sh
   git tag v1.2.3
   git push origin v1.2.3
   ```

4. Approve the run in the `nuget` environment if it has required reviewers. The workflow checks that the package and `setpoint --version` are `1.2.3` before it pushes.
5. Check that `dotnet tool install -g Setpoint --version 1.2.3` works.

A package on nuget.org can't be replaced, only unlisted, so a broken release is fixed by releasing the next patch.

## nuget.org setup

The workflow uses [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): it swaps the run's GitHub OIDC token for an API key that lasts an hour, so no key is stored. This is set up once:

1. On nuget.org, sign in as the account that owns the `Setpoint` package (the first push creates the id under the account the policy belongs to).
2. Under **Trusted Publishing**, add a policy for GitHub Actions with owner `Quattordici`, repository `Setpoint`, workflow file `release.yml` and environment `nuget`.
3. In the GitHub repo, add the repository variable `NUGET_USER` with that nuget.org account's user name (not its email), and create the `nuget` environment, optionally with required reviewers and a rule that only `v*` tags may deploy to it.
