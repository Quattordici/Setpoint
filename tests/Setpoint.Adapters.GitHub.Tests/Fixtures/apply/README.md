# Fixtures

WireMock.Net mappings in the format `--record` writes (see `RecordingHandler`), replayed by the adapter tests.

The `apply/` mappings follow GitHub's documented responses for the repository ruleset endpoints, rewritten to
the `ghe` test target. Re-record them against the throwaway GitHub org with
`./tests/live/Invoke-LiveGate.ps1 -Environment github -Record` once that org exists (see `docs/live-gates.md`),
and rename the owner and repo to `acme/payments` and `acme/readonly`. Never record against an employer's server.
