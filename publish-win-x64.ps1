$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the Microsoft .NET 8 SDK before publishing.'
}

$root = $PSScriptRoot
$checks = Join-Path $root 'src\SurfaceFrame.Checks\SurfaceFrame.Checks.csproj'
$app = Join-Path $root 'src\SurfaceFrame\SurfaceFrame.csproj'
$dist = Join-Path $root 'dist'
$version = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([Guid]::NewGuid().ToString('N').Substring(0, 6))
$release = Join-Path $dist "SurfaceFrame-win-x64-$version"
$work = Join-Path $dist ".build-$version"

Push-Location -LiteralPath $root
try {
    dotnet run --project $checks -c Release
    if ($LASTEXITCODE -ne 0) { throw "Self-checks failed ($LASTEXITCODE)." }

    dotnet publish $app -c Release -r win-x64 --self-contained true -o $work
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
    if (-not (Test-Path -LiteralPath (Join-Path $work 'SurfaceFrame.exe'))) {
        throw 'SurfaceFrame.exe is missing.'
    }

    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $work
    Copy-Item -LiteralPath (Join-Path $root 'Photos') -Destination $work -Recurse
    Get-ChildItem -LiteralPath $work -Filter '*.pdb' -File -Recurse | Remove-Item
    $zip = "$release.zip"
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -CompressionLevel Optimal
    Move-Item -LiteralPath $work -Destination $release
    Get-FileHash -LiteralPath $zip -Algorithm SHA256
}
finally {
    Pop-Location
}
