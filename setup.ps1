#!/usr/bin/env pwsh
# Engram Memory -- one-click setup
#
# Works on Windows (powershell / pwsh) and macOS / Linux (pwsh / PowerShell Core)
#
# Fresh install:
#   Windows:  irm https://raw.githubusercontent.com/wyckit/mcp-engram-memory/main/setup.ps1 | iex
#   pwsh:     curl -fsSL https://raw.githubusercontent.com/wyckit/mcp-engram-memory/main/setup.ps1 | pwsh -
#
# Already cloned:
#   pwsh setup.ps1
#   pwsh setup.ps1 -For gemini,codex -Profile standard
#   pwsh setup.ps1 -For all

param(
    [string] $InstallDir  = "",         # Where to clone  (default: ~/mcp-engram-memory)
    [string] $Profile     = "minimal",  # minimal | standard | full
    [string] $Storage     = "json",     # json | sqlite
    [string] $AgentId     = "",         # AGENT_ID for multi-agent setups
    [string] $For         = "claude",   # claude | gemini | codex | all  (comma-separated)
    [switch] $SkipConfig               # Don't touch any config files
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step  { param($msg) Write-Host "  $msg" -ForegroundColor Cyan }
function Write-Ok    { param($msg) Write-Host "  [ok] $msg" -ForegroundColor Green }
function Write-Skip  { param($msg) Write-Host "  [--] $msg" -ForegroundColor DarkGray }
function Write-Warn  { param($msg) Write-Host "  [!]  $msg" -ForegroundColor Yellow }
function Write-Fatal { param($msg) Write-Host "`n  [ERROR] $msg`n" -ForegroundColor Red; exit 1 }

# Resolve which tools to configure
$targets = @()
if ($For -eq "all") {
    $targets = @("claude", "gemini", "codex")
} else {
    $targets = $For -split "," | ForEach-Object { $_.Trim().ToLower() }
}

Write-Host ""
Write-Host "  Engram Memory -- setup" -ForegroundColor White
Write-Host "  -----------------------------------------" -ForegroundColor DarkGray
Write-Host "  Configuring: $($targets -join ', ')" -ForegroundColor DarkGray
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
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Fatal "git not found. Install git and re-run." }
        git clone https://github.com/wyckit/mcp-engram-memory.git $InstallDir --quiet 2>&1
        if ($LASTEXITCODE -ne 0) { Write-Fatal "git clone failed." }
        $RepoRoot = $InstallDir
        Write-Ok "Cloned to $RepoRoot"
    }
}

# ── 3. Restore ────────────────────────────────────────────────────────────────
Write-Step "Restoring packages...  first run downloads ~5.7 MB ONNX model"
Push-Location $RepoRoot
try {
    dotnet restore McpEngramMemory.slnx --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Fatal "Restore failed. Run 'dotnet restore' in $RepoRoot for details." }
} finally { Pop-Location }
Write-Ok "Packages restored"

# ── 4. Project path ───────────────────────────────────────────────────────────
$ProjectPath = Join-Path (Join-Path $RepoRoot "src") "McpEngramMemory"
$ProjectPathFwd = $ProjectPath -replace '\\', '/'   # forward slashes for JSON/TOML
Write-Ok "Project at $ProjectPathFwd"

if ($SkipConfig) {
    Write-Warn "-SkipConfig: not modifying any config files."
    Write-Host "  Add this entry to your tool's MCP server config:" -ForegroundColor Gray
    Write-Host "    command: dotnet" -ForegroundColor Gray
    Write-Host "    args:    run --project $ProjectPathFwd" -ForegroundColor Gray
    Write-Host "    env:     MEMORY_TOOL_PROFILE=$Profile" -ForegroundColor Gray
    Write-Host ""
    exit 0
}

# ── Helper: patch a JSON file with mcpServers key (Claude / Gemini format) ────
function Set-McpJson {
    param([string]$ConfigPath, [string]$Label)

    $envHash = [ordered]@{ MEMORY_TOOL_PROFILE = $Profile; MEMORY_STORAGE = $Storage }
    if ($AgentId) { $envHash["AGENT_ID"] = $AgentId }

    $entry = [PSCustomObject]@{
        type    = "stdio"
        command = "dotnet"
        args    = @("run", "--project", $ProjectPathFwd)
        env     = [PSCustomObject]$envHash
    }

    if (Test-Path $ConfigPath) {
        $raw = Get-Content $ConfigPath -Raw
        try   { $cfg = $raw | ConvertFrom-Json -Depth 20 }
        catch { Write-Warn "Could not parse $ConfigPath -- adding mcpServers section."; $cfg = [PSCustomObject]@{} }
    } else {
        $cfg = [PSCustomObject]@{}
    }

    if (-not ($cfg.PSObject.Properties.Name -contains "mcpServers")) {
        $cfg | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue ([PSCustomObject]@{})
    }

    if ($cfg.mcpServers.PSObject.Properties.Name -contains "engram-memory") {
        $cfg.mcpServers."engram-memory" = $entry
        Write-Ok "${Label}: updated existing entry in $ConfigPath"
    } else {
        $cfg.mcpServers | Add-Member -NotePropertyName "engram-memory" -NotePropertyValue $entry
        Write-Ok "${Label}: added entry to $ConfigPath"
    }

    $cfg | ConvertTo-Json -Depth 20 | Set-Content $ConfigPath -Encoding UTF8
}

