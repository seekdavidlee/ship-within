#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Starts the local Ship Within frontend and backend.
.DESCRIPTION
    Builds and launches the .NET API and React Vite dev server on free loopback ports.
    Runs each service in a managed terminal and tracks both terminals in an ignored PID file.
.PARAMETER TerminalService
    Internal mode used to run one service in its own managed terminal.
.EXAMPLE
    ./Start-App.ps1
.NOTES
    Requires .NET 10, Node.js 18+, and npm.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('Backend', 'Frontend')]
    [string]$TerminalService,

    [Parameter(Mandatory = $false)]
    [int]$ApiPort,

    [Parameter(Mandatory = $false)]
    [int]$FrontendPort
)

$ErrorActionPreference = 'Stop'

#region Functions
function Get-AppProcess {
    param([int]$ProcessId, [string]$ExpectedArgument)

    $Process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if ($Process -and $Process.CommandLine -and $Process.CommandLine.Contains($ExpectedArgument, [StringComparison]::OrdinalIgnoreCase)) {
        return $Process
    }
    return $null
}

function Get-AppTerminal {
    param([int]$ProcessId, [string]$Service, [string]$ScriptPath)

    $Process = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if ($Process -and $Process.Name -ieq 'pwsh.exe' -and $Process.CommandLine -and
        $Process.CommandLine.Contains($ScriptPath, [StringComparison]::OrdinalIgnoreCase) -and
        $Process.CommandLine.Contains("-TerminalService $Service", [StringComparison]::OrdinalIgnoreCase)) {
        return $Process
    }
    return $null
}

function Stop-AppTerminalTree {
    param([int]$ProcessId)

    $TaskKill = Get-Command taskkill.exe -ErrorAction SilentlyContinue
    if ($TaskKill) {
        & $TaskKill.Source /PID $ProcessId /T /F 2>$null | Out-Null
    }
}

function Stop-AppServices {
    param($Saved, [string]$BackendDll, [string]$FrontendEntry, [string]$ScriptPath)

    foreach ($Service in @(
        @{ Name = 'Backend'; TerminalId = $Saved.backendTerminalPid; ProcessId = $Saved.backendPid; Argument = $BackendDll },
        @{ Name = 'Frontend'; TerminalId = $Saved.frontendTerminalPid; ProcessId = $Saved.frontendPid; Argument = $FrontendEntry }
    )) {
        if ($Service.TerminalId) {
            $Terminal = Get-AppTerminal -ProcessId $Service.TerminalId -Service $Service.Name -ScriptPath $ScriptPath
            if ($Terminal) {
                Stop-AppTerminalTree -ProcessId $Terminal.ProcessId
                Wait-Process -Id $Terminal.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
            }
        }
        if ($Service.ProcessId) {
            $Process = Get-AppProcess -ProcessId $Service.ProcessId -ExpectedArgument $Service.Argument
            if ($Process) {
                Stop-Process -Id $Process.ProcessId -ErrorAction SilentlyContinue
                Wait-Process -Id $Process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
            }
        }
    }
}

