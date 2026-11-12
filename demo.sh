#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

if [[ ! -d samples/ticks ]]; then
  dotnet run --project tools/MuNTrade.SampleData -c Release
fi

dotnet run --project src/MuNTrade.Demo.Cli -c Release -- backtest
dotnet run --project src/MuNTrade.Demo.Cli -c Release -- report
printf 'Report: %s\n' "$repo_root/site/index.html"