# ── Helper: patch ~/.codex/config.toml ────────────────────────────────────────
function Set-McpToml {
    param([string]$ConfigPath)

    $args = "['run', '--project', '$($ProjectPathFwd -replace "'", "''")']"
    $envLines = "MEMORY_TOOL_PROFILE = `"$Profile`"`nMEMORY_STORAGE = `"$Storage`""
    if ($AgentId) { $envLines += "`nAGENT_ID = `"$AgentId`"" }

    $newSection = @"

[mcp_servers.engram-memory]
command = "dotnet"
args = $args

[mcp_servers.engram-memory.env]
$envLines
"@

    if (Test-Path $ConfigPath) {
        # Remove existing engram-memory sections line-by-line (safe with TOML arrays)
        $lines      = Get-Content $ConfigPath
        $inSection  = $false
        $kept       = [System.Collections.Generic.List[string]]::new()
        foreach ($line in $lines) {
            $trimmed = $line.Trim()
            if ($trimmed -match '^\[mcp_servers\.engram-memory') {
                $inSection = $true
            } elseif ($trimmed -match '^\[' -and $trimmed -notmatch '^\[mcp_servers\.engram-memory') {
                $inSection = $false
            }
            if (-not $inSection) { $kept.Add($line) }
        }
        $content = ($kept -join "`n").TrimEnd()
        Set-Content $ConfigPath -Value ($content + $newSection) -Encoding utf8NoBOM
        Write-Ok "Codex: updated $ConfigPath"
    } else {
        # Create minimal config
        $dir = Split-Path $ConfigPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        Set-Content $ConfigPath -Value $newSection.TrimStart() -Encoding utf8NoBOM
        Write-Ok "Codex: created $ConfigPath"
    }
}

# ── 5. Configure each target tool ─────────────────────────────────────────────
Write-Step "Patching config files..."

foreach ($tool in $targets) {
    switch ($tool) {
        "claude" {
            $path = Join-Path $HOME ".claude.json"
            Set-McpJson -ConfigPath $path -Label "Claude Code"
        }
        "gemini" {
            $geminiDir = Join-Path $HOME ".gemini"
            $path = Join-Path $geminiDir "settings.json"
            if (-not (Test-Path $geminiDir)) {
                New-Item -ItemType Directory -Path $geminiDir | Out-Null
            }
            Set-McpJson -ConfigPath $path -Label "Gemini CLI"
        }
        "codex" {
            $path = Join-Path (Join-Path $HOME ".codex") "config.toml"
            Set-McpToml -ConfigPath $path
        }
        default {
            Write-Warn "Unknown tool '$tool' -- skipping. Valid values: claude, gemini, codex, all"
        }
    }
}

# ── 6. Done ───────────────────────────────────────────────────────────────────
$toolCount = switch ($Profile) { "minimal" { 16 } "standard" { 41 } default { 65 } }

Write-Host ""
Write-Host "  -----------------------------------------" -ForegroundColor DarkGray
Write-Host "  Setup complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor White
Write-Host "   1. Restart configured tools to reload MCP servers" -ForegroundColor Gray
Write-Host "   2. The 'engram-memory' server will appear in your tools" -ForegroundColor Gray
Write-Host "   3. Copy examples/CLAUDE.md  -> ~/.claude/CLAUDE.md  (Claude Code)" -ForegroundColor Gray
Write-Host "      Copy GEMINI.md           -> workspace GEMINI.md  (Gemini CLI)" -ForegroundColor Gray
Write-Host "      Copy examples/AGENTS.md  -> project AGENTS.md    (Codex)" -ForegroundColor Gray
Write-Host ""
Write-Host "  Profile: $Profile  |  Storage: $Storage  |  Tools: $toolCount" -ForegroundColor DarkGray
Write-Host ""