function Get-RunningAppProcesses {
    param([string]$BackendDll, [string]$BackendExe, [string]$FrontendEntry)

    Get-CimInstance Win32_Process -Filter "Name = 'ShipWithin.Api.exe' OR Name = 'dotnet.exe' OR Name = 'node.exe'" | Where-Object {
        ($_.Name -ieq 'ShipWithin.Api.exe' -and $_.ExecutablePath -ieq $BackendExe) -or
        ($_.Name -ieq 'dotnet.exe' -and $_.CommandLine -and $_.CommandLine.Contains($BackendDll, [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.Name -ieq 'node.exe' -and $_.CommandLine -and $_.CommandLine.Contains($FrontendEntry, [StringComparison]::OrdinalIgnoreCase))
    }
}

function Get-FreePort {
    param([int]$Preferred)

    foreach ($Candidate in @($Preferred, 0)) {
        try {
            $Listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Candidate)
            $Listener.Start()
            $Port = $Listener.LocalEndpoint.Port
            $Listener.Stop()
            return $Port
        }
        catch [System.Net.Sockets.SocketException] {
            if ($Candidate -eq 0) { throw }
        }
    }
}
#endregion Functions

#region Main Execution
if ($TerminalService) {
    $env:SHIP_WITHIN_API_PORT = [string]$ApiPort
    $env:SHIP_WITHIN_FRONTEND_PORT = [string]$FrontendPort
    $CopilotCli = Get-Command copilot.exe -ErrorAction SilentlyContinue
    if ($CopilotCli) { $env:COPILOT_CLI_PATH = $CopilotCli.Source }

    try {
        if ($TerminalService -eq 'Backend') {
            $ServiceEntry = Join-Path $PSScriptRoot 'src/backend/bin/Debug/net10.0/ShipWithin.Api.dll'
            & dotnet $ServiceEntry
        }
        else {
            $ServiceEntry = Join-Path $PSScriptRoot 'src/frontend/node_modules/vite/bin/vite.js'
            & node $ServiceEntry
        }
        if ($LASTEXITCODE -ne 0) { throw "$TerminalService exited with code $LASTEXITCODE." }
    }
    catch {
        Write-Error -ErrorAction Continue "$TerminalService terminal failed: $($_.Exception.Message)"
    }
    return
}

if ($MyInvocation.InvocationName -ne '.') {
    $RepoRoot = $PSScriptRoot
    $DataDir = Join-Path $RepoRoot 'data'
    $PidFile = Join-Path $DataDir 'app-processes.json'
    $BackendDll = Join-Path $RepoRoot 'src/backend/bin/Debug/net10.0/ShipWithin.Api.dll'
    $BackendExe = Join-Path $RepoRoot 'src/backend/bin/Debug/net10.0/ShipWithin.Api.exe'
    $FrontendEntry = Join-Path $RepoRoot 'src/frontend/node_modules/vite/bin/vite.js'
    $BackendProject = Join-Path $RepoRoot 'src/backend/ShipWithin.Api.csproj'
    $FrontendDir = Join-Path $RepoRoot 'src/frontend'

    try {
        if (Test-Path $PidFile) {
            $Saved = Get-Content $PidFile -Raw | ConvertFrom-Json
            $BackendTerminal = $null
            $FrontendTerminal = $null
            $Backend = $null
            $Frontend = $null
            if ($Saved.backendTerminalPid) {
                $BackendTerminal = Get-AppTerminal -ProcessId $Saved.backendTerminalPid -Service 'Backend' -ScriptPath $PSCommandPath
            }
            if ($Saved.frontendTerminalPid) {
                $FrontendTerminal = Get-AppTerminal -ProcessId $Saved.frontendTerminalPid -Service 'Frontend' -ScriptPath $PSCommandPath
            }
            if ($Saved.backendPid) { $Backend = Get-AppProcess -ProcessId $Saved.backendPid -ExpectedArgument $BackendDll }
            if ($Saved.frontendPid) { $Frontend = Get-AppProcess -ProcessId $Saved.frontendPid -ExpectedArgument $FrontendEntry }
            if ($BackendTerminal -or $FrontendTerminal -or $Backend -or $Frontend) {
                Write-Host 'Stopping existing Ship Within services before starting new ones.'
                Stop-AppServices -Saved $Saved -BackendDll $BackendDll -FrontendEntry $FrontendEntry -ScriptPath $PSCommandPath
                if (($Saved.backendTerminalPid -and (Get-AppTerminal -ProcessId $Saved.backendTerminalPid -Service 'Backend' -ScriptPath $PSCommandPath)) -or
                    ($Saved.frontendTerminalPid -and (Get-AppTerminal -ProcessId $Saved.frontendTerminalPid -Service 'Frontend' -ScriptPath $PSCommandPath)) -or
                    ($Saved.backendPid -and (Get-AppProcess -ProcessId $Saved.backendPid -ExpectedArgument $BackendDll)) -or
                    ($Saved.frontendPid -and (Get-AppProcess -ProcessId $Saved.frontendPid -ExpectedArgument $FrontendEntry))) {
                    throw 'Could not stop the existing Ship Within services.'
                }
            }
            Remove-Item $PidFile
        }

        $Running = @(Get-RunningAppProcesses -BackendDll $BackendDll -BackendExe $BackendExe -FrontendEntry $FrontendEntry)
        if ($Running.Count) {
            Write-Host 'Stopping existing Ship Within processes before building.'
            foreach ($Process in $Running) {
                Stop-Process -Id $Process.ProcessId -ErrorAction SilentlyContinue
                Wait-Process -Id $Process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
            }
            if (@(Get-RunningAppProcesses -BackendDll $BackendDll -BackendExe $BackendExe -FrontendEntry $FrontendEntry).Count) {
                throw 'Could not stop the existing Ship Within processes.'
            }
        }

        foreach ($Tool in @('dotnet', 'node', 'npm')) {
            if (-not (Get-Command $Tool -ErrorAction SilentlyContinue)) { throw "Missing prerequisite: $Tool" }
        }
        $DotnetVersion = (& dotnet --version)
        $NodeVersion = (& node --version).TrimStart('v')
        if ([version]$DotnetVersion -lt [version]'10.0' -or [version]$NodeVersion -lt [version]'18.0.0') {
            throw 'Install .NET SDK 10 and Node.js 18 or newer before starting.'
        }
        if (-not (Test-Path (Join-Path $FrontendDir 'node_modules/vite'))) {
            & npm install --prefix $FrontendDir
            if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
        }
        & dotnet build $BackendProject --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }

        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
        $ApiPort = Get-FreePort -Preferred 4174
        $UiPort = Get-FreePort -Preferred 5173
        $PowerShellPath = (Get-Command pwsh).Source
        $BackendTerminal = $null
        $FrontendTerminal = $null
        try {
            $BackendTerminal = Start-Process -FilePath $PowerShellPath -ArgumentList @('-NoProfile', '-NoExit', '-File', "`"$PSCommandPath`"", '-TerminalService', 'Backend', '-ApiPort', "$ApiPort", '-FrontendPort', "$UiPort") -WorkingDirectory $RepoRoot -PassThru
            $FrontendTerminal = Start-Process -FilePath $PowerShellPath -ArgumentList @('-NoProfile', '-NoExit', '-File', "`"$PSCommandPath`"", '-TerminalService', 'Frontend', '-ApiPort', "$ApiPort", '-FrontendPort', "$UiPort") -WorkingDirectory $FrontendDir -PassThru
            $Saved = @{ backendTerminalPid = $BackendTerminal.Id; frontendTerminalPid = $FrontendTerminal.Id; apiPort = $ApiPort; frontendPort = $UiPort }
            $Saved | ConvertTo-Json | Set-Content $PidFile -Encoding utf8
        }
        catch {
            if ($BackendTerminal) { Stop-AppTerminalTree -ProcessId $BackendTerminal.Id }
            if ($FrontendTerminal) { Stop-AppTerminalTree -ProcessId $FrontendTerminal.Id }
            throw
        }
        Write-Host "Ship Within: http://127.0.0.1:$UiPort (API: http://127.0.0.1:$ApiPort)"
        Write-Host 'Backend and React are running in separate terminals.'
        Write-Host 'Enter to stop both services.'
        try {
            [void](Read-Host)
        }
        finally {
            Stop-AppServices -Saved $Saved -BackendDll $BackendDll -FrontendEntry $FrontendEntry -ScriptPath $PSCommandPath
            Remove-Item $PidFile -ErrorAction SilentlyContinue
        }
        Write-Host 'Ship Within frontend and backend stopped.'
    }
    catch {
        Write-Error -ErrorAction Continue "Start-App failed: $($_.Exception.Message)"
        exit 1
    }
}
#endregion Main Execution