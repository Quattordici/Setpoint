# The Azure DevOps Server releases Setpoint supports, one Express VM each. See docs/live-gates.md.
#
#   VmName     the Hyper-V VM, restored from its 'clean' checkpoint by -ResetVm
#   Url        the collection URL; it replaces url: in targets/ado-server.yml
#   Detected   what the preflight must name the server; the gate fails on anything else
#   TokenName  the secret in the live-gate environment holding that VM's PAT (the nightly workflow reads it)
@{
    '2022' = @{
        VmName    = 'setpoint-ado'
        Url       = 'http://setpoint-ado/DefaultCollection'
        Detected  = 'Azure DevOps Server 2022'
        TokenName = 'LIVE_ADO_SERVER_TOKEN'
    }
    '2022.1' = @{
        VmName    = 'setpoint-ado-2022-1'
        Url       = 'http://setpoint-ado-2022-1/DefaultCollection'
        Detected  = 'Azure DevOps Server 2022.1 or 2022.2'
        TokenName = 'LIVE_ADO_SERVER_2022_1_TOKEN'
    }
    '2022.2' = @{
        VmName    = 'setpoint-ado-2022-2'
        Url       = 'http://setpoint-ado-2022-2/DefaultCollection'
        Detected  = 'Azure DevOps Server 2022.1 or 2022.2'
        TokenName = 'LIVE_ADO_SERVER_2022_2_TOKEN'
    }
    '2025' = @{
        VmName    = 'setpoint-ado-2025'
        Url       = 'http://setpoint-ado-2025/DefaultCollection'
        Detected  = 'Azure DevOps Server (2025)'
        TokenName = 'LIVE_ADO_SERVER_2025_TOKEN'
    }
}
