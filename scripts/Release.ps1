param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $output = Join-Path $root "artifacts/release/$Version"
    if (Test-Path -LiteralPath $output) { throw "Release directory already exists: $output. Use a new version or archive it first." }
    $publish = Join-Path $output 'MusicPlayer'
    # WPF's generated markup can differ between incremental and clean compilation.
    dotnet clean MusicPlayer.csproj -c Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Release clean failed.' }
    dotnet restore MusicPlayer.csproj --locked-mode -r win-x64 -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw 'Release restore failed.' }
    dotnet publish MusicPlayer.csproj --no-restore -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
    foreach ($required in @('MusicPlayer.exe', 'sndfile.dll', 'MusicPlayer.runtimeconfig.json', 'Assets/Lucide/LICENSE')) {
        if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Missing release file: $required" }
    }
    Copy-Item -LiteralPath README.md, LYRICS.md, ARTIST_PHOTOS.md, DISCORD_PRESENCE.md -Destination $publish
    Copy-Item -LiteralPath docs -Destination $publish -Recurse
    # Manifest identifies reproducible publish outputs; ZIP metadata itself is not deterministic.
    $manifest = Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($publish.Length + 1).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    }
    $manifest | Set-Content -LiteralPath (Join-Path $output 'files.sha256') -Encoding ascii
    $archive = Join-Path $output "MusicPlayer-$Version-win-x64.zip"
    Compress-Archive -LiteralPath $publish -DestinationPath $archive
    '{0}  {1}' -f (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $archive -Leaf) |
        Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host "Release ready: $archive"
} finally { Pop-Location }
