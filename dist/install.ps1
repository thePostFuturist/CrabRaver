#Requires -Version 5.1
<#
.SYNOPSIS
    DigitRaver Bridge MCP — PowerShell installer for OpenClaw users.

.DESCRIPTION
    Downloads the Bridge MCP server binary and agent skill, then configures
    OpenClaw to use them.

.PARAMETER Uninstall
    Remove the binary, skill, and openclaw.json server entry.

.PARAMETER Help
    Show usage information.

.EXAMPLE
    # Install (piped from web)
    irm https://github.com/REPO/releases/download/vVERSION/install.ps1 | iex

    # Install (saved locally)
    .\install.ps1

    # Uninstall
    .\install.ps1 -Uninstall

.NOTES
    Environment variables:
      GITHUB_TOKEN    — auth token for private repo downloads
      BRIDGE_VERSION  — override version (default: from VERSION file)
      BRIDGE_REPO     — override GitHub repo (default: thePostFuturist/CrabRaver)
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

# ── Configuration ────────────────────────────────────────────────────
$ScriptDir   = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD.Path }
$VersionFile = Join-Path $ScriptDir 'VERSION'
$Version     = if ($env:BRIDGE_VERSION) {
    $env:BRIDGE_VERSION
} elseif (Test-Path $VersionFile) {
    (Get-Content $VersionFile -Raw).Trim()
} else {
    '1.0.0'
}

$Repo        = if ($env:BRIDGE_REPO) { $env:BRIDGE_REPO } else { 'thePostFuturist/CrabRaver' }
$BinaryName  = 'DigitRaverHelperMCP'
$SkillName   = 'digitraver-agent'

$BridgeDir      = Join-Path $HOME '.digitraver' 'mcp' 'bridge'
$OpenClawDir    = Join-Path $HOME '.openclaw'
$OpenClawConfig = Join-Path $OpenClawDir 'openclaw.json'
$SkillDir       = Join-Path $OpenClawDir 'skills' $SkillName

# ── Helpers ──────────────────────────────────────────────────────────
function Write-Info  { param([string]$Msg) Write-Host "[install] $Msg" }
function Write-Err   { param([string]$Msg) Write-Host "[install] ERROR: $Msg" -ForegroundColor Red }

function Download-Asset {
    param(
        [string]$AssetName,
        [string]$Dest
    )
    $url = "https://github.com/$Repo/releases/download/v$Version/$AssetName"
    Write-Info "Downloading: $AssetName"

    $headers = @{}
    if ($env:GITHUB_TOKEN) {
        $headers['Authorization'] = "token $($env:GITHUB_TOKEN)"
        $headers['Accept']        = 'application/octet-stream'
    }

    try {
        $ProgressPreference = 'SilentlyContinue'   # speed up Invoke-WebRequest
        Invoke-WebRequest -Uri $url -OutFile $Dest -Headers $headers -UseBasicParsing
    }
    catch {
        if (Test-Path $Dest) { Remove-Item $Dest -Force }
        Write-Err "Download failed: $url"
        if (-not $env:GITHUB_TOKEN) {
            Write-Info 'If this is a private repo, set GITHUB_TOKEN:'
            Write-Info '  $env:GITHUB_TOKEN = "ghp_xxx"; .\install.ps1'
        }
        throw
    }
}

# ── Show help ────────────────────────────────────────────────────────
if ($Help) {
    Write-Host @"
Usage: install.ps1 [-Uninstall] [-Help]

Installs DigitRaver Bridge MCP server for OpenClaw.

Options:
  -Uninstall   Remove binary, skill, and config entry
  -Help        Show this help

Environment:
  GITHUB_TOKEN     Auth token for private repo downloads
  BRIDGE_VERSION   Override version (default: $Version)
  BRIDGE_REPO      Override GitHub repo (default: $Repo)
"@
    return
}

# ── Uninstall ────────────────────────────────────────────────────────
if ($Uninstall) {
    Write-Info 'Uninstalling DigitRaver Bridge MCP...'

    # Remove binary directory
    $binDir = Join-Path $BridgeDir $Version
    if (Test-Path $binDir) {
        Remove-Item $binDir -Recurse -Force
        Write-Info "Removed: $binDir"
    }
    # Clean empty parents
    foreach ($d in $BridgeDir, (Join-Path $HOME '.digitraver' 'mcp'), (Join-Path $HOME '.digitraver')) {
        if ((Test-Path $d) -and @(Get-ChildItem $d -Force).Count -eq 0) {
            Remove-Item $d -Force
        }
    }

    # Remove skill
    if (Test-Path $SkillDir) {
        Remove-Item $SkillDir -Recurse -Force
        Write-Info "Removed: $SkillDir"
    }

    # Patch openclaw.json — remove bridge server entry
    if (Test-Path $OpenClawConfig) {
        try {
            $cfg = Get-Content $OpenClawConfig -Raw | ConvertFrom-Json
            $servers = $cfg.plugins.entries.'mcp-adapter'.config.servers
            if ($servers) {
                $cfg.plugins.entries.'mcp-adapter'.config.servers = @($servers | Where-Object { $_.name -ne 'digitraver-bridge' })
                $cfg | ConvertTo-Json -Depth 20 | Set-Content $OpenClawConfig -Encoding UTF8
                Write-Info 'Removed bridge server entry from openclaw.json'
            }
        }
        catch {
            Write-Info "Could not patch openclaw.json: $_"
        }
    }

    Write-Info 'Uninstall complete.'
    return
}

