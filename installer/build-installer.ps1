$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$wrapperRoot = Split-Path -Parent $scriptDir
$propsPath = Join-Path $wrapperRoot 'Directory.Build.props'
$desktopProj = Join-Path $wrapperRoot 'src\Meloberry.Wrapper.Desktop\Meloberry.Wrapper.Desktop.csproj'
$issPath = Join-Path $scriptDir 'mby-pc.iss'

[xml]$propsXml = Get-Content -LiteralPath $propsPath
$version = $propsXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    throw "Could not read <Version> from $propsPath"
}

Write-Output "Publishing Meloberry PC v$version..."
& dotnet publish $desktopProj -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishedExe = Join-Path $wrapperRoot "app\mby-pc-$version.exe"
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Expected published binary not found at $publishedExe"
}

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) {
        $iscc = $cmd.Source
    }
}

if (-not $iscc) {
    throw "Inno Setup 6 compiler (ISCC.exe) was not found."
}

Write-Output "Compiling Inno Setup installer with $iscc..."
& $iscc "/DMyAppVersion=$version" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE"
}

$setupExe = Join-Path $wrapperRoot "app\mby-pc-setup-$version.exe"
if (-not (Test-Path -LiteralPath $setupExe)) {
    throw "Expected installer not found at $setupExe"
}

Write-Output "Successfully built $publishedExe and $setupExe"
