$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $repoRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'samples/ticks') -PathType Container)) {
        & dotnet run --project tools/MuNTrade.SampleData -c Release
        if ($LASTEXITCODE -ne 0) { throw "Sample data generator failed with exit code $LASTEXITCODE." }
    }

    & dotnet run --project src/MuNTrade.Demo.Cli -c Release -- backtest
    if ($LASTEXITCODE -ne 0) { throw "Backtest failed with exit code $LASTEXITCODE." }

    & dotnet run --project src/MuNTrade.Demo.Cli -c Release -- report
    if ($LASTEXITCODE -ne 0) { throw "Report generation failed with exit code $LASTEXITCODE." }
    Write-Output "Report: $(Join-Path $repoRoot 'site/index.html')"
}
finally {
    Pop-Location
}
