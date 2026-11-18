param([switch]$Build)
$ErrorActionPreference = 'Stop'
$apartmentPreparation = Join-Path $PSScriptRoot 'prepare-development.ps1'
if (!$Build) {
    & $apartmentPreparation
    if ($LASTEXITCODE -ne 0) { throw 'Development environment preparation failed.' }
}
$apartmentRepo = Split-Path -Parent $PSScriptRoot
$apartmentData = Join-Path $apartmentRepo 'bin\ApartmentPrototypeData'
$apartmentExe = Join-Path $apartmentRepo 'bin\Content.Server\Content.Server.exe'
$apartmentConfig = Join-Path $PSScriptRoot 'apartment-prototype.toml'
if ($Build) {
    Push-Location $apartmentRepo
    try {
        & $apartmentPreparation dotnet build Content.Server/Content.Server.csproj
        if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
        & $apartmentPreparation dotnet build Content.Client/Content.Client.csproj
        if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }
    } finally { Pop-Location }
}
if (!(Test-Path -LiteralPath $apartmentExe)) { throw 'Build Content.Server first, or run with -Build.' }
New-Item -ItemType Directory -Path $apartmentData -Force | Out-Null
$apartmentPidPath = Join-Path $apartmentData 'server.pid'
if (Test-Path -LiteralPath $apartmentPidPath) {
    $apartmentPreviousId = [int](Get-Content -LiteralPath $apartmentPidPath)
    $apartmentPrevious = Get-Process -Id $apartmentPreviousId -ErrorAction SilentlyContinue
    if ($apartmentPrevious -and $apartmentPrevious.Path -eq $apartmentExe) { throw "Prototype already running (PID $apartmentPreviousId)." }
}
$apartmentProcess = Start-Process -FilePath $apartmentExe -WorkingDirectory $apartmentRepo -WindowStyle Hidden -PassThru `
    -ArgumentList @('--config-file', ('"' + $apartmentConfig + '"'), '--data-dir', ('"' + $apartmentData + '"')) `
    -RedirectStandardOutput (Join-Path $apartmentData 'server.log') -RedirectStandardError (Join-Path $apartmentData 'server.stderr.log')
Set-Content -LiteralPath $apartmentPidPath -Value $apartmentProcess.Id
Write-Output "Apartment prototype: ss14://127.0.0.1:1214 (PID $($apartmentProcess.Id))"
Write-Output "Local data and logs: $apartmentData"
