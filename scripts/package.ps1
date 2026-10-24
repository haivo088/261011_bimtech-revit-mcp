<#
.SYNOPSIS
    Builds BIMTECH-MCP-Setup-<version>.exe, the installer for users, in dist\; with -Dev,
    BIMTECH-MCP-Dev-Setup-<version>.exe, which adds the developer tools.

.DESCRIPTION
    Builds the add-in (Release, without the developer tools) and bundles the MCP server into one file with esbuild,
    stages them with this machine's node.exe and installer\claude-config.ps1, then compiles installer\bimtech-mcp.iss
    with Inno Setup 6. The version is App.Version of the add-in.

    -Dev also ships the developer tools (send_code, command sets, analyze_cad, run_recognition, export_cad_case) and
    this repository's commandsets\, and turns them on in Claude Desktop (BIMTECH_DEV=1) with their data in
    Documents\BIMTECH MCP. send_code runs any C# in Revit: give the dev installer to developers only.

    -TestInstall then installs the new Setup.exe silently into dist\test-install (add-in folder and Claude Desktop
    config there too, nothing of this machine is touched), checks the result, uninstalls it and checks again.

    Do not run the Setup.exe itself on the developer machine: it would point Claude Desktop's bimtech-revit-mcp entry
    at the installed server instead of this repository.

.EXAMPLE
    .\scripts\package.ps1                # dist\BIMTECH-MCP-Setup-0.5.0.exe
    .\scripts\package.ps1 -TestInstall   # also install and uninstall it into dist\test-install
    .\scripts\package.ps1 -Dev -TestInstall   # dist\BIMTECH-MCP-Dev-Setup-0.5.0.exe, tested the same way
