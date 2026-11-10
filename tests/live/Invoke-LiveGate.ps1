#Requires -Version 7.2
<#
.SYNOPSIS
    Runs a Setpoint live gate against one test environment. See docs/live-gates.md.

.DESCRIPTION
    Builds Setpoint from this checkout, then, for github, ado-cloud and ado-server:

      1. validates the gate and reset policies with --strict;
      2. applies the reset policy, so the gate repos start from (nearly) nothing;
      3. plans the gate policy and expects changes (exit 5);
      4. applies that plan and expects success (exit 0);
      5. plans again and expects everything in sync (exit 0);
      6. applies the old plan again and expects it to be refused as stale (exit 3);
      7. runs drift and expects no drift (exit 0);
      8. changes the reviewer count on gate-basic by hand, through the platform's API, the way a person would;
      9. runs drift and expects drift (exit 5), with a report that names gate-basic and minimumReviewers.count;
     10. applies the drift report and expects success (exit 0);
     11. runs drift again and expects no drift (exit 0). Steps 7 to 11 are gate 3.

    With -PlanOnly, and for ado-employer, it also runs drift read-only and accepts drift or none (exit 0 or 5).

    For ado-employer it only validates, plans and checks for drift. It never applies there: that server belongs to an employer,
    and the gate has no code path that writes to it.

    Output, plan files, drift reports and any recordings go to tests/live/out/<environment>-<timestamp>/, which git
    ignores. When GITHUB_STEP_SUMMARY is set, as in the nightly workflow, the drift summaries are appended to it.

.PARAMETER Environment
    github, ado-cloud, ado-server (the Express VM, the phase 2 gate) or ado-employer (plan only).

.PARAMETER Record
    Also record every HTTP exchange, redacted, as WireMock.Net mappings, under out/.../recordings/<step>/.
    Not allowed with ado-employer.

.PARAMETER ResetVm
    ado-server only: restore the VM's Hyper-V checkpoint and wait for Azure DevOps to answer before the run.

.PARAMETER PlanOnly
    Only validate and plan, as for ado-employer. Use it on Azure DevOps until writing lands (#36), or to check a
    read-only token.

.PARAMETER SkipReset
    Don't apply the reset policy first. Step 3 then accepts in-sync (exit 0) as well as changes.

.PARAMETER EmployerUrl
    ado-employer only: the collection URL. It is never committed; pass it on each run.

.PARAMETER EmployerScope
    ado-employer only: the project/repo globs to plan, such as 'Platform/*'. Required, so a run never reads the
    whole collection by accident.

.EXAMPLE
    $env:SETPOINT_LIVE_TOKEN = '<github fine-grained PAT, Administration: write>'
    ./tests/live/Invoke-LiveGate.ps1 -Environment github

.EXAMPLE
    ./tests/live/Invoke-LiveGate.ps1 -Environment ado-server -ResetVm -Record

.EXAMPLE
    $env:SETPOINT_LIVE_TOKEN = '<read-only PAT: Code (read), Build (read)>'
    ./tests/live/Invoke-LiveGate.ps1 -Environment ado-employer -EmployerUrl https://tfs.example.com/DefaultCollection -EmployerScope 'Platform/*'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('github', 'ado-cloud', 'ado-server', 'ado-employer')]
    [string] $Environment,

    [switch] $Record,

    [switch] $ResetVm,

    [switch] $SkipReset,

    [switch] $PlanOnly,

    [string] $EmployerUrl,

    [string[]] $EmployerScope,

    # Hyper-V names for -ResetVm.
    [string] $VmName = 'setpoint-ado',
    [string] $CheckpointName = 'clean',
    [int] $VmReadyTimeoutSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$liveRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $liveRoot '../..')).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $liveRoot "out/$Environment-$stamp"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$transcript = Join-Path $out 'gate.log'
$failures = [System.Collections.Generic.List[string]]::new()

function Write-Step([string] $text) { Write-Host "`n=== $text" -ForegroundColor Cyan }

function Assert-Token {
    if (-not $env:SETPOINT_LIVE_TOKEN) {
        throw 'Set SETPOINT_LIVE_TOKEN to the token for this environment. See docs/live-gates.md.'
    }
}

function Install-Setpoint {
    Write-Step 'Build and install Setpoint from this checkout'
    $packages = Join-Path $out 'packages'
    $toolPath = Join-Path $out 'tool'
    & dotnet pack (Join-Path $repoRoot 'src/Setpoint.Cli') --configuration Release --output $packages --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }
    & dotnet tool install Setpoint --tool-path $toolPath --add-source $packages --prerelease | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool install failed.' }
    $exe = Join-Path $toolPath ($IsWindows ? 'setpoint.exe' : 'setpoint')
    & $exe --version | Out-Host
    return $exe
}

