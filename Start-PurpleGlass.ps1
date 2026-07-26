<#
Open this file in VS Code and press Run / Play ▶.

Starts the complete supported PurpleGlass local-development environment. The
script is safe to run repeatedly and never deletes containers, volumes, data,
or developer configuration.
#>

[CmdletBinding()]
param(
    [switch] $NoBrowser,
    [ValidateRange(15, 600)]
    [int] $DockerReadyTimeoutSeconds = 120,
    [ValidateRange(15, 600)]
    [int] $ServiceReadyTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$OriginalLocation = (Get-Location).Path

$RepositoryRoot = $PSScriptRoot
$ApplicationRoot = Join-Path $RepositoryRoot 'AI Call Center'
$BackendRoot = Join-Path $ApplicationRoot 'src\backend'
$FrontendRoot = Join-Path $ApplicationRoot 'src\frontend'
$SolutionPath = Join-Path $BackendRoot 'PurpleGlass.sln'
$MigrationProject = Join-Path $BackendRoot 'Hosts\PurpleGlass.Migrations\PurpleGlass.Migrations.csproj'
$WebBffProject = Join-Path $BackendRoot 'Hosts\PurpleGlass.WebBff\PurpleGlass.WebBff.csproj'
$WorkerProject = Join-Path $BackendRoot 'Hosts\PurpleGlass.Integrations.Worker\PurpleGlass.Integrations.Worker.csproj'
$ComposePath = Join-Path $ApplicationRoot 'compose.yaml'
$StateDirectory = Join-Path $ApplicationRoot '.artifacts\local-launcher'
$StatePath = Join-Path $StateDirectory 'processes.json'
$BackendUrl = 'http://127.0.0.1:5101'
$FrontendUrl = 'http://127.0.0.1:5173'

function Write-Status {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host "[PurpleGlass] $Message" -ForegroundColor Cyan
}

function Write-Ready {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host "[PurpleGlass] $Message" -ForegroundColor Green
}

function Assert-Path {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Description)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Required $Description was not found at '$Path'."
    }
}

function Resolve-RequiredCommand {
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $InstallMessage)
    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        throw "Missing prerequisite: $Name.`n$InstallMessage"
    }
    return $command.Source
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $FailureMessage
    )
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function Test-DockerDaemon {
    param([Parameter(Mandatory)][string] $DockerPath)
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        $result = & $DockerPath info --format '{{.ServerVersion}}' 2>$null
        return ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($result -join '')))
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Start-DockerDesktopIfNeeded {
    param([Parameter(Mandatory)][string] $DockerPath)
    if (Test-DockerDaemon $DockerPath) {
        Write-Ready 'Docker ready.'
        return
    }

    $desktopProcess = Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $desktopProcess) {
        $candidates = @(
            (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'),
            (Join-Path $env:LOCALAPPDATA 'Docker\Docker Desktop.exe')
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

        $desktopPath = $candidates | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($desktopPath)) {
            throw "Docker is installed on PATH, but Docker Desktop could not be found or started.`nStart Docker Desktop and run Start-PurpleGlass.ps1 again."
        }

        Write-Status 'Starting Docker Desktop...'
        Start-Process -FilePath $desktopPath | Out-Null
    }
    else {
        Write-Status 'Docker Desktop is running; waiting for its daemon...'
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $DockerReadyTimeoutSeconds) {
        if (Test-DockerDaemon $DockerPath) {
            Write-Ready 'Docker ready.'
            return
        }
        Start-Sleep -Seconds 2
    }

    throw "Docker Desktop did not become ready within $DockerReadyTimeoutSeconds seconds.`nOpen Docker Desktop, resolve its startup error, and run Start-PurpleGlass.ps1 again."
}

function Get-ListeningProcessInfo {
    param([Parameter(Mandatory)][int] $Port)
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
    $results = @()
    foreach ($listener in $listeners) {
        $processId = [int]$listener.OwningProcess
        if (@($results | Where-Object { $_.ProcessId -eq $processId }).Count -gt 0) { continue }
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
        $results += [pscustomobject]@{
            ProcessId = $processId
            Name = if ($null -ne $process) { [string]$process.Name } else { 'unknown' }
            CommandLine = if ($null -ne $process) { [string]$process.CommandLine } else { '' }
            ExecutablePath = if ($null -ne $process) { [string]$process.ExecutablePath } else { '' }
        }
    }
    return $results
}

