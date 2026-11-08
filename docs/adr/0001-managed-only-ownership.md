# Setpoint manages only what the policy declares

Setpoint keeps no state, so it can't tell which live settings it created and which someone else did. If `apply` removed every setting the policy doesn't mention, the first run against a hand-configured estate would delete settings nobody reviewed. So `apply` only changes what the policy declares, and leaves everything else alone. Removal happens only when a rule says `enabled: false`. `~` means "unmanaged", not "remove".

## Considered Options

- **Authoritative** (remove anything undeclared): rejected for the MVP because it is unsafe without state.
- **Per-area `exclusive: true`**: deferred to the drift phase, where unmanaged settings are already being reported.

## Consequences

- On GitHub, Setpoint owns a single ruleset per repo, named `setpoint`, and never edits other rulesets or classic branch protection. Rulesets stack and the strictest one wins, so a hand-made ruleset can make a repo stricter than the policy but never weaker.
- On Azure DevOps, Setpoint matches a live policy by policy type and branch scope. When more than one live policy matches, the plan reports it as ambiguous and Setpoint leaves it alone.
- `plan` reports a count of unmanaged settings for each repo (`--verbose` lists them). It warns when an unmanaged setting is stricter than the policy, because the effective result then differs from what the policy says.