# Assembles a policy repo: the environment's targets.yml plus a policy from policies/.
function New-PolicyRepo([string] $policy, [string] $targetsText) {
    $dir = Join-Path $out "policy-$policy"
    Copy-Item -Path (Join-Path $liveRoot "policies/$policy") -Destination $dir -Recurse
    Set-Content -Path (Join-Path $dir 'targets.yml') -Value $targetsText -NoNewline
    return $dir
}

function Get-TargetsText {
    if ($Environment -ne 'ado-employer') {
        return Get-Content -Raw (Join-Path $liveRoot "targets/$Environment.yml")
    }

    if (-not $EmployerUrl) { throw 'ado-employer needs -EmployerUrl.' }
    if (-not $EmployerScope) { throw "ado-employer needs -EmployerScope, such as 'Platform/*'." }
    $scope = ($EmployerScope | ForEach-Object { '"' + $_.Replace('"', '') + '"' }) -join ', '
    return @"
version: 1
targets:
  live:
    platform: azuredevops
    url: $EmployerUrl
    scope: [$scope]
"@
}

# Runs setpoint, tees its output, and records a failure unless the exit code is one of $expect.
function Invoke-Setpoint([string] $step, [int[]] $expect, [string[]] $arguments) {
    Write-Step "$step  (expect exit $($expect -join ' or '))"
    if ($Record -and $arguments[0] -ne 'validate') {
        $arguments += @('--record', (Join-Path $out "recordings/$step"))
    }
    Write-Host "setpoint $($arguments -join ' ')" -ForegroundColor DarkGray
    & $script:setpoint @arguments --output plain 2>&1 | Tee-Object -FilePath (Join-Path $out "$step.txt") | Out-Host
    $code = $LASTEXITCODE
    if ($expect -notcontains $code) {
        # Later steps depend on this one, so stop here.
        throw "$step exited $code, expected $($expect -join ' or '). See $(Join-Path $out "$step.txt")."
    }
    Write-Host "ok: $step exited $code" -ForegroundColor Green
    return $code
}

# Adds --summary to a drift step when the run has a step summary to show it on (GitHub Actions).
function Get-SummaryArguments {
    return $env:GITHUB_STEP_SUMMARY ? @('--summary', $env:GITHUB_STEP_SUMMARY) : @()
}

function Get-TargetUrl([string] $targetsText) {
    return ([regex]::Match($targetsText, 'url:\s*(\S+)')).Groups[1].Value.TrimEnd('/')
}

# Changes one managed setting by hand, through the platform's own API and not through Setpoint, the way a person
# editing settings in the web UI would: gate-basic's minimum reviewer count goes from the policy's 1 to 3.
function Set-HandEdit([string] $targetsText) {
    Write-Step 'hand-edit  (gate-basic: minimum reviewers 1 -> 3, outside Setpoint)'
    if ($Environment -eq 'github') {
        $owner = ([regex]::Match($targetsText, 'scope:\s*\[\s*"?([^/"\s]+)/')).Groups[1].Value
        $headers = @{
            Authorization          = "Bearer $env:SETPOINT_LIVE_TOKEN"
            Accept                 = 'application/vnd.github+json'
            'X-GitHub-Api-Version' = '2022-11-28'
        }
        $base = "https://api.github.com/repos/$owner/gate-basic/rulesets"
        $summary = @(Invoke-RestMethod -Uri "$base`?includes_parents=false" -Headers $headers) | Where-Object { $_.name -eq 'setpoint' }
        if (-not $summary) { throw "gate-basic has no 'setpoint' ruleset to edit; did apply run?" }
        $ruleset = Invoke-RestMethod -Uri "$base/$($summary.id)" -Headers $headers
        $pullRequest = $ruleset.rules | Where-Object { $_.type -eq 'pull_request' }
        if (-not $pullRequest) { throw "The 'setpoint' ruleset on gate-basic has no pull_request rule to edit." }
        $pullRequest.parameters.required_approving_review_count = 3
        $body = @{
            name          = $ruleset.name
            target        = $ruleset.target
            enforcement   = $ruleset.enforcement
            conditions    = $ruleset.conditions
            rules         = $ruleset.rules
            bypass_actors = @($ruleset.bypass_actors | Where-Object { $_ })
        } | ConvertTo-Json -Depth 20
        Invoke-RestMethod -Method Put -Uri "$base/$($summary.id)" -Headers $headers -Body $body -ContentType 'application/json' | Out-Null
    } else {
        $url = Get-TargetUrl $targetsText
        $headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$env:SETPOINT_LIVE_TOKEN")) }
        $project = "$url/setpoint-live"
        $repo = Invoke-RestMethod -Uri "$project/_apis/git/repositories/gate-basic?api-version=7.0" -Headers $headers
        $minimumReviewers = 'fa4e907d-c16b-4a4c-9dfa-4906e5d171dd'
        $configs = (Invoke-RestMethod -Uri "$project/_apis/policy/configurations?repositoryId=$($repo.id)&refName=$($repo.defaultBranch)&policyType=$minimumReviewers&api-version=7.0" -Headers $headers).value
        $config = @($configs | Where-Object { -not $_.isDeleted }) | Select-Object -First 1
        if (-not $config) { throw 'gate-basic has no minimum reviewers policy to edit; did apply run?' }
        $config.settings.minimumApproverCount = 3
        $body = $config | Select-Object isEnabled, isBlocking, type, settings | ConvertTo-Json -Depth 20
        Invoke-RestMethod -Method Put -Uri "$project/_apis/policy/configurations/$($config.id)?api-version=7.0" -Headers $headers -Body $body -ContentType 'application/json' | Out-Null
    }
    Write-Host 'ok: hand-edit made' -ForegroundColor Green
}

