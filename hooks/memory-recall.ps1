# UserPromptSubmit hook: thin wrapper around `agent-memory-bank hook recall`.
# Prefer configuring the native binary directly in ~/.claude/settings.json.
$ErrorActionPreference = 'SilentlyContinue'

$isWin = ($IsWindows -eq $true) -or ($env:OS -eq 'Windows_NT')
$binary = if ($isWin) { 'agent-memory-bank.exe' } else { 'agent-memory-bank' }

$exe = $env:AGENT_MEMORY_BANK_EXE
if (-not $exe) {
    $sibling = Join-Path $PSScriptRoot "../$binary"
    $exe = if (Test-Path $sibling) { $sibling } else { Join-Path $PSScriptRoot "../publish/$binary" }
}
if (-not (Test-Path $exe)) { exit 0 }

# Hook input is UTF-8 JSON: read it as bytes, not through the console code
# page, and pipe it on as UTF-8, which Windows PowerShell 5.1 would send as
# ASCII, turning every non-ASCII character of a prompt into '?'.
$utf8 = [Text.UTF8Encoding]::new($false)
$OutputEncoding = $utf8
$inputJson = [IO.StreamReader]::new([Console]::OpenStandardInput(), $utf8).ReadToEnd()
$out = $inputJson | & $exe hook recall 2>$null
if ($LASTEXITCODE -ne 0 -or -not $out) { exit 0 }

$text = ($out -join [Environment]::NewLine).Trim()
if ($text.StartsWith('{')) { $text }
exit 0
