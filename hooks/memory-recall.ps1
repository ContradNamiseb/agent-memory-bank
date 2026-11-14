# UserPromptSubmit hook: thin wrapper around `agent-memory-bank hook recall`.
# Prefer configuring the native binary directly in ~/.claude/settings.json.
$ErrorActionPreference = 'SilentlyContinue'

$isWin = ($IsWindows -eq $true) -or ($env:OS -eq 'Windows_NT')
$binary = if ($isWin) { 'agent-memory-bank.exe' } else { 'agent-memory-bank' }

$exe = $env:AGENT_MEMORY_BANK_EXE
if (-not $exe) { $exe = Join-Path $PSScriptRoot "../publish/$binary" }
if (-not (Test-Path $exe)) { exit 0 }

[Console]::In.ReadToEnd() | & $exe hook recall 2>$null
exit 0
