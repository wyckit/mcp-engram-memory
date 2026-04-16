#!/usr/bin/env pwsh
# Engram Memory -- one-click setup for Claude Code
#
# Works on Windows (powershell / pwsh) and macOS / Linux (pwsh / PowerShell Core)
#
# Fresh install -- run from anywhere:
#   Windows PowerShell:  irm https://raw.githubusercontent.com/wyckit/mcp-engram-memory/main/setup.ps1 | iex
#   PowerShell Core:     curl -fsSL https://raw.githubusercontent.com/wyckit/mcp-engram-memory/main/setup.ps1 | pwsh -
#
# Already cloned:
#   pwsh setup.ps1
#   pwsh setup.ps1 -Profile standard -Storage sqlite

param(
    [string] $InstallDir  = "",        # Where to clone  (default: ~/mcp-engram-memory)
    [string] $Profile     = "minimal", # minimal | standard | full
    [string] $Storage     = "json",    # json | sqlite
    [string] $AgentId     = "",        # AGENT_ID for multi-agent setups
    [switch] $SkipConfig              # Don't touch ~/.claude.json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step  { param($msg) Write-Host "  $msg" -ForegroundColor Cyan }
function Write-Ok    { param($msg) Write-Host "  [ok] $msg" -ForegroundColor Green }
function Write-Warn  { param($msg) Write-Host "  [!]  $msg" -ForegroundColor Yellow }
function Write-Fatal { param($msg) Write-Host "`n  [ERROR] $msg`n" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "  Engram Memory -- setup" -ForegroundColor White
Write-Host "  -----------------------------------------" -ForegroundColor DarkGray
Write-Host ""

# ── 1. .NET SDK ───────────────────────────────────────────────────────────────
Write-Step "Checking .NET SDK..."
try {
    $dotnetVer = (dotnet --version 2>&1).Trim()
    $major = [int]($dotnetVer -split '\.')[0]
    if ($major -lt 8) {
        Write-Fatal ".NET SDK $dotnetVer found but >= 8.0 required. Install from https://dot.net"
    }
    Write-Ok ".NET SDK $dotnetVer"
} catch {
    Write-Fatal ".NET SDK not found. Install from https://dot.net and re-run."
}

# ── 2. Locate or clone ────────────────────────────────────────────────────────
Write-Step "Locating repo..."

$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD.Path }
$RepoRoot  = ""

if (Test-Path (Join-Path $ScriptDir "McpEngramMemory.slnx")) {
    $RepoRoot = $ScriptDir
    Write-Ok "Using existing repo at $RepoRoot"
} else {
    if (-not $InstallDir) { $InstallDir = Join-Path $HOME "mcp-engram-memory" }

    if (Test-Path (Join-Path $InstallDir "McpEngramMemory.slnx")) {
        $RepoRoot = $InstallDir
        Write-Ok "Using existing clone at $RepoRoot"
    } else {
        Write-Step "Cloning into $InstallDir ..."
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
            Write-Fatal "git not found. Install git and re-run."
        }
        git clone https://github.com/wyckit/mcp-engram-memory.git $InstallDir --quiet 2>&1
        if ($LASTEXITCODE -ne 0) { Write-Fatal "git clone failed." }
        $RepoRoot = $InstallDir
        Write-Ok "Cloned to $RepoRoot"
    }
}

# ── 3. Restore (downloads ONNX model) ────────────────────────────────────────
Write-Step "Restoring packages...  first run downloads ~5.7 MB ONNX model"
Push-Location $RepoRoot
try {
    dotnet restore McpEngramMemory.slnx --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Fatal "Restore failed. Run 'dotnet restore' in $RepoRoot for details."
    }
} finally {
    Pop-Location
}
Write-Ok "Packages restored"

# ── 4. Project path (used in MCP config) ─────────────────────────────────────
$ProjectPath = Join-Path (Join-Path $RepoRoot "src") "McpEngramMemory"

