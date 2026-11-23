param(
    [string]$UnicornOneRoot = 'C:\Users\Bizmuth\source\repos\UnicornOne',
    [string]$PluginDir = 'C:\UnicornOne\Plugins\Workbooks',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [switch]$CheckOnly,
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'GaNLiveExternal.csproj'
if (-not (Test-Path $project)) { throw "Missing project: $project" }
$assemblies = @(
    "UnicornOne.Abstractions\bin\$Configuration\UnicornOne.Abstractions.dll",
    "UnicornOne.Platform\bin\$Configuration\UnicornOne.Platform.dll",
    "UnicornOne.Core\bin\$Configuration\UnicornOne.Core.dll"
)
foreach ($relative in $assemblies) {
    $p = Join-Path $UnicornOneRoot $relative
    if (-not (Test-Path $p)) { throw "Build UnicornOne first; required assembly missing: $p" }
    Write-Host "OK: $p"
}
$candidates = @(
    (Join-Path $UnicornOneRoot "UnicornOne.Core\bin\$Configuration\Newtonsoft.Json.dll"),
    (Join-Path $UnicornOneRoot "UnicornOne.RunTime\bin\$Configuration\Newtonsoft.Json.dll"),
    (Join-Path $UnicornOneRoot "UnicornOne.Platform\bin\$Configuration\Newtonsoft.Json.dll")
)
$newtonsoft = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $newtonsoft) { throw "Newtonsoft.Json.dll not found in build outputs; build UnicornOne first." }
Write-Host "OK: $newtonsoft"
$command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
$msbuild = $null
if ($command) { $msbuild = $command.Source }
if (-not $msbuild) {
    $pf = ${env:ProgramFiles(x86)}
    if ($pf) {
        $vswhere = Join-Path $pf 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        }
    }
}
if (-not $msbuild) { throw "MSBuild.exe not found. Install VS2022 build tools or run from Developer PowerShell." }
Write-Host "MSBuild: $msbuild"
Write-Host "Plugin destination: $PluginDir"
if ($CheckOnly) { Write-Host 'Check-only succeeded; nothing modified.'; exit 0 }
Write-Host 'Building external GaNLive DLL...'
& $msbuild $project '/t:Rebuild' "/p:Configuration=$Configuration" "/p:UnicornOneRoot=$UnicornOneRoot" "/p:NewtonsoftJsonPath=$newtonsoft" '/verbosity:minimal' '/nologo'
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }
$dll = Join-Path $PSScriptRoot "bin\$Configuration\GaNLiveExternal.dll"
if (-not (Test-Path $dll)) { throw "Build returned without expected DLL: $dll" }
Write-Host "Built: $dll"
if ($Deploy) {
    if (-not (Test-Path $PluginDir)) { New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null }
    $dest = Join-Path $PluginDir 'GaNLiveExternal.dll'
    if (Test-Path $dest) {
        $backup = "$dest.bak.$(Get-Date -Format yyyyMMdd_HHmmss)"
        Copy-Item -LiteralPath $dest -Destination $backup
        Write-Host "Backup: $backup"
    }
    Copy-Item -LiteralPath $dll -Destination $dest -Force
    Write-Host "Deployed: $dest"
    Write-Warning 'Restart UnicornOne; .NET Framework cannot replace an already-loaded plugin assembly.'
}
