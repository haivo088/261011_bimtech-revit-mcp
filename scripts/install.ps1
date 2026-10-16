<#
.SYNOPSIS
    Builds the BIMTECH Revit MCP add-in and server and installs the add-in into Revit's add-in folder.

.DESCRIPTION
    Close Revit first: Revit locks the add-in DLL while it runs.
    Without -Dev, the developer tools are removed from the add-in folder if an earlier -Dev install left them
    there, so the result is exactly what a user gets.

.EXAMPLE
    .\scripts\install.ps1                 # user install (Release)
    .\scripts\install.ps1 -Dev            # also the developer tools (docs/07-dev-tools.md)
    .\scripts\install.ps1 -Dev -NoBuild   # copy the last build only
#>
param(
    # Also install the developer tools (Bimtech.RevitMcp.DevTools.dll and Roslyn).
    [switch]$Dev,
    # Skip "dotnet build" and "npm run build".
    [switch]$NoBuild,
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$RevitVersion = "2025",
    # Where Revit looks for add-ins; change only for a test install.
    [string]$AddinsDir = (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$addinOutput = Join-Path $root "addin\bin\$Configuration\net8.0-windows"
$devOutput = Join-Path $root "devtools\bin\$Configuration\net8.0-windows"
$target = Join-Path $AddinsDir "Bimtech.RevitMcp"
# Files only the developer tools bring; removed again by a user install.
$devFiles = @("Bimtech.RevitMcp.DevTools.*", "Microsoft.CodeAnalysis*.dll")

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    throw "Revit is running. Close it, then run this script again (Revit locks the add-in DLL)."
}

if (-not $NoBuild) {
    Step "Building the add-in ($Configuration)"
    & dotnet build (Join-Path $root "Bimtech.RevitMcp.sln") -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

    Step "Building the MCP server"
    Push-Location (Join-Path $root "server")
    try {
        if (-not (Test-Path "node_modules")) {
            & npm.cmd ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
        }
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed." }
    }
    finally { Pop-Location }
}

if (-not (Test-Path (Join-Path $addinOutput "Bimtech.RevitMcp.Addin.dll"))) {
    throw "No add-in build in $addinOutput. Run without -NoBuild."
}

Step "Installing the add-in into $target"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $root "addin\Bimtech.RevitMcp.addin") $AddinsDir -Force
Copy-Item (Join-Path $addinOutput "*") $target -Recurse -Force

if ($Dev) {
    $devDll = Join-Path $devOutput "Bimtech.RevitMcp.DevTools.dll"
    foreach ($file in @("Bimtech.RevitMcp.DevTools.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll")) {
        if (-not (Test-Path (Join-Path $devOutput $file))) {
            throw "No $file in $devOutput. Run without -NoBuild."
        }
    }
    # The add-in and the developer tools share interfaces (IDevToolsModule, plan actions): a tools DLL built before
    # the add-in may not load ("failed to load" in ping_revit). Both come from one solution build.
    $addinTime = (Get-Item (Join-Path $addinOutput "Bimtech.RevitMcp.Addin.dll")).LastWriteTime
    if ((Get-Item $devDll).LastWriteTime -lt $addinTime.AddMinutes(-2)) {
        throw "The developer tools build is older than the add-in build. Run without -NoBuild so both are rebuilt together."
    }
    Step "Installing the developer tools"
    Get-ChildItem $devOutput -File |
        Where-Object { $_.Name -notlike "Bimtech.RevitMcp.Addin.*" } |
        Copy-Item -Destination $target -Force
}
else {
    $leftovers = foreach ($pattern in $devFiles) { Get-ChildItem (Join-Path $target $pattern) -ErrorAction SilentlyContinue }
    if ($leftovers) {
        Step "Removing developer tools left by an earlier -Dev install"
        $leftovers | Remove-Item -Force
    }
}

Write-Host ""
Write-Host "Installed:" -ForegroundColor Green
Get-ChildItem $target -File | Sort-Object Name | ForEach-Object { Write-Host ("  " + $_.Name) }

# Claude Desktop must start this repository's server, with BIMTECH_DEV=1 for the developer tools. Only checked and
# reported here; the file is the user's, so it is never changed.
$serverEntry = Join-Path $root "server\build\index.js"
$claudeConfig = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"
$problems = @()
if (-not (Test-Path $serverEntry)) { $problems += "The MCP server is not built ($serverEntry). Run without -NoBuild." }
if (-not (Test-Path $claudeConfig)) {
    $problems += "No Claude Desktop config at $claudeConfig. Add the entry from config\claude-desktop.example.json."
}
else {
    try {
        $server = (Get-Content $claudeConfig -Raw -Encoding UTF8 | ConvertFrom-Json).mcpServers."bimtech-revit-mcp"
        if ($null -eq $server) {
            $problems += "Claude Desktop has no 'bimtech-revit-mcp' server. Add the entry from config\claude-desktop.example.json."
        }
        else {
            $entry = @($server.args)[0]
            if (-not $entry -or [System.IO.Path]::GetFullPath($entry) -ne [System.IO.Path]::GetFullPath($serverEntry)) {
                $problems += "Claude Desktop starts the server from '$entry', not from this repository ($serverEntry)."
            }
            $devFlag = $server.env.BIMTECH_DEV
            if ($Dev -and $devFlag -ne "1") {
                $problems += "Developer tools: add `"BIMTECH_DEV`": `"1`" to `"env`" of bimtech-revit-mcp in $claudeConfig."
            }
            if (-not $Dev -and $devFlag -eq "1") {
                $problems += "BIMTECH_DEV=1 is set but the developer tools were removed: their tools will fail. Install with -Dev or remove the flag."
            }
        }
    }
    catch {
        $problems += "Could not read $claudeConfig ($($_.Exception.Message))."
    }
}

Write-Host ""
if ($problems.Count -gt 0) {
    Write-Host "Check before testing:" -ForegroundColor Yellow
    $problems | ForEach-Object { Write-Host ("  - " + $_) -ForegroundColor Yellow }
    Write-Host ""
}
Write-Host "Next: restart Claude Desktop, open Revit, Add-Ins > BIMTECH MCP > MCP Bridge, then ask Claude to run ping_revit."
if ($Dev) { Write-Host "ping_revit must report devTools: loaded." }
