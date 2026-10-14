#!/usr/bin/env bash
# 跑單元測試並產生覆蓋率 HTML 報表。
#   用法：在 backend/ 底下執行  ./run-tests.sh
#   報表：backend/TestResults/CoverageReport/index.html
set -euo pipefail
cd "$(dirname "$0")"

rm -rf TestResults
dotnet tool restore
dotnet test ChatBot.Api.Tests/ChatBot.Api.Tests.csproj \
  --settings ChatBot.Api.Tests/coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory TestResults \
  --logger "trx;LogFileName=test-results.trx"

dotnet tool run reportgenerator \
  -reports:"TestResults/**/coverage.cobertura.xml" \
  -targetdir:TestResults/CoverageReport \
  -reporttypes:"Html;TextSummary" \
  -title:"法律 RAG 聊天室後端 單元測試覆蓋率"

cat TestResults/CoverageReport/Summary.txt
echo
echo "HTML 報表：$(pwd)/TestResults/CoverageReport/index.html"
