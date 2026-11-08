# PROTOTYPE: policy file schema (ticket #17)

Throwaway. This sample answers "what do policy files look like?" and nothing else. No code reads it.

Layout of a policy repo:

```
targets.yml                                   # where: named endpoints
baseline.yml                                  # top layer, applies to every repo
groups/<name>.yml                             # one file per group, with selectors
repos/<target>/<project-or-owner>/<repo>.yml  # repo overrides; path mirrors the selector
```

## Effective policy (what `plan` would compute)

**ado-onprem/Payments/payments-api** (baseline → payments):

| Rule | Value | From |
|---|---|---|
| minimumReviewers | count 2, resetOnPush (locked) | baseline |
| requiredChecks | payments-ci (build expires after 720 min) | payments |
| requireCommentResolution | true | baseline |
| allowedMergeTypes | squash | payments |
| protectFromForcePushAndDelete | true (locked) | baseline |
| azuredevops.minimumReviewers | creatorVoteCounts false, allowDownvotes false | baseline |

**ado-onprem/Payments/legacy-ledger** (baseline → payments → repo):

| Rule | Value | From |
|---|---|---|
| minimumReviewers | count 2, resetOnPush (locked) | baseline |
| requiredChecks | unmanaged (`~`) | repo |
| requireCommentResolution | **removed** (`enabled: false`) | repo |
| allowedMergeTypes | merge, squash | repo |
| protectFromForcePushAndDelete | true (locked) | baseline |

**github/quattordici/docs-site** (baseline only): the baseline rules, plus `github.pullRequest.requireCodeOwnerReview: false`.

## A validate error

If the commented-out line in `legacy-ledger.yml` were uncommented:

```
error SP0107: repos/ado-onprem/Payments/legacy-ledger.yml:14
  'minimumReviewers' is locked by baseline.yml and cannot be overridden.
```