# Fails the run unless the drift report names gate-basic's reviewer count as the drift.
function Assert-DriftReport([string] $report) {
    $document = Get-Content -Raw $report | ConvertFrom-Json
    $basic = @($document.repos | Where-Object { $_.path -like '*/gate-basic' -and $_.status -eq 'changes' })
    if ($basic.Count -ne 1) { throw "The drift report doesn't show gate-basic as drifted. See $report." }
    if (-not ($basic[0].changes | Where-Object { $_.rule -eq 'minimumReviewers.count' })) {
        throw "The drift report doesn't name minimumReviewers.count on gate-basic. See $report."
    }
    $others = @($document.repos | Where-Object { $_.status -eq 'changes' -and $_.path -notlike '*/gate-basic' })
    if ($others.Count -gt 0) { throw "The drift report shows drift on repos nobody touched: $($others.path -join ', ')." }
    Write-Host 'ok: the drift report names gate-basic minimumReviewers.count, and nothing else' -ForegroundColor Green
}

function Reset-ExpressVm {
    Write-Step "Restore Hyper-V checkpoint '$CheckpointName' on VM '$VmName'"
    if (-not $IsWindows) { throw '-ResetVm needs Hyper-V on Windows.' }
    Import-Module Hyper-V
    Restore-VMCheckpoint -VMName $VmName -Name $CheckpointName -Confirm:$false
    Start-VM -Name $VmName -ErrorAction SilentlyContinue

    $url = ([regex]::Match((Get-Content -Raw (Join-Path $liveRoot 'targets/ado-server.yml')), 'url:\s*(\S+)')).Groups[1].Value
    $probe = "$($url.TrimEnd('/'))/_apis/connectionData"
    $auth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$env:SETPOINT_LIVE_TOKEN"))
    $deadline = (Get-Date).AddSeconds($VmReadyTimeoutSeconds)
    while ($true) {
        try {
            $response = Invoke-WebRequest -Uri $probe -Headers @{ Authorization = $auth } -UseBasicParsing -TimeoutSec 15
            if ($response.StatusCode -eq 200) { Write-Host 'Azure DevOps answers.'; break }
        } catch {
            # Still booting.
        }
        if ((Get-Date) -gt $deadline) { throw "Azure DevOps didn't answer at $probe within $VmReadyTimeoutSeconds s." }
        Start-Sleep -Seconds 10
    }
}

# Fails the run if a recording holds the token itself or anything shaped like a credential.
function Test-Recordings {
    $dir = Join-Path $out 'recordings'
    if (-not (Test-Path $dir)) { return }
    Write-Step 'Scan recordings for secrets'
    $patterns = @(
        [regex]::Escape($env:SETPOINT_LIVE_TOKEN),
        'gh[pousr]_[A-Za-z0-9]{20,}', 'github_pat_[A-Za-z0-9_]{20,}', '-----BEGIN [A-Z ]*PRIVATE KEY-----',
        '\b[a-z2-7]{52}\b'   # the shape of a classic Azure DevOps PAT
    )
    $hits = Get-ChildItem -Path $dir -Recurse -File | Select-String -Pattern $patterns
    if ($hits) {
        $hits | ForEach-Object { Write-Host "SECRET? $($_.Path):$($_.LineNumber)" -ForegroundColor Red }
        $failures.Add("recordings contain $(@($hits).Count) secret-like value(s); delete them and fix the redactor before committing anything")
    } else {
        Write-Host "No secrets found in $((Get-ChildItem -Path $dir -Recurse -File).Count) recorded exchanges."
    }
}

