# Offline proof that the agent works, no Unity needed (about 2 minutes): unit tests, then trained agent vs the game's scripted guard.
Set-Location $PSScriptRoot
$env:OPENBLAS_NUM_THREADS = "1"
$py = "$PSScriptRoot\.venv\Scripts\python.exe"
Write-Host "`n=== 1. Unit tests (belief filter, roles, radio, stay-inside rule, reflexes) ===" -ForegroundColor Cyan
& $py -m pytest -q tests
Write-Host "`n=== 2. Sentinel: trained PPO vs the game's patrol guard, 100 games per row ===" -ForegroundColor Cyan
& $py evaluate.py --role sentinel --episodes 100 2>&1 | Select-Object -First 7
Write-Host "`n(full tables for all four roles are in logs\evaluate_*.txt, the role table is in ROLES.md)" -ForegroundColor DarkGray
