param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $baseline = Join-Path $root "artifacts/release/$Version/MusicPlayer"
    if (!(Test-Path -LiteralPath (Join-Path $baseline 'MusicPlayer.exe'))) { throw 'Package the release first.' }
    $repeat = Join-Path $root "artifacts/reproducibility/$Version-$([guid]::NewGuid().ToString('N'))"
    dotnet clean MusicPlayer.csproj -c Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Clean failed.' }
    dotnet restore MusicPlayer.csproj --locked-mode -r win-x64 -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet publish MusicPlayer.csproj --no-restore -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -o $repeat
    if ($LASTEXITCODE -ne 0) { throw 'Repeat publish failed.' }
    $files = @(Get-ChildItem -LiteralPath $repeat -File -Recurse)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($repeat.Length + 1)
        $original = Join-Path $baseline $relative
        if (!(Test-Path -LiteralPath $original) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $original).Hash) {
            throw "Clean publish differs: $relative"
        }
    }
    Write-Host "Clean release publish matches all $($files.Count) payload files."
} finally { Pop-Location }