function Test-RepositoryProcess {
    param(
        [Parameter(Mandatory)] $ProcessInfo,
        [Parameter(Mandatory)][string[]] $Identifiers
    )
    $evidence = "$($ProcessInfo.CommandLine) $($ProcessInfo.ExecutablePath)"
    $hasRepositoryPath = $evidence.IndexOf($ApplicationRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
    foreach ($identifier in $Identifiers) {
        if ($evidence.IndexOf($identifier, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and $hasRepositoryPath) {
            return $true
        }
    }
    return $false
}

function Assert-ApplicationPortAvailable {
    param(
        [Parameter(Mandatory)][string] $Component,
        [Parameter(Mandatory)][int] $Port,
        [Parameter(Mandatory)][string[]] $Identifiers
    )
    $owners = @(Get-ListeningProcessInfo $Port)
    if ($owners.Count -eq 0) { return $false }

    foreach ($owner in $owners) {
        if (Test-RepositoryProcess $owner $Identifiers) {
            Write-Status "$Component is already running on port $Port (PID $($owner.ProcessId)); reusing it."
            return $true
        }
    }

    $ownerSummary = ($owners | ForEach-Object { "PID $($_.ProcessId) ($($_.Name))" }) -join ', '
    throw "$Component cannot start.`nPort $Port is already in use by $ownerSummary.`nResolve the port conflict and run Start-PurpleGlass.ps1 again. No process was terminated."
}

function Get-ComposeConfiguration {
    param([Parameter(Mandatory)][string] $DockerPath)
    $raw = & $DockerPath compose --file $ComposePath config --format json
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Compose configuration is invalid. Review compose.yaml and .env, then run the launcher again.'
    }
    return (($raw -join "`n") | ConvertFrom-Json)
}

function Get-ComposeStates {
    param([Parameter(Mandatory)][string] $DockerPath)
    $raw = @(& $DockerPath compose --file $ComposePath ps --all --format json 2>$null)
    $states = @()
    foreach ($line in $raw) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $states += ($line | ConvertFrom-Json)
        }
    }
    return $states
}

function Assert-InfrastructurePortsAvailable {
    param([Parameter(Mandatory)] $ComposeConfiguration, [Parameter(Mandatory)][string] $DockerPath)
    $states = @(Get-ComposeStates $DockerPath)
    foreach ($serviceProperty in $ComposeConfiguration.services.PSObject.Properties) {
        $serviceName = $serviceProperty.Name
        foreach ($portMapping in @($serviceProperty.Value.ports)) {
            if ($null -eq $portMapping -or [string]::IsNullOrWhiteSpace([string]$portMapping.published)) { continue }
            $port = [int]$portMapping.published
            $owners = @(Get-ListeningProcessInfo $port)
            if ($owners.Count -eq 0) { continue }

            $ownedState = $states | Where-Object { $_.Service -eq $serviceName -and $_.State -eq 'running' } | Select-Object -First 1
            if ($null -ne $ownedState) { continue }

            $container = & $DockerPath ps --filter "publish=$port" --format '{{.Names}}|{{.Label "com.docker.compose.project"}}|{{.Label "com.docker.compose.service"}}' 2>$null
            $containerDescription = ($container -join ', ').Trim()
            if (-not [string]::IsNullOrWhiteSpace($containerDescription)) {
                throw "Infrastructure service '$serviceName' cannot start.`nPort $port is published by another container: $containerDescription.`nResolve the conflict and run the launcher again."
            }

            $ownerSummary = ($owners | ForEach-Object { "PID $($_.ProcessId) ($($_.Name))" }) -join ', '
            throw "Infrastructure service '$serviceName' cannot start.`nPort $port is already in use by $ownerSummary.`nResolve the conflict and run the launcher again. No process was terminated."
        }
    }
}

