# Starts the agent brain (Python server) in this window so the lecturer can SEE it think. Leave it open, then press Play in Unity.
# Optional: trace every decision of one person, e.g.  .\start_brain.ps1 -Trace Worker_17
param([string]$Trace = "")
Set-Location $PSScriptRoot
$env:OPENBLAS_NUM_THREADS = "1"; $env:OMP_NUM_THREADS = "1"
if ($Trace) { $env:SENTINEL_TRACE = $Trace }
Write-Host "Starting the brain on 127.0.0.1:5555 ... (Ctrl+C to stop)" -ForegroundColor Cyan
& "$PSScriptRoot\.venv\Scripts\python.exe" -u -m guard_agent.server --port 5555
