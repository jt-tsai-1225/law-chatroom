#!/usr/bin/env pwsh
# TTFT Benchmark Script
# Compare TTFT differences across three configurations

param(
    [string]$ApiBaseUrl = "http://localhost:8000",
    [string]$OutputFile = "benchmark_results.json"
)

# Test questions
$Questions = @(
    "In a sales contract, what responsibilities does the seller have?",
    "Under what circumstances must one bear liability for damages?",
    "What does 'sale does not break lease' mean?",
    "What is the statute of limitations for claims?",
    "What are the capital requirements for a limited liability company?",
    "How is the capacity of minors defined?",
    "If I rent a house and the owner sells it, can I still live there?",
    "Please explain in detail the requirements for contract formation"
)

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  RAG Legal Consultation TTFT Benchmark" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "API Base URL: $ApiBaseUrl" -ForegroundColor Yellow
Write-Host "Number of Questions: $($Questions.Count)" -ForegroundColor Yellow
Write-Host ""

# Clear cache
Write-Host "[1/4] Clearing cache..." -ForegroundColor Green
try {
    $clearResponse = Invoke-RestMethod -Uri "$ApiBaseUrl/api/cache/lmcache/clear" -Method Post -ErrorAction Stop
    Write-Host "      Cache cleared" -ForegroundColor Gray
} catch {
    Write-Host "      Warning: Failed to clear cache: $($_.Exception.Message)" -ForegroundColor Yellow
}

# Run tests
Write-Host ""
Write-Host "[2/4] Running tests..." -ForegroundColor Green

$Results = @()

foreach ($question in $Questions) {
    $TotalTimeMs = 0
    $SuccessCount = 0
    
    # Run each question 3 times
    for ($i = 1; $i -le 3; $i++) {
        Write-Host "      Testing: [$question] $i/3..." -ForegroundColor Gray -NoNewline
        
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        
        try {
            $body = @{
                Message = $question
            } | ConvertTo-Json
            
            $response = Invoke-RestMethod -Uri "$ApiBaseUrl/api/chat" -Method Post -ContentType "application/json" -Body $body -ErrorAction Stop
            
            $sw.Stop()
            $TotalTimeMs += $sw.ElapsedMilliseconds
            $SuccessCount++
            
            Write-Host (" ({0}ms)" -f $sw.ElapsedMilliseconds) -ForegroundColor Green
        } catch {
            $sw.Stop()
            Write-Host (" FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
        }
    }
    
    if ($SuccessCount -gt 0) {
        $AvgTimeMs = $TotalTimeMs / $SuccessCount
        
        $Result = [PSCustomObject]@{
            Question = $question
            AverageTimeMs = [math]::Round($AvgTimeMs, 2)
            SuccessCount = $SuccessCount
        }
        
        $Results += $Result
    }
}

# Get cache statistics
Write-Host ""
Write-Host "[3/4] Getting cache statistics..." -ForegroundColor Green

try {
    $stats = Invoke-RestMethod -Uri "$ApiBaseUrl/api/cache/stats" -Method Get -ErrorAction Stop
    Write-Host ("      LMcache Hit Rate: {0}%" -f $stats.lmcache.hitRate) -ForegroundColor Gray
    Write-Host ("      Total Entries: {0}" -f $stats.lmcache.totalEntries) -ForegroundColor Gray
} catch {
    Write-Host "      Warning: Failed to get stats: $($_.Exception.Message)" -ForegroundColor Yellow
}

# Generate report
Write-Host ""
Write-Host "[4/4] Generating report..." -ForegroundColor Green

$Report = [PSCustomObject]@{
    TestDate = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    ApiBaseUrl = $ApiBaseUrl
    TotalQuestions = $Questions.Count
    IterationsPerQuestion = 3
    Results = $Results
}

$Report | ConvertTo-Json | Out-File -FilePath $OutputFile -Encoding utf8

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Test Complete!" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ("Report saved to: {0}" -f $OutputFile) -ForegroundColor Green
Write-Host ""

# Display summary
Write-Host "Test Summary:" -ForegroundColor Yellow
Write-Host "------------------------------------------------------" -ForegroundColor Gray
Write-Host ("{0,5} | {1,50} | {2,15}" -f "No.", "Question", "Avg Time(ms)") -ForegroundColor White

Write-Host "------------------------------------------------------" -ForegroundColor Gray

for ($i = 0; $i -lt $Results.Count; $i++) {
    $r = $Results[$i]
    $truncatedQuestion = if ($r.Question.Length -gt 50) { $r.Question.Substring(0, 47) + "..." } else { $r.Question }
    Write-Host ("{0,5} | {1,50} | {2,15}" -f ($i + 1), $truncatedQuestion, $r.AverageTimeMs)
}

Write-Host "------------------------------------------------------" -ForegroundColor Gray
