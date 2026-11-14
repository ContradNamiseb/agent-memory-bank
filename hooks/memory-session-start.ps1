# SessionStart hook: thin wrapper around `agent-memory-bank hook session-start`.
# Prefer configuring the native binary directly in ~/.claude/settings.json.
$ErrorActionPreference = 'SilentlyContinue'

$isWin = ($IsWindows -eq $true) -or ($env:OS -eq 'Windows_NT')
$binary = if ($isWin) { 'agent-memory-bank.exe' } else { 'agent-memory-bank' }

$exe = $env:AGENT_MEMORY_BANK_EXE
if (-not $exe) { $exe = Join-Path $PSScriptRoot "../publish/$binary" }
if (-not (Test-Path $exe)) { exit 0 }

$inputJson = if ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else { '' }
$inputJson | & $exe hook session-start 2>$null
exit 0