Start-Transcript -Path $transcript | Out-Null
try {
    Assert-Token
    if ($ResetVm -and $Environment -ne 'ado-server') { throw '-ResetVm only applies to ado-server.' }
    # Recordings become committed fixtures, and nothing from the employer's server may end up in this repo.
    if ($Record -and $Environment -eq 'ado-employer') { throw "-Record isn't allowed against the employer's server." }
    if ($Environment -ne 'ado-employer' -and ($EmployerUrl -or $EmployerScope)) { throw '-EmployerUrl and -EmployerScope only apply to ado-employer.' }

    $script:setpoint = Install-Setpoint
    $targets = Get-TargetsText
    $plan = Join-Path $out 'plan.json'
    $drift = Join-Path $out 'drift.json'

    if ($PlanOnly -and $Environment -ne 'ado-employer') {
        if ($ResetVm) { Reset-ExpressVm }
        $gate = New-PolicyRepo 'gate' $targets
        Invoke-Setpoint 'validate-gate' @(0) @('validate', '--policy', $gate, '--strict') | Out-Null
        Invoke-Setpoint 'plan' @(0, 5) @('plan', '--policy', $gate, '--out', $plan, '--exit-code') | Out-Null
        Invoke-Setpoint 'drift' @(0, 5) (@('drift', '--policy', $gate, '--report', $drift) + @(Get-SummaryArguments)) | Out-Null
    } elseif ($Environment -eq 'ado-employer') {
        # Plan only, with the baseline alone: the gate's group and override name repos that only exist in the
        # test environments. Warnings are expected with a read-only token (SP2202), so no --strict.
        $gate = Join-Path $out 'policy-employer'
        New-Item -ItemType Directory -Path $gate | Out-Null
        Copy-Item (Join-Path $liveRoot 'policies/gate/baseline.yml') $gate
        Set-Content -Path (Join-Path $gate 'targets.yml') -Value $targets -NoNewline

        Invoke-Setpoint 'validate' @(0) @('validate', '--policy', $gate) | Out-Null
        Invoke-Setpoint 'plan' @(0, 5) @('plan', '--policy', $gate, '--out', $plan, '--exit-code', '--max-changes', '0') | Out-Null
        Invoke-Setpoint 'drift' @(0, 5) @('drift', '--policy', $gate, '--report', $drift) | Out-Null
        Write-Host "`nPlan only: nothing is ever applied to the employer's server." -ForegroundColor Yellow
    } else {
        if ($ResetVm) { Reset-ExpressVm }

        $gate = New-PolicyRepo 'gate' $targets
        $reset = New-PolicyRepo 'reset' $targets

        Invoke-Setpoint 'validate-gate' @(0) @('validate', '--policy', $gate, '--strict') | Out-Null
        Invoke-Setpoint 'validate-reset' @(0) @('validate', '--policy', $reset, '--strict') | Out-Null

        if (-not $SkipReset) {
            Invoke-Setpoint 'reset' @(0) @('apply', '--policy', $reset, '--yes') | Out-Null
        }

        $firstPlan = $SkipReset ? @(0, 5) : @(5)
        Invoke-Setpoint 'plan' $firstPlan @('plan', '--policy', $gate, '--out', $plan, '--exit-code') | Out-Null
        Invoke-Setpoint 'apply' @(0) @('apply', $plan, '--policy', $gate) | Out-Null
        Invoke-Setpoint 'replan-in-sync' @(0) @('plan', '--policy', $gate, '--exit-code') | Out-Null
        Invoke-Setpoint 'apply-stale-plan' @(3) @('apply', $plan, '--policy', $gate) | Out-Null

        # Gate 3: drift finds a hand edit unattended, and its report puts it back.
        Invoke-Setpoint 'drift-in-sync' @(0) @('drift', '--policy', $gate, '--strict') | Out-Null
        Set-HandEdit $targets
        Invoke-Setpoint 'drift-found' @(5) (@('drift', '--policy', $gate, '--strict', '--report', $drift) + @(Get-SummaryArguments)) | Out-Null
        Assert-DriftReport $drift
        Invoke-Setpoint 'apply-drift-report' @(0) @('apply', $drift, '--policy', $gate) | Out-Null
        Invoke-Setpoint 'drift-restored' @(0) (@('drift', '--policy', $gate, '--strict') + @(Get-SummaryArguments)) | Out-Null
    }
} catch {
    $failures.Add($_.Exception.Message)
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    # Scan whatever was recorded, even when a step failed part-way.
    try { Test-Recordings } catch { $failures.Add("recording scan failed: $($_.Exception.Message)") }
    Stop-Transcript | Out-Null
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "Live gate $Environment FAILED ($($failures.Count)):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Output: $out"
    exit 1
}

Write-Host "Live gate $Environment passed. Output: $out" -ForegroundColor Green
exit 0
