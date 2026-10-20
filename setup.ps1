#!/usr/bin/env pwsh
# Engram Memory source installer: Windows PowerShell 5.1 / PowerShell 7+.
param(
    [string] $InstallDir = "",
    [ValidateSet('minimal','standard','full')] [string] $Profile = "",
    [ValidateSet('json','sqlite','sqlserver')] [string] $Storage = "",
    [string] $AgentId = "",
    [string] $For = 'claude',
    [string] $ConfigRoot = "",
    [string] $DataPath = "",
    [string] $LegacyData = "",
    [switch] $ServerStopped,
    [switch] $SkipConfig
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Stop-Setup([string] $Message) { Write-Error $Message; exit 1 }
Write-Host 'Engram Memory setup'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Stop-Setup 'Install .NET SDK 8 or later and retry.' }
$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [int]($dotnetVersion.Split('.')[0]) -lt 8) { Stop-Setup '.NET SDK 8 or later is required.' }
$scriptDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD.Path }
if (Test-Path (Join-Path $scriptDirectory 'McpEngramMemory.slnx')) {
    $repository = $scriptDirectory
} else {
    if (-not $InstallDir) { $InstallDir = Join-Path $HOME 'mcp-engram-memory' }
    if (-not (Test-Path (Join-Path $InstallDir 'McpEngramMemory.slnx'))) {
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Stop-Setup 'Git is required to clone the repository.' }
        & git clone https://github.com/wyckit/mcp-engram-memory.git $InstallDir
        if ($LASTEXITCODE -ne 0) { Stop-Setup 'Clone failed.' }
    }
    $repository = (Resolve-Path $InstallDir).Path
}
# Build only the server target: SDK 8 need not read .slnx or target net9/net10.
$project = Join-Path $repository 'src/McpEngramMemory/McpEngramMemory.csproj'
$output = Join-Path $repository '.engram/server'
Push-Location $repository
try {
    & dotnet publish $project -c Release -f net8.0 -p:TargetFrameworks=net8.0 -o $output --nologo
    if ($LASTEXITCODE -ne 0) { Stop-Setup 'Server build failed; client configuration was not changed.' }
} finally { Pop-Location }
$server = Join-Path $output 'McpEngramMemory.dll'
$replaceDataPath = [bool]$DataPath
if (-not $DataPath) {
    $DataPath = (& dotnet $server data-path --plain)
    if ($LASTEXITCODE -ne 0) { Stop-Setup 'Could not resolve the durable data directory.' }
    $DataPath = $DataPath.Trim()
}
$prepareArguments = @($server, 'prepare-data', '--repo', $repository, '--destination', $DataPath)
if ($LegacyData) { $prepareArguments += @('--source', $LegacyData) }
if ($ServerStopped) { $prepareArguments += '--server-stopped' }
& dotnet @prepareArguments
if ($LASTEXITCODE -ne 0) { Stop-Setup 'Existing data needs migration. Stop the old server, select -LegacyData if needed, and rerun with -ServerStopped. Client configuration was not changed.' }
if ($SkipConfig) {
    Write-Host "Build complete. command: dotnet; args: $server"
    Write-Host "Set MEMORY_DATA_PATH=$DataPath and MEMORY_TOOL_PROFILE=$Profile."
    exit 0
}
if (-not $ConfigRoot) { $ConfigRoot = $HOME }
$configureArguments = @($server, 'configure-client', '--root', $ConfigRoot, '--for', $For,
    '--server', $server, '--data-path', $DataPath)
if ($Profile) { $configureArguments += @('--profile', $Profile) }
if ($Storage) { $configureArguments += @('--storage', $Storage) }
if ($replaceDataPath) { $configureArguments += @('--replace-data-path', 'true') }
if ($AgentId) { $configureArguments += @('--agent-id', $AgentId) }
& dotnet @configureArguments
if ($LASTEXITCODE -ne 0) { Stop-Setup 'Configuration failed. Review the error and any reported backup paths.' }
$toolCount = switch ($Profile) { "minimal" { 17 } "standard" { 39 } default { 63 } }
Write-Host 'Setup complete. Existing profile, storage and path selections are preserved unless explicitly supplied.'
if ($Profile) { Write-Host "Profile: $Profile | Tools: $toolCount" }
Write-Host "Default durable data: $DataPath"
Write-Host 'Restart the configured clients to load Engram. Existing configuration backups are listed above.'
