# 跑單元測試並產生覆蓋率 HTML 報表。
#   用法：在 backend\ 底下執行  .\run-tests.ps1
#   報表：backend\TestResults\CoverageReport\index.html
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (Test-Path TestResults) { Remove-Item -Recurse -Force TestResults }
dotnet tool restore

dotnet test ChatBot.Api.Tests/ChatBot.Api.Tests.csproj `
  --settings ChatBot.Api.Tests/coverlet.runsettings `
  --collect:"XPlat Code Coverage" `
  --results-directory TestResults `
  --logger "trx;LogFileName=test-results.trx"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet tool run reportgenerator `
  "-reports:TestResults/**/coverage.cobertura.xml" `
  "-targetdir:TestResults/CoverageReport" `
  "-reporttypes:Html;TextSummary" `
  "-title:法律 RAG 聊天室後端 單元測試覆蓋率"

Get-Content TestResults/CoverageReport/Summary.txt
Write-Host ""
Write-Host "HTML 報表：$(Join-Path (Get-Location) 'TestResults/CoverageReport/index.html')"
