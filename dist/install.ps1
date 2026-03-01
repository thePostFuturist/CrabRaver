#Requires -Version 5.1
<#
.SYNOPSIS
    DigitRaver Bridge MCP — PowerShell installer (configures mcporter).

.DESCRIPTION
    Downloads the Bridge MCP server binary and agent skill, then configures
    mcporter to use them.

.PARAMETER Uninstall
    Remove the binary, skill, and mcporter.json server entry.

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
    throw "VERSION file not found and BRIDGE_VERSION not set. Set `$env:BRIDGE_VERSION='x.y.z' when piping from web."
}

$Repo        = if ($env:BRIDGE_REPO) { $env:BRIDGE_REPO } else { 'thePostFuturist/CrabRaver' }
$BinaryName  = 'DigitRaverHelperMCP'
$SkillName   = 'digitraver-agent'

$BridgeDir      = Join-Path $HOME '.digitraver' 'mcp' 'bridge'
$OpenClawDir    = Join-Path $HOME '.openclaw'
$SkillDir       = Join-Path $OpenClawDir 'skills' $SkillName
$McPorterDir    = Join-Path $HOME '.mcporter'
$McPorterConfig = Join-Path $McPorterDir 'mcporter.json'

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

Installs DigitRaver Bridge MCP server and configures mcporter.

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
    $binDir = Join-Path $BridgeDir $RID
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

    # Remove digitraver-bridge from mcporter.json
    if (Test-Path $McPorterConfig) {
        try {
            $cfg = Get-Content $McPorterConfig -Raw | ConvertFrom-Json
            if ($cfg.mcpServers -and $cfg.mcpServers.PSObject.Properties['digitraver-bridge']) {
                $cfg.mcpServers.PSObject.Properties.Remove('digitraver-bridge')
                $cfg | ConvertTo-Json -Depth 20 | Set-Content $McPorterConfig -Encoding UTF8
                Write-Info 'Removed digitraver-bridge from mcporter.json'
            }
            else {
                Write-Info 'digitraver-bridge not found in mcporter.json'
            }
        }
        catch {
            Write-Info "Could not patch mcporter.json: $_"
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
$binDir   = Join-Path $BridgeDir $RID
New-Item -ItemType Directory -Path $binDir -Force | Out-Null
$localExe = "$BinaryName.exe"

Download-Asset -AssetName $Asset -Dest (Join-Path $binDir $localExe)
Set-Content -Path (Join-Path $binDir '.version') -Value $Version -NoNewline
Write-Info "Binary installed: $(Join-Path $binDir $localExe)"

# 2. Download SKILL.md
New-Item -ItemType Directory -Path $SkillDir -Force | Out-Null
Download-Asset -AssetName 'SKILL.md' -Dest (Join-Path $SkillDir 'SKILL.md')
Write-Info "Skill installed: $(Join-Path $SkillDir 'SKILL.md')"

# 3. Configure mcporter
$binaryPath = (Join-Path $binDir $localExe).Replace('\', '/')

if (-not (Test-Path $McPorterDir)) { New-Item -ItemType Directory -Path $McPorterDir -Force | Out-Null }

if (Test-Path $McPorterConfig) {
    $cfg = Get-Content $McPorterConfig -Raw | ConvertFrom-Json
}
else {
    $cfg = [PSCustomObject]@{}
}

# Ensure mcpServers key
if (-not $cfg.mcpServers) {
    $cfg | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([PSCustomObject]@{})
}

# Upsert digitraver-bridge
$bridgeEntry = [PSCustomObject]@{
    command = $binaryPath
    args    = @()
}

if ($cfg.mcpServers.PSObject.Properties['digitraver-bridge']) {
    $cfg.mcpServers.'digitraver-bridge' = $bridgeEntry
}
else {
    $cfg.mcpServers | Add-Member -NotePropertyName 'digitraver-bridge' -NotePropertyValue $bridgeEntry
}

$cfg | ConvertTo-Json -Depth 20 | Set-Content $McPorterConfig -Encoding UTF8
Write-Info "mcporter config updated: $McPorterConfig"

# 4. Success
Write-Host ''
Write-Host '=================================================='
Write-Host '  DigitRaver Bridge MCP — Installed!'
Write-Host '=================================================='
Write-Host ''
Write-Host "  Binary:  $(Join-Path $binDir $localExe)"
Write-Host "  Skill:   $(Join-Path $SkillDir 'SKILL.md')"
Write-Host "  Config:  $McPorterConfig"
Write-Host ''
Write-Host '  Verify:  mcporter config list'
Write-Host ''
Write-Host '  Next steps:'
Write-Host '    1. Make sure the DigitRaver binary is running with Bridge active'
Write-Host '    2. Use the agent: /digitraver-agent'
Write-Host ''
Write-Host '  To uninstall:'
Write-Host '    .\install.ps1 -Uninstall'
Write-Host ''