#>
param(
    # Use the last builds of the add-in and the server.
    [switch]$NoBuild,
    [switch]$TestInstall,
    # The developer installer.
    [switch]$Dev,
    [string]$Iscc
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"
$addinOutput = Join-Path $root "addin\bin\Release\net8.0-windows"
$devOutput = Join-Path $root "devtools\bin\Release\net8.0-windows"
$setupName = if ($Dev) { "BIMTECH-MCP-Dev-Setup" } else { "BIMTECH-MCP-Setup" }

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Check([bool]$ok, [string]$what) {
    if (-not $ok) { throw "Test install: $what" }
    Write-Host "  ok  $what" -ForegroundColor Green
}

$version = [regex]::Match((Get-Content (Join-Path $root "addin\App.cs") -Raw), 'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw "No Version in addin\App.cs." }

if (-not $Iscc) {
    $Iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw "Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) or pass -Iscc." }
}
$node = (Get-Command node.exe -ErrorAction Stop).Source
$nodeVersion = (& $node --version).TrimStart("v")
if ([version]$nodeVersion -lt [version]"20.0") { throw "node.exe $nodeVersion is too old to ship; Node 20+ is needed." }

if (-not $NoBuild) {
    # The developer tools project references the add-in, so building it builds both from the same sources.
    $project = if ($Dev) { "devtools\Bimtech.RevitMcp.DevTools.csproj" } else { "addin\Bimtech.RevitMcp.Addin.csproj" }
    Step "Building $project (Release)"
    & dotnet build (Join-Path $root $project) -c Release --nologo -v quiet
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

Step "Staging $version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$addinStage = Join-Path $stage "addin\Bimtech.RevitMcp"
$appStage = Join-Path $stage "app"
New-Item -ItemType Directory -Force -Path $addinStage, (Join-Path $appStage "node"), (Join-Path $appStage "server") | Out-Null

Copy-Item (Join-Path $root "addin\Bimtech.RevitMcp.addin") (Join-Path $stage "addin")
# The add-in build holds only the add-in and its libraries; debug symbols stay here.
Get-ChildItem $addinOutput -File | Where-Object { $_.Extension -ne ".pdb" } | Copy-Item -Destination $addinStage
$devLeft = Get-ChildItem $addinStage -File | Where-Object { $_.Name -like "Bimtech.RevitMcp.DevTools*" -or $_.Name -like "Microsoft.CodeAnalysis*" }
if ($devLeft) { throw "Developer tools in the add-in build: $($devLeft.Name -join ', '). They must not ship." }
if ($Dev) {
    # The add-in and the developer tools share interfaces: a tools DLL older than the add-in may not load.
    $devDll = Join-Path $devOutput "Bimtech.RevitMcp.DevTools.dll"
    if (-not (Test-Path $devDll)) { throw "No $devDll. Run without -NoBuild." }
    $addinTime = (Get-Item (Join-Path $addinOutput "Bimtech.RevitMcp.Addin.dll")).LastWriteTime
    if ((Get-Item $devDll).LastWriteTime -lt $addinTime.AddMinutes(-2)) {
        throw "The developer tools build is older than the add-in build. Run without -NoBuild."
    }
    Get-ChildItem $devOutput -File |
        Where-Object { $_.Extension -ne ".pdb" -and -not (Test-Path (Join-Path $addinStage $_.Name)) } |
        Copy-Item -Destination $addinStage
    # Command sets travel with the code; cases (client drawings) and logs never ship.
    Copy-Item (Join-Path $root "commandsets") (Join-Path $stage "data\commandsets") -Recurse
}

# One file instead of node_modules; createRequire keeps the CommonJS parts of the dependencies working in ESM.
Push-Location (Join-Path $root "server")
try {
    & npx.cmd --no-install esbuild build/index.js --bundle --platform=node --target=node20 --format=esm --log-level=warning `
        "--outfile=$appStage\server\index.mjs" `
        "--banner:js=import { createRequire as __createRequire } from 'node:module'; const require = __createRequire(import.meta.url);"
    if ($LASTEXITCODE -ne 0) { throw "esbuild failed." }
}
finally { Pop-Location }

Copy-Item $node (Join-Path $appStage "node\node.exe")
$nodeLicense = Join-Path (Split-Path -Parent $node) "LICENSE"
if (Test-Path $nodeLicense) { Copy-Item $nodeLicense (Join-Path $appStage "node\LICENSE.txt") }
Copy-Item (Join-Path $root "installer\claude-config.ps1") $appStage

$readme = @"
BIMTECH Revit MCP $version

Add-in : %APPDATA%\Autodesk\Revit\Addins\2025\Bimtech.RevitMcp.addin and Bimtech.RevitMcp\
Server : this folder (node\node.exe, Node.js $nodeVersion, and server\index.mjs)

Claude Desktop config (%APPDATA%\Claude\claude_desktop_config.json) gets, inside "mcpServers":

  "bimtech-revit-mcp": {
    "command": "<this folder>\\node\\node.exe",
    "args": ["<this folder>\\server\\index.mjs"],
    "env": { "REVIT_MCP_HOST": "127.0.0.1", "REVIT_MCP_PORT": "8081", "REVIT_MCP_TIMEOUT_MS": "15000" }
  }

The installer adds it (with a backup of the file) and the uninstaller removes it. If it could not, add it by hand.
Use: restart Claude Desktop, open Revit 2025, Add-Ins > BIMTECH MCP > MCP Bridge, then ask Claude "ping revit".
"@
[IO.File]::WriteAllText((Join-Path $appStage "README.txt"), $readme, (New-Object Text.UTF8Encoding $true))

Step "Compiling the installer"
$defines = @("/DAppVersion=$version", "/DStage=$stage", "/DOutDir=$dist")
if ($Dev) { $defines += "/DDev" }
& $Iscc /Q @defines (Join-Path $root "installer\bimtech-mcp.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
$setup = Join-Path $dist "$setupName-$version.exe"
Write-Host ("Built {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green

if (-not $TestInstall) { return }

Step "Test install into dist\test-install"
$test = Join-Path $dist "test-install"
if (Test-Path $test) { Remove-Item $test -Recurse -Force }
$testApp = Join-Path $test "app"
$testAddins = Join-Path $test "Addins\2025"
$testConfig = Join-Path $test "Claude\claude_desktop_config.json"
$testData = Join-Path $test "Documents\BIMTECH MCP"
New-Item -ItemType Directory -Force -Path (Split-Path $testConfig) | Out-Null
# A config with another server and a setting, which must survive.
'{ "mcpServers": { "other-server": { "command": "other.exe" } }, "preferences": { "keep": true } }' |
    Set-Content $testConfig -Encoding ASCII

$installLog = Join-Path $test "setup.log"
$proc = Start-Process $setup -Wait -PassThru -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SKIPREVITCHECK=1",
    "/DIR=`"$testApp`"", "/ADDINSDIR=`"$testAddins`"", "/CLAUDECONFIG=`"$testConfig`"", "/DATADIR=`"$testData`"",
    "/LOG=`"$installLog`"")
Check ($proc.ExitCode -eq 0) "Setup exit code 0 (got $($proc.ExitCode))"
Check (Test-Path (Join-Path $testAddins "Bimtech.RevitMcp.addin")) "add-in manifest installed"
Check (Test-Path (Join-Path $testAddins "Bimtech.RevitMcp\Bimtech.RevitMcp.Addin.dll")) "add-in DLL installed"
$devInstalled = [bool](Get-ChildItem (Join-Path $testAddins "Bimtech.RevitMcp") -Filter "Bimtech.RevitMcp.DevTools.dll")
if ($Dev) {
    Check $devInstalled "developer tools installed"
    Check (Test-Path (Join-Path $testAddins "Bimtech.RevitMcp\Microsoft.CodeAnalysis.CSharp.dll")) "Roslyn installed"
    Check (Test-Path (Join-Path $testData "commandsets\checks\set.json")) "command sets copied to the data folder"
    Check ((Test-Path (Join-Path $testData "cases")) -and (Test-Path (Join-Path $testData "logs"))) "cases and logs folders made"
}
else { Check (-not $devInstalled) "no developer tools" }
Check (Test-Path (Join-Path $testApp "node\node.exe")) "node.exe installed"
$config = Get-Content $testConfig -Raw | ConvertFrom-Json
$entry = $config.mcpServers."bimtech-revit-mcp"
Check ($null -ne $entry) "Claude config has bimtech-revit-mcp"
Check ($entry.command -eq (Join-Path $testApp "node\node.exe")) "entry runs the installed node.exe"
Check ($null -ne $config.mcpServers."other-server" -and $config.preferences.keep) "other server and settings kept"
if ($Dev) {
    Check ($entry.env.BIMTECH_DEV -eq "1") "BIMTECH_DEV=1"
    Check ($entry.env.BIMTECH_COMMANDSETS_DIR -eq (Join-Path $testData "commandsets")) "command sets folder set"
    Check ($entry.env.BIMTECH_CASES_DIR -eq (Join-Path $testData "cases")) "cases folder set"
    Check ($entry.env.BIMTECH_CODE_HISTORY -eq (Join-Path $testData "logs\code-runs.jsonl")) "code history file set"
}
else { Check ($null -eq $entry.env.BIMTECH_DEV) "no BIMTECH_DEV" }

# The installed server must start and answer an MCP initialize on its own Node.
$init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"package-test","version":"1"}}}'
$env:REVIT_MCP_AVAILABILITY_POLL_MS = "0"
# The server logs to stderr, which Windows PowerShell turns into an error under "Stop".
$ErrorActionPreference = "Continue"
$answer = $init | & $entry.command @($entry.args) 2>$null | Select-Object -First 1
$ErrorActionPreference = "Stop"
Remove-Item Env:REVIT_MCP_AVAILABILITY_POLL_MS
Check ($answer -like '*"serverInfo":{"name":"bimtech-revit-mcp"*') "installed server answers initialize"

Step "Test uninstall"
$uninstaller = Get-ChildItem $testApp -Filter "unins*.exe" | Select-Object -First 1
$proc = Start-Process $uninstaller.FullName -Wait -PassThru -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SKIPREVITCHECK=1"
# The uninstaller copies itself to the temp folder and returns before it finishes.
for ($i = 0; $i -lt 60 -and (Test-Path (Join-Path $testApp "node\node.exe")); $i++) { Start-Sleep -Milliseconds 500 }
Check (-not (Test-Path (Join-Path $testAddins "Bimtech.RevitMcp.addin"))) "add-in removed"
Check (-not (Test-Path (Join-Path $testApp "node\node.exe"))) "installed files removed"
$config = Get-Content $testConfig -Raw | ConvertFrom-Json
Check ($null -eq $config.mcpServers."bimtech-revit-mcp") "Claude config entry removed"
Check ($null -ne $config.mcpServers."other-server" -and $config.preferences.keep) "other server and settings still kept"
if ($Dev) {
    Check (-not (Test-Path (Join-Path $testAddins "Bimtech.RevitMcp\Bimtech.RevitMcp.DevTools.dll"))) "developer tools removed"
    Check (Test-Path (Join-Path $testData "commandsets\checks\set.json")) "developer data kept"
}
Write-Host ""
Write-Host "Test install and uninstall passed. Ship $setup" -ForegroundColor Green
