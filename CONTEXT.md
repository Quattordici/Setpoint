# Setpoint

Setpoint declares repository governance once and brings repositories on GitHub and Azure DevOps in line with it.

## Language

**Policy**:
The declared desired state for repositories, written in policy files and made of rules.
_Avoid_: Settings (except as plain English in docs), governance config, desired config

**Policy file**:
A file in the policy repo that holds part of the policy.
_Avoid_: Config file, settings file

**Policy repo**:
The repository that holds an estate's policy files, owned by the platform team that runs Setpoint.
_Avoid_: Config repo, governance repo, admin repo

**Rule**:
One declared requirement inside a policy, such as a minimum number of reviewers.
_Avoid_: Setting, check, policy (for a single item)

**Live state**:
What a platform currently has configured for a repository.
_Avoid_: Actual state, current config, reality
