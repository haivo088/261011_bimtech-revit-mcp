<#
.SYNOPSIS
    Adds or removes the bimtech-revit-mcp server in Claude Desktop's config. Run by the installer and uninstaller.

.DESCRIPTION
    The file is the user's: every other server and setting is kept, a timestamped backup is written next to it before
    any change, and a file that is not valid JSON is left alone (exit code 2). Without -Config it updates every Claude
    Desktop config found: the usual one in %APPDATA%\Claude and the Microsoft Store one; for "add" with none found it
    creates the usual one. Windows PowerShell 5.1 compatible.

.EXAMPLE
    .\claude-config.ps1 -Action add -Node "C:\...\node.exe" -Server "C:\...\index.mjs"
    .\claude-config.ps1 -Action remove
#>
param(
    [Parameter(Mandatory)][ValidateSet("add", "remove")][string]$Action,
    [string]$Node,
    [string]$Server,
    # One config file to change instead of the ones found (used by the test install).
    [string]$Config
)

$ErrorActionPreference = "Stop"
$name = "bimtech-revit-mcp"
# Windows PowerShell 5.1 may carry type data that turns arrays into {"value": [...], "Count": n} in ConvertTo-Json.
Remove-TypeData System.Array -ErrorAction SilentlyContinue

function Find-Configs {
    $usual = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"
    $found = @()
    if (Test-Path $usual) { $found += $usual }
    $store = Join-Path $env:LOCALAPPDATA "Packages"
    if (Test-Path $store) {
        $found += Get-ChildItem $store -Directory -Filter "Claude_*" -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName "LocalCache\Roaming\Claude\claude_desktop_config.json" } |
            Where-Object { Test-Path $_ }
    }
    if ($found.Count -eq 0 -and $Action -eq "add") { $found += $usual }
    return $found
}

function Read-Config([string]$path) {
    if (-not (Test-Path $path)) { return [pscustomobject]@{} }
    $text = [IO.File]::ReadAllText($path).TrimStart([char]0xFEFF)
    if ($text.Trim().Length -eq 0) { return [pscustomobject]@{} }
    try { return $text | ConvertFrom-Json }
    catch { throw "INVALID_JSON: $path is not valid JSON, so it was not changed. Fix it or add the server by hand." }
}

function Write-Config([string]$path, $data) {
    $folder = Split-Path -Parent $path
    if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
    if (Test-Path $path) { Copy-Item $path ("$path.bak-" + (Get-Date -Format "yyyyMMdd-HHmmss")) -Force }
    $json = $data | ConvertTo-Json -Depth 50
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding $false))
}

if ($Action -eq "add" -and (-not $Node -or -not $Server)) { throw "add needs -Node and -Server." }
$configs = if ($Config) { @($Config) } else { Find-Configs }

try {
    foreach ($path in $configs) {
        $data = Read-Config $path
        $servers = $data.PSObject.Properties["mcpServers"]
        if ($Action -eq "add") {
            if ($null -eq $servers) {
                $data | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
            }
            $entry = [pscustomobject]@{
                command = $Node
                args    = @($Server)
                env     = [pscustomobject]@{ REVIT_MCP_HOST = "127.0.0.1"; REVIT_MCP_PORT = "8081"; REVIT_MCP_TIMEOUT_MS = "15000" }
            }
            $data.mcpServers | Add-Member -NotePropertyName $name -NotePropertyValue $entry -Force
            Write-Config $path $data
            Write-Output "Added $name to $path"
        }
        elseif ($null -ne $servers -and $null -ne $data.mcpServers.PSObject.Properties[$name]) {
            $data.mcpServers.PSObject.Properties.Remove($name)
            Write-Config $path $data
            Write-Output "Removed $name from $path"
        }
    }
    exit 0
}
catch {
    Write-Output $_.Exception.Message
    if ($_.Exception.Message -like "INVALID_JSON:*") { exit 2 }
    exit 1
}