# Normalise to forward slashes for JSON compatibility across platforms
$ProjectPathJson = $ProjectPath -replace '\\', '/'

Write-Ok "Project at $ProjectPathJson"

# ── 5. Patch ~/.claude.json ───────────────────────────────────────────────────
$ClaudeJson = Join-Path $HOME ".claude.json"

if (-not $SkipConfig) {
    Write-Step "Patching $ClaudeJson ..."

    # Load or initialise
    if (Test-Path $ClaudeJson) {
        $raw = Get-Content $ClaudeJson -Raw
        try   { $cfg = $raw | ConvertFrom-Json -Depth 20 }
        catch { Write-Warn "Could not parse $ClaudeJson -- adding mcpServers section only."; $cfg = [PSCustomObject]@{} }
    } else {
        $cfg = [PSCustomObject]@{}
    }

    # Ensure mcpServers exists
    if (-not ($cfg.PSObject.Properties.Name -contains "mcpServers")) {
        $cfg | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue ([PSCustomObject]@{})
    }

    # Build env block
    $envHash = [ordered]@{ MEMORY_TOOL_PROFILE = $Profile; MEMORY_STORAGE = $Storage }
    if ($AgentId) { $envHash["AGENT_ID"] = $AgentId }

    # MCP server entry uses `dotnet run --project` -- no pre-built DLL required,
    # no lock conflicts when the server is already running.
    $entry = [PSCustomObject]@{
        type    = "stdio"
        command = "dotnet"
        args    = @("run", "--project", $ProjectPathJson)
        env     = [PSCustomObject]$envHash
    }

    # Upsert
    if ($cfg.mcpServers.PSObject.Properties.Name -contains "engram-memory") {
        $cfg.mcpServers."engram-memory" = $entry
        Write-Ok "Updated existing engram-memory entry"
    } else {
        $cfg.mcpServers | Add-Member -NotePropertyName "engram-memory" -NotePropertyValue $entry
        Write-Ok "Added engram-memory entry"
    }

    # Write back (depth 20 prevents value truncation)
    $cfg | ConvertTo-Json -Depth 20 | Set-Content $ClaudeJson -Encoding utf8NoBOM
    Write-Ok "Saved $ClaudeJson"

} else {
    Write-Warn "-SkipConfig: skipping $ClaudeJson. Add this to your mcpServers block:"
    Write-Host ""
    Write-Host "  `"engram-memory`": {" -ForegroundColor Gray
    Write-Host "    `"type`": `"stdio`"," -ForegroundColor Gray
    Write-Host "    `"command`": `"dotnet`"," -ForegroundColor Gray
    Write-Host "    `"args`": [`"run`", `"--project`", `"$ProjectPathJson`"]," -ForegroundColor Gray
    Write-Host "    `"env`": { `"MEMORY_TOOL_PROFILE`": `"$Profile`" }" -ForegroundColor Gray
    Write-Host "  }" -ForegroundColor Gray
    Write-Host ""
}

# ── 6. Done ───────────────────────────────────────────────────────────────────
$toolCount = switch ($Profile) { "minimal" { 16 } "standard" { 35 } default { 52 } }

Write-Host ""
Write-Host "  -----------------------------------------" -ForegroundColor DarkGray
Write-Host "  Setup complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor White
Write-Host "   1. Restart Claude Code (or reload MCP servers)" -ForegroundColor Gray
Write-Host "   2. The 'engram-memory' server will appear in your tools" -ForegroundColor Gray
Write-Host "   3. Copy examples/CLAUDE.md to ~/.claude/CLAUDE.md for full integration" -ForegroundColor Gray
Write-Host ""
Write-Host "  Profile: $Profile  |  Storage: $Storage  |  Tools: $toolCount" -ForegroundColor DarkGray
Write-Host "  Change profile: set MEMORY_TOOL_PROFILE in $ClaudeJson" -ForegroundColor DarkGray
Write-Host ""
