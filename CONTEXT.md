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

**Locked rule**:
A baseline rule that groups and repo overrides may not change.
_Avoid_: Enforced rule, mandatory rule

## Layers

**Layer**:
One level of policy that contributes rules to a repo: the baseline, a group, or a repo override.
_Avoid_: Tier, scope, level

**Baseline**:
The top layer, applying to every repo the policy repo governs on every platform.
_Avoid_: Organization defaults, org policy, global config

**Group**:
A named layer whose rules apply to the repos its selectors match.
_Avoid_: Team, project (for this concept), cluster

**Selector**:
An explicit repo or a glob over platform-qualified repo paths that puts repos into a group.
_Avoid_: Filter, matcher, query

**Repo override**:
The bottom layer: rules declared for one specific repo.
_Avoid_: Repo config, local policy

**Live state**:
What a platform currently has configured for a repository.
_Avoid_: Actual state, current config, reality

**Managed setting**:
A piece of live state that some rule in the effective policy declares, either as present or as `enabled: false` (absent).
_Avoid_: Owned setting, tracked setting

**Unmanaged setting**:
A piece of live state that no rule declares; Setpoint reports it but never changes it.
_Avoid_: Foreign setting, extra setting, drift (drift is a difference on a managed setting)
