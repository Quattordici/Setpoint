# Fixtures

WireMock.Net mappings in the format `--record` writes (see `RecordingHandler`), replayed by the adapter tests.

The `apply/` mappings follow GitHub's documented responses for the repository ruleset endpoints, rewritten to
the `ghe` test target. Re-record them against the throwaway GitHub org with
`setpoint apply ... --record tests/Setpoint.Adapters.GitHub.Tests/Fixtures/apply` once that org exists, and
rename the owner and repo to `acme/payments` and `acme/readonly`. Never record against an employer's server.