function Wait-ForComposeServices {
    param([Parameter(Mandatory)][string] $DockerPath, [Parameter(Mandatory)][string[]] $Services)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $ServiceReadyTimeoutSeconds) {
        $states = @(Get-ComposeStates $DockerPath)
        $allReady = $true
        foreach ($service in $Services) {
            $state = $states | Where-Object { $_.Service -eq $service } | Select-Object -First 1
            if ($null -eq $state -or $state.State -ne 'running' -or (-not [string]::IsNullOrWhiteSpace([string]$state.Health) -and $state.Health -ne 'healthy')) {
                $allReady = $false
                break
            }
        }
        if ($allReady) {
            foreach ($service in $Services) { Write-Ready "$service ready." }
            return
        }
        Start-Sleep -Seconds 2
    }

    $diagnostics = & $DockerPath compose --file $ComposePath ps
    throw "Infrastructure did not become healthy within $ServiceReadyTimeoutSeconds seconds.`n$($diagnostics -join "`n")"
}

function Test-HttpReady {
    param([Parameter(Mandatory)][string] $Url)
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
        return ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400)
    }
    catch {
        return $false
    }
}

function Wait-ForHttpReady {
    param([Parameter(Mandatory)][string] $Component, [Parameter(Mandatory)][string] $Url)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $ServiceReadyTimeoutSeconds) {
        if (Test-HttpReady $Url) {
            Write-Ready "$Component ready."
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "$Component did not become reachable at $Url within $ServiceReadyTimeoutSeconds seconds. Review its titled terminal for details."
}

function Find-RepositoryComponentProcess {
    param([Parameter(Mandatory)][string[]] $Identifiers)
    $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        $info = [pscustomobject]@{
            ProcessId = [int]$process.ProcessId
            Name = [string]$process.Name
            CommandLine = [string]$process.CommandLine
            ExecutablePath = [string]$process.ExecutablePath
        }
        if (Test-RepositoryProcess $info $Identifiers) { return $info }
    }
    return $null
}

function Start-ComponentTerminal {
    param(
        [Parameter(Mandatory)][string] $Component,
        [Parameter(Mandatory)][string] $Title,
        [Parameter(Mandatory)][string] $WorkingDirectory,
        [Parameter(Mandatory)][string] $Command
    )
    $shell = Get-Command 'pwsh.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $shell) {
        $shell = Get-Command 'powershell.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if ($null -eq $shell) {
        throw 'Neither PowerShell 7 nor Windows PowerShell is available to open component log windows.'
    }

    $escapedTitle = $Title.Replace("'", "''")
    $escapedDirectory = $WorkingDirectory.Replace("'", "''")
    $terminalCommand = @"
`$Host.UI.RawUI.WindowTitle = '$escapedTitle'
Set-Location -LiteralPath '$escapedDirectory'
Write-Host '$Title' -ForegroundColor Cyan
Write-Host 'Press Ctrl+C to stop this component.' -ForegroundColor DarkGray
$Command
if (`$LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '$Component stopped with exit code' `$LASTEXITCODE -ForegroundColor Red
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($terminalCommand))
    $process = Start-Process -FilePath $shell.Source -ArgumentList "-NoLogo -NoExit -EncodedCommand $encoded" -PassThru
    return [pscustomobject]@{
        Component = $Component
        ProcessId = $process.Id
        StartedAtUtc = $process.StartTime.ToUniversalTime().ToString('O')
        CommandMarker = $encoded
    }
}

