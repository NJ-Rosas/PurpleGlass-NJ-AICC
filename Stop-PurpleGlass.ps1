<#
Stops only application process trees recorded by Start-PurpleGlass.ps1.
Docker infrastructure is preserved by default, including all named volumes.
#>

[CmdletBinding()]
param([switch] $IncludeInfrastructure)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = $PSScriptRoot
$ApplicationRoot = Join-Path $RepositoryRoot 'AI Call Center'
$StatePath = Join-Path $ApplicationRoot '.artifacts\local-launcher\processes.json'

function Get-DescendantProcessIds {
    param([Parameter(Mandatory)][int] $ParentProcessId, [Parameter(Mandatory)][object[]] $Processes)
    $results = @()
    foreach ($child in @($Processes | Where-Object { [int]$_.ParentProcessId -eq $ParentProcessId })) {
        $results += Get-DescendantProcessIds -ParentProcessId ([int]$child.ProcessId) -Processes $Processes
        $results += [int]$child.ProcessId
    }
    return $results
}

try {
    if (Test-Path -LiteralPath $StatePath) {
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        if ([string]$state.RepositoryRoot -ne $RepositoryRoot) {
            throw 'The launcher state belongs to a different repository path; no process was stopped.'
        }

        $allProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
        foreach ($entry in @($state.Processes)) {
            $process = Get-Process -Id ([int]$entry.ProcessId) -ErrorAction SilentlyContinue
            if ($null -eq $process) {
                Write-Host "[PurpleGlass] $($entry.Component) is already stopped."
                continue
            }

            $rootProcessInfo = $allProcesses | Where-Object { [int]$_.ProcessId -eq [int]$entry.ProcessId } | Select-Object -First 1
            $markerProperty = $entry.PSObject.Properties['CommandMarker']
            $marker = if ($null -ne $markerProperty) { [string]$markerProperty.Value } else { '' }
            if ($null -eq $rootProcessInfo -or [string]::IsNullOrWhiteSpace($marker) -or
                -not ([string]$rootProcessInfo.CommandLine).Contains($marker)) {
                Write-Warning "PID $($entry.ProcessId) does not match its launcher ownership marker; it was not stopped."
                continue
            }

            $descendants = @(Get-DescendantProcessIds -ParentProcessId ([int]$entry.ProcessId) -Processes $allProcesses)
            foreach ($processId in $descendants) {
                Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            }
            Stop-Process -Id ([int]$entry.ProcessId) -Force -ErrorAction SilentlyContinue
            Write-Host "[PurpleGlass] Stopped $($entry.Component)." -ForegroundColor Green
        }
        Remove-Item -LiteralPath $StatePath -Force
    }
    else {
        Write-Host '[PurpleGlass] No launcher-owned application processes were recorded.'
    }

    if ($IncludeInfrastructure) {
        $docker = Get-Command docker -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $docker) { throw 'Docker was not found; application processes were stopped, but infrastructure could not be stopped.' }
        & $docker.Source compose --file (Join-Path $ApplicationRoot 'compose.yaml') stop
        if ($LASTEXITCODE -ne 0) { throw "Docker Compose stop failed with exit code $LASTEXITCODE." }
        Write-Host '[PurpleGlass] Infrastructure containers stopped. Named volumes and data were preserved.' -ForegroundColor Green
    }
    else {
        Write-Host '[PurpleGlass] Infrastructure containers remain running.'
    }
}
catch {
    Write-Host ''
    Write-Host 'PurpleGlass shutdown was incomplete.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
