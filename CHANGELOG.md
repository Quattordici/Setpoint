# Changelog

All notable changes to Setpoint are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Setpoint follows [Semantic Versioning](https://semver.org/): the policy format (schema v1), the plan file format, the exit codes and the `SPxxxx` diagnostic codes are its public contract.

The release workflow takes a release's notes from its section below, so every tag needs one.

## [Unreleased]

## [1.0.0]

The first release: repository governance as code for Azure DevOps Server, Azure DevOps Services and GitHub.

### Commands

- `setpoint validate`: offline checks of a policy repo against schema v1 and the layering rules.
- `setpoint plan`: reads live state and shows the changes apply would make, as a tree, plain lines or JSON. `--out plan.json` saves a plan file, `--exit-code` exits 5 when there are changes, `--summary` writes a Markdown summary for the pipeline run page.
- `setpoint apply plan.json` applies a saved plan after checking that the policy, the Setpoint version and every planned object are as they were (exit 3 otherwise). `setpoint apply --yes` re-plans and applies in one step. `--max-changes` (25 repos by default), `--strict`, `--fail-fast` and `--skip-unreachable` guard it.
- `setpoint drift`: plans and never writes; exits 5 when live state differs from the policy. `--report drift.json` is a plan file that `apply` can put back.

### Policy

- YAML policy repos with a required `version: 1`: `targets.yml`, `baseline.yml`, `groups/<name>.yml` and `repos/<target>/<project>/<repo>.yml`, merged baseline, then groups, then the repo override. `locked:` stops lower layers from changing a rule, and `~` leaves a rule unmanaged at a layer.
- Neutral rules for the default branch: `minimumReviewers`, `requiredChecks`, `requireCommentResolution`, `allowedMergeTypes` and `protectFromForcePushAndDelete`, plus `access` (who may read, write or administer a repo) and `environments` (deployment approvals).
- `github:` and `azuredevops:` provider blocks for settings only one platform has.
- JSON Schemas for editors under `schema/v1/`. Schema v1 is frozen: later 1.x releases only add optional keys.

### Platforms

- Azure DevOps Server 2022 and later and Azure DevOps Services, with API version 7.0 and the version detected at start-up. Branch policies, repository ACLs for groups, and environment Approval checks. Sign-in with a PAT, `System.AccessToken`, `auth: integrated` (Windows) or, on Services, `auth: entra`.
- GitHub.com: one ruleset named `setpoint` per repo, team access and environment protection. Sign-in with a PAT or a GitHub App (`auth: app`).
- Managed-only ownership: Setpoint changes only what it owns, leaves ambiguous matches alone with a warning, and lists the settings it leaves unmanaged.

[Unreleased]: https://github.com/Quattordici/Setpoint/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/Quattordici/Setpoint/releases/tag/v1.0.0