function Save-LauncherState {
    param([Parameter(Mandatory)][object[]] $Processes)
    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    [pscustomobject]@{
        RepositoryRoot = $RepositoryRoot
        UpdatedAtUtc = [DateTime]::UtcNow.ToString('O')
        Processes = @($Processes)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StatePath -Encoding UTF8
}

function Get-ValidLauncherStateEntries {
    if (-not (Test-Path -LiteralPath $StatePath)) { return @() }
    try {
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        if ([string]$state.RepositoryRoot -ne $RepositoryRoot) { return @() }
        $valid = @()
        foreach ($entry in @($state.Processes)) {
            $process = Get-Process -Id ([int]$entry.ProcessId) -ErrorAction SilentlyContinue
            if ($null -eq $process) { continue }
            $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($entry.ProcessId)" -ErrorAction SilentlyContinue
            $markerProperty = $entry.PSObject.Properties['CommandMarker']
            $marker = if ($null -ne $markerProperty) { [string]$markerProperty.Value } else { '' }
            if ($null -ne $processInfo -and -not [string]::IsNullOrWhiteSpace($marker) -and
                ([string]$processInfo.CommandLine).Contains($marker)) {
                $valid += $entry
            }
        }
        return $valid
    }
    catch {
        return @()
    }
}

try {
    Write-Host ''
    Write-Host '========================================' -ForegroundColor Magenta
    Write-Host ' PurpleGlass Development Environment' -ForegroundColor Magenta
    Write-Host '========================================' -ForegroundColor Magenta
    Write-Host ''

    Assert-Path $ApplicationRoot 'application directory'
    Assert-Path $SolutionPath 'backend solution'
    Assert-Path $ComposePath 'Docker Compose file'
    Assert-Path $MigrationProject 'migration host'
    Assert-Path $WebBffProject 'Web BFF project'
    Assert-Path $WorkerProject 'integrations worker project'
    Assert-Path (Join-Path $FrontendRoot 'package-lock.json') 'frontend lockfile'
    Assert-Path (Join-Path $ApplicationRoot 'deploy\local\mosquitto\config\mosquitto.conf') 'MQTT configuration'

    Set-Location -LiteralPath $ApplicationRoot
    Write-Status 'Checking developer prerequisites...'
    $dotnet = Resolve-RequiredCommand 'dotnet' 'Install the .NET SDK version declared in AI Call Center\global.json.'
    $node = Resolve-RequiredCommand 'node' 'Install Node.js 24 LTS and run the launcher again.'
    $npm = Resolve-RequiredCommand 'npm.cmd' 'Install npm with Node.js 24 LTS and run the launcher again.'
    $docker = Resolve-RequiredCommand 'docker' 'Install Docker Desktop with Docker Compose and run the launcher again.'

    # Windows can resolve node.exe through its registered App Path even when the
    # installation directory is absent from PATH. npm.cmd itself can start in
    # that state, but npm scripts then fail when they perform a normal `node`
    # lookup. Explicitly propagate the validated Node directory to every child.
    $nodeDirectory = Split-Path -Parent $node
    if (-not (($env:PATH -split ';') -contains $nodeDirectory)) {
        $env:PATH = "$nodeDirectory;$env:PATH"
    }

    $dotnetVersion = (& $dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The .NET SDK was found but could not run.' }
    $requiredDotnetVersion = (Get-Content -LiteralPath (Join-Path $ApplicationRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actualDotnet = [Version]$dotnetVersion
    $requiredDotnet = [Version]$requiredDotnetVersion
    $sameFeatureBand = $actualDotnet.Major -eq $requiredDotnet.Major -and
        $actualDotnet.Minor -eq $requiredDotnet.Minor -and
        [Math]::Floor($actualDotnet.Build / 100) -eq [Math]::Floor($requiredDotnet.Build / 100)
    if (-not $sameFeatureBand -or $actualDotnet -lt $requiredDotnet) {
        throw "The repository requires .NET SDK $requiredDotnetVersion with global.json rollForward=latestPatch, but dotnet resolved $dotnetVersion.`nInstall a compatible SDK and run the launcher again."
    }

    $nodeVersion = (& $node --version).Trim().TrimStart('v')
    if ([Version]$nodeVersion -lt [Version]'24.0.0') {
        throw "The repository requires Node.js 24 LTS or newer, but Node.js $nodeVersion was found."
    }
    & $docker compose version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose is unavailable. Install or update Docker Desktop and try again.' }
    Write-Ready ".NET $dotnetVersion, Node.js $nodeVersion, npm, Docker, and Docker Compose found."

    $webBffAlreadyRunning = Assert-ApplicationPortAvailable 'PurpleGlass Web BFF' 5101 @('PurpleGlass.WebBff')
    $frontendAlreadyRunning = Assert-ApplicationPortAvailable 'PurpleGlass frontend' 5173 @('vite', 'purpleglass-web')

    Start-DockerDesktopIfNeeded $docker
    $composeConfiguration = Get-ComposeConfiguration $docker
    $services = @($composeConfiguration.services.PSObject.Properties.Name)
    Assert-InfrastructurePortsAvailable $composeConfiguration $docker

    if (Test-Path -LiteralPath (Join-Path $ApplicationRoot '.env')) {
        Write-Status 'Using the local .env file through Docker Compose; no values will be printed.'
    }
    else {
        Write-Status 'No .env file found; using the committed synthetic development defaults.'
    }

    Write-Status 'Starting PostgreSQL, MQTT, and Valkey containers...'
    Invoke-CheckedCommand $docker @('compose', '--file', $ComposePath, 'up', '-d') 'Docker Compose failed to start the local infrastructure'
    Wait-ForComposeServices $docker $services

    $postgres = $composeConfiguration.services.postgres
    $postgresPort = [string](@($postgres.ports)[0].published)
    $postgresHost = '127.0.0.1'
    $postgresDatabase = [string]$postgres.environment.POSTGRES_DB
    $postgresUser = [string]$postgres.environment.POSTGRES_USER
    $postgresPassword = [string]$postgres.environment.POSTGRES_PASSWORD
    $env:ConnectionStrings__Postgres = "Host=$postgresHost;Port=$postgresPort;Database=$postgresDatabase;Username=$postgresUser;Password=$postgresPassword"
    $env:Mqtt__Host = '127.0.0.1'
    $env:Mqtt__Port = [string](@($composeConfiguration.services.mqtt.ports)[0].published)
    $env:Providers__EnableRealTelephony = 'false'
    $env:Providers__EnableRealAI = 'false'
    $env:Providers__EnableRealSpeech = 'false'
    $env:Integrations__EnableOpenDental = 'false'
    $env:DataProtection__AllowSensitiveData = 'false'
    $env:Telephony__Provider = 'None'
    $env:SpeechToText__Provider = 'Fake'
    $env:LanguageModel__Provider = 'Fake'
    $env:TextToSpeech__Provider = 'Fake'

    Write-Status 'Restoring backend tools and locked dependencies...'
    Invoke-CheckedCommand $dotnet @('tool', 'restore') 'Backend tool restore failed'
    Invoke-CheckedCommand $dotnet @('restore', $SolutionPath, '--locked-mode') 'Backend dependency restore failed'
    Write-Ready 'Backend dependencies ready.'

    $nodeModules = Join-Path $FrontendRoot 'node_modules'
    if (-not (Test-Path -LiteralPath $nodeModules)) {
        Write-Status 'Frontend node_modules is missing; running npm ci...'
        Invoke-CheckedCommand $npm @('ci', '--prefix', $FrontendRoot) 'Frontend dependency restore failed'
        Write-Ready 'Frontend dependencies installed from package-lock.json.'
    }
    else {
        Write-Ready 'Frontend dependencies already present.'
    }

    Write-Status 'Applying existing database migrations...'
    $env:DOTNET_ENVIRONMENT = 'Development'
    Invoke-CheckedCommand $dotnet @('run', '--project', $MigrationProject, '--no-restore', '--no-launch-profile') 'Database migration failed; application processes were not started'
    Write-Ready 'Database migrations applied.'

    $ownedProcesses = @(Get-ValidLauncherStateEntries)
    $workerAlreadyRunning = $null -ne (Find-RepositoryComponentProcess @('PurpleGlass.Integrations.Worker'))
    if ($workerAlreadyRunning) {
        Write-Status 'PurpleGlass Integration Worker is already running; reusing it.'
    }

    if (-not $webBffAlreadyRunning) {
        $ownedProcesses = @($ownedProcesses | Where-Object { $_.Component -ne 'Web BFF' })
        Write-Status 'Opening the Web BFF log window...'
        $dotnetEscaped = $dotnet.Replace("'", "''")
        $projectEscaped = $WebBffProject.Replace("'", "''")
        $bffCommand = "`$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:ASPNETCORE_URLS='$BackendUrl'; & '$dotnetEscaped' run --project '$projectEscaped' --no-restore --no-launch-profile"
        $ownedProcesses += Start-ComponentTerminal 'Web BFF' 'PurpleGlass — Web BFF' $BackendRoot $bffCommand
        Save-LauncherState $ownedProcesses
    }
    if (-not $workerAlreadyRunning) {
        $ownedProcesses = @($ownedProcesses | Where-Object { $_.Component -ne 'Integration Worker' })
        Write-Status 'Opening the Integration Worker log window...'
        $dotnetEscaped = $dotnet.Replace("'", "''")
        $projectEscaped = $WorkerProject.Replace("'", "''")
        $workerCommand = "`$env:DOTNET_ENVIRONMENT='Development'; & '$dotnetEscaped' run --project '$projectEscaped' --no-restore --no-launch-profile"
        $ownedProcesses += Start-ComponentTerminal 'Integration Worker' 'PurpleGlass — Integration Worker' $BackendRoot $workerCommand
        Save-LauncherState $ownedProcesses
    }
    if (-not $frontendAlreadyRunning) {
        $ownedProcesses = @($ownedProcesses | Where-Object { $_.Component -ne 'Frontend' })
        Write-Status 'Opening the frontend log window...'
        $viteEntryPoint = Join-Path $FrontendRoot 'node_modules\vite\bin\vite.js'
        Assert-Path $viteEntryPoint 'Vite entry point'
        $nodeEscaped = $node.Replace("'", "''")
        $viteEntryPointEscaped = $viteEntryPoint.Replace("'", "''")
        $frontendCommand = "& '$nodeEscaped' '$viteEntryPointEscaped' --host 127.0.0.1"
        $ownedProcesses += Start-ComponentTerminal 'Frontend' 'PurpleGlass — Frontend' $FrontendRoot $frontendCommand
        Save-LauncherState $ownedProcesses
    }

    if ($ownedProcesses.Count -gt 0) { Save-LauncherState $ownedProcesses }

    Wait-ForHttpReady 'Web BFF' "$BackendUrl/health/ready"
    if ($workerAlreadyRunning -or $null -ne (Find-RepositoryComponentProcess @('PurpleGlass.Integrations.Worker'))) {
        Write-Ready 'Integration Worker running.'
    }
    else {
        throw 'Integration Worker exited during startup. Review its titled terminal for details.'
    }
    Wait-ForHttpReady 'Frontend' $FrontendUrl

    if (-not $NoBrowser) {
        Start-Process $FrontendUrl | Out-Null
        Write-Status 'Opened the frontend in the default browser.'
    }

    Write-Host ''
    Write-Host '========================================' -ForegroundColor Magenta
    Write-Host ' PurpleGlass Development Environment' -ForegroundColor Magenta
    Write-Host '========================================' -ForegroundColor Magenta
    Write-Host 'Docker              READY'
    Write-Host 'PostgreSQL          READY'
    Write-Host 'MQTT                READY'
    Write-Host 'Valkey              READY'
    Write-Host 'Migrations          APPLIED'
    Write-Host 'Web BFF             READY'
    Write-Host 'Integration Worker  RUNNING'
    Write-Host 'Frontend            READY'
    Write-Host ''
    Write-Host 'PurpleGlass is running.' -ForegroundColor Green
    Write-Host "Frontend: $FrontendUrl"
    Write-Host "Backend:  $BackendUrl"
    Write-Host ''
    Write-Host 'Use Ctrl+C in a component window to stop only that component.'
    Write-Host 'Run Stop-PurpleGlass.ps1 to stop launcher-owned application processes.'
    Write-Host 'Infrastructure remains running unless the stop helper is passed -IncludeInfrastructure.'
    Write-Host '========================================' -ForegroundColor Magenta
    Set-Location -LiteralPath $OriginalLocation
}
catch {
    Set-Location -LiteralPath $OriginalLocation
    Write-Host ''
    Write-Host 'PurpleGlass cannot start.' -ForegroundColor Red
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    if (Test-Path -LiteralPath $StatePath) {
        Write-Host 'Some launcher-owned component windows may have started.' -ForegroundColor Yellow
        Write-Host 'Run Stop-PurpleGlass.ps1 to stop them safely.' -ForegroundColor Yellow
    }
    Write-Host 'Correct the reported issue and run Start-PurpleGlass.ps1 again.' -ForegroundColor Yellow
    exit 1
}
