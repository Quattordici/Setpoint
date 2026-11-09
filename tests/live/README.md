# Live gates

The gate script, the gate and reset policies, and each test environment's `targets.yml`. The runbook (setup,
running, resetting the VM, recording fixtures, rotating tokens) is [docs/live-gates.md](../../docs/live-gates.md).

```powershell
$env:SETPOINT_LIVE_TOKEN = '<token>'
./tests/live/Invoke-LiveGate.ps1 -Environment github
```

| Path | What |
|---|---|
| `Invoke-LiveGate.ps1` | Builds Setpoint, then validates, resets, plans, applies, re-plans and checks the stale guard |
| `targets/<environment>.yml` | The `live` target for `github`, `ado-cloud` and `ado-server`. The employer's URL is never committed |
| `policies/gate/` | The policy every gate applies: every neutral rule, a group, an override and both provider blocks |
| `policies/reset/` | Removes what the gate sets, so each run writes |
| `out/` | Each run's output, plan file and recordings. Ignored by git |