# ── Install ──────────────────────────────────────────────────────────
$RID   = 'win-x64'
$Asset = "$BinaryName.exe"

Write-Info "Installing DigitRaver Bridge MCP v$Version"
Write-Info "  Platform:  $RID"
Write-Info "  Binary:    $Asset"
Write-Host ''

# 1. Download binary
$binDir   = Join-Path $BridgeDir $Version $RID
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
$localExe = "$BinaryName.exe"

Download-Asset -AssetName $Asset -Dest (Join-Path $binDir $localExe)
Write-Info "Binary installed: $(Join-Path $binDir $localExe)"

# 2. Download SKILL.md
New-Item -ItemType Directory -Path $SkillDir -Force | Out-Null
Download-Asset -AssetName 'SKILL.md' -Dest (Join-Path $SkillDir 'SKILL.md')
Write-Info "Skill installed: $(Join-Path $SkillDir 'SKILL.md')"

# 3. Install mcp-adapter plugin
$openclawCmd = Get-Command openclaw -ErrorAction SilentlyContinue
if ($openclawCmd) {
    $plugins = & openclaw plugins list 2>$null
    if ($plugins -match 'mcp-adapter') {
        Write-Info 'mcp-adapter plugin already installed'
    }
    else {
        Write-Info 'Installing mcp-adapter plugin...'
        try { & openclaw plugins install mcp-adapter }
        catch { Write-Info 'Warning: could not install mcp-adapter plugin automatically' }
    }
}
else {
    Write-Info "Warning: 'openclaw' CLI not found. Install the mcp-adapter plugin manually:"
    Write-Info '  openclaw plugins install mcp-adapter'
}

# 4. Patch openclaw.json
$binaryPath = (Join-Path $binDir $localExe).Replace('\', '/')

if (-not (Test-Path $OpenClawDir)) { New-Item -ItemType Directory -Path $OpenClawDir -Force | Out-Null }

if (Test-Path $OpenClawConfig) {
    $cfg = Get-Content $OpenClawConfig -Raw | ConvertFrom-Json
}
else {
    $cfg = [PSCustomObject]@{}
}

# Ensure structure
if (-not $cfg.plugins)                                     { $cfg | Add-Member -NotePropertyName plugins -NotePropertyValue ([PSCustomObject]@{}) }
if (-not $cfg.plugins.entries)                             { $cfg.plugins | Add-Member -NotePropertyName entries -NotePropertyValue ([PSCustomObject]@{}) }
if (-not $cfg.plugins.entries.'mcp-adapter')               { $cfg.plugins.entries | Add-Member -NotePropertyName 'mcp-adapter' -NotePropertyValue ([PSCustomObject]@{}) }
$cfg.plugins.entries.'mcp-adapter' | Add-Member -NotePropertyName enabled -NotePropertyValue $true -Force
if (-not $cfg.plugins.entries.'mcp-adapter'.config)        { $cfg.plugins.entries.'mcp-adapter' | Add-Member -NotePropertyName config -NotePropertyValue ([PSCustomObject]@{}) }
if (-not $cfg.plugins.entries.'mcp-adapter'.config.servers){ $cfg.plugins.entries.'mcp-adapter'.config | Add-Member -NotePropertyName servers -NotePropertyValue @() }

$newEntry = [PSCustomObject]@{
    name      = 'digitraver-bridge'
    transport = 'stdio'
    command   = $binaryPath
    args      = @()
}

$servers  = [System.Collections.ArrayList]@($cfg.plugins.entries.'mcp-adapter'.config.servers)
$replaced = $false
for ($i = 0; $i -lt $servers.Count; $i++) {
    if ($servers[$i].name -eq 'digitraver-bridge') {
        $servers[$i] = $newEntry
        $replaced = $true
        break
    }
}
if (-not $replaced) { [void]$servers.Add($newEntry) }
$cfg.plugins.entries.'mcp-adapter'.config.servers = @($servers)

$cfg | ConvertTo-Json -Depth 20 | Set-Content $OpenClawConfig -Encoding UTF8
Write-Info "OpenClaw config updated: $OpenClawConfig"

# 5. Success
Write-Host ''
Write-Host '=================================================='
Write-Host '  DigitRaver Bridge MCP — Installed!'
Write-Host '=================================================='
Write-Host ''
Write-Host "  Binary:  $(Join-Path $binDir $localExe)"
Write-Host "  Skill:   $(Join-Path $SkillDir 'SKILL.md')"
Write-Host "  Config:  $OpenClawConfig"
Write-Host ''
Write-Host '  Next steps:'
Write-Host '    1. Make sure Unity is running with Bridge active'
Write-Host '    2. Restart OpenClaw: openclaw gateway restart'
Write-Host '    3. Use the agent: /digitraver-agent'
Write-Host ''
Write-Host '  To uninstall:'
Write-Host '    .\install.ps1 -Uninstall'
Write-Host ''
