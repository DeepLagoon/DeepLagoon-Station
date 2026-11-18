# Also works when VS Code inherited PATH before Python was installed.
$ErrorActionPreference = 'Stop'
$developmentRoot = Split-Path -Parent $PSScriptRoot
$developmentCandidates = @(
    "$env:LOCALAPPDATA\Python\bin\python.exe"
    Get-ChildItem -Path "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe", `
        "$env:LOCALAPPDATA\Python\pythoncore-3.*\python.exe", `
        "$env:ProgramFiles\Python3*\python.exe" -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
    Get-Command python.exe, python3.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Source
)
foreach ($developmentPython in $developmentCandidates) {
    if (!(Test-Path -LiteralPath $developmentPython)) { continue }
    try {
        $developmentVersion = & $developmentPython -c 'import sys; print(sys.version_info >= (3, 10))' 2>$null
    } catch { continue }
    if ($LASTEXITCODE -eq 0 -and $developmentVersion -eq 'True') {
        & $developmentPython (Join-Path $developmentRoot 'Tools\prepare_development.py') @args
        exit $LASTEXITCODE
    }
}
foreach ($developmentLauncher in (Get-Command py.exe -CommandType Application -ErrorAction SilentlyContinue)) {
    try {
        $developmentVersion = & $developmentLauncher.Source -3 -c 'import sys; print(sys.version_info >= (3, 10))' 2>$null
    } catch { continue }
    if ($LASTEXITCODE -eq 0 -and $developmentVersion -eq 'True') {
        & $developmentLauncher.Source -3 (Join-Path $developmentRoot 'Tools\prepare_development.py') @args
        exit $LASTEXITCODE
    }
}
[Console]::Error.WriteLine('DeepLagoon: Python 3.10+ was not found. Install Python from https://www.python.org/downloads/ and restart VS Code.')
exit 1
