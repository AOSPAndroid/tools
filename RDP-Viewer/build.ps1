#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$RunTests,
    [switch]$SmokeTest
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) {
    throw 'Build in 64-bit Windows PowerShell on Windows with Visual Studio / Build Tools and the .NET 4.8 SDK.'
}
$project = $PSScriptRoot
$interop = Join-Path $project 'interop'
$artifacts = Join-Path $project 'artifacts'
$package = Join-Path $artifacts 'RDP-Viewer-win-x64'
$reports = Join-Path $artifacts 'reports'
New-Item -ItemType Directory -Force -Path $interop, $package, $reports | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio / Build Tools was not found. Install the .NET desktop build workload and .NET Framework 4.8 SDK/targeting pack.' }
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild.exe was not found by vswhere.' }
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows'
$aximp = Get-ChildItem -LiteralPath $sdkRoot -Filter 'AxImp.exe' -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'NETFX 4\.[78].*Tools' } |
    Sort-Object @{Expression = { $_.FullName -match '\\x64\\' }; Descending = $true}, FullName -Descending |
    Select-Object -First 1
if (-not $aximp) { throw 'AxImp.exe was not found. Install the .NET Framework SDK tools through Visual Studio Installer.' }
$systemControl = Join-Path $env:SystemRoot 'System32\mstscax.dll'
if (-not (Test-Path -LiteralPath $systemControl)) { throw 'The installed Microsoft RDP ActiveX control was not found.' }
Push-Location $interop
try {
    & $aximp.FullName $systemControl '/out:AxMSTSCLib.dll' '/nologo'
    if ($LASTEXITCODE -ne 0) { throw "AxImp failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }
foreach ($name in 'MSTSCLib.dll', 'AxMSTSCLib.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $interop $name))) { throw "Expected interop output missing: $name" }
}
& $msbuild (Join-Path $project 'RdpViewer.csproj') '/nologo' '/m' '/v:minimal' "/p:Configuration=$Configuration" '/p:Platform=x64'
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE." }
$output = Join-Path $project "bin\$Configuration"
$exe = Join-Path $output 'RDP-Viewer.exe'
function Invoke-ViewerTest([string]$Mode, [string]$ReportName) {
    $report = Join-Path $reports $ReportName
    $process = Start-Process -FilePath $exe -ArgumentList @($Mode, ('"{0}"' -f $report)) -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw "$Mode timed out; no network connection is part of this test."
    }
    $process.Refresh()
    if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
    if ($process.ExitCode -ne 0) { throw "$Mode failed with exit code $($process.ExitCode)." }
}
if ($RunTests) { Invoke-ViewerTest '--self-test' 'self-test.txt' }
if ($SmokeTest) { Invoke-ViewerTest '--smoke-test' 'activex-smoke-test.txt' }
foreach ($name in 'RDP-Viewer.exe', 'RDP-Viewer.exe.config', 'MSTSCLib.dll', 'AxMSTSCLib.dll') {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination $package -Force
}
Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination (Join-Path $package 'README.md') -Force
@(
    "Built UTC: $([DateTime]::UtcNow.ToString('o'))"
    "Source commit (CI): $env:GITHUB_SHA"
    "MSBuild: $msbuild"
    "System RDP control version: $((Get-Item -LiteralPath $systemControl).VersionInfo.FileVersion)"
    "Self-tests requested: $RunTests"
    "Offline ActiveX smoke test requested: $SmokeTest"
    'Live corporate authentication and remote dynamic-resolution tests: NOT PERFORMED'
    'Executable is unsigned; use your approved internal signing/deployment process.'
) | Set-Content -LiteralPath (Join-Path $package 'BUILD-INFO.txt') -Encoding UTF8
Get-ChildItem -LiteralPath $package -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name |
    ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name } |
    Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "Package: $package"
