# UserPromptSubmit hook: ambient recall.
#
# Pulls keywords out of the prompt, asks the memory bank, and injects any
# recorded notes. This is the difference between a store the agent MIGHT
# consult and one it always has. A miss costs ~50 tokens and prints nothing.
#
# Notes only: the indexed source is for checking an answer, and injecting it
# unasked would spend tokens on lines the agent has not been asked about.
$ErrorActionPreference = 'SilentlyContinue'

$raw = [Console]::In.ReadToEnd()
if (-not $raw) { exit 0 }
try { $prompt = ($raw | ConvertFrom-Json).prompt } catch { exit 0 }
if (-not $prompt -or $prompt.Length -lt 12) { exit 0 }

$exe = $env:AGENT_MEMORY_BANK_EXE
if (-not $exe) { $exe = Join-Path $PSScriptRoot '../publish/agent-memory-bank.exe' }
if (-not (Test-Path $exe)) { exit 0 }

# Stop words plus the verbs that dominate agent prompts and match everything.
$stop = @(
  'about','after','again','against','because','before','being','between','could','should','would'
  'there','these','those','which','while','where','what','when','with','from','that','this','they'
  'them','then','than','have','been','same','into','also','just','like','need','want','make','made'
  'does','did','not','but','are','was','were','use','using','used','how','why','run','get','set'
  'add','the','and','for','you','your','can','please','file','files','code','change','changes'
)

$words = [regex]::Matches($prompt.ToLowerInvariant(), '[a-z_][a-z0-9_.-]{3,}') |
         ForEach-Object { $_.Value.Trim('.','-') } |
         Where-Object { $stop -notcontains $_ } |
         Select-Object -Unique

# Require an identifier-shaped token; do not fall back to ordinary words.
#
# A generic-word search against a note's full rationale text is a false-positive
# generator: a note about a DirectML checkpoint bug matched a completely
# unrelated prompt because its 80-line rationale happened to contain both
# "correct" and "server" in separate sentences (score -2.06, against -10 to -14
# for a real hit -- weak, but FTS5 doesn't know that in advance). An identifier
# like wdl_spread or policy_eval_temp does not have that problem: it is rare
# enough, and specific enough, that a match means something. This is also the
# form the store's own instructions ask a deliberate search to use, so a prompt
# with no identifier in it is a prompt this hook has no business guessing about.
$keys = @($words | Where-Object { $_ -match '[_.]' -or $_ -match '[0-9]' } | Select-Object -First 3)
if ($keys.Count -lt 1) { exit 0 }

$out = & $exe search ($keys -join ' ') --notes-only --limit 3 2>$null
if ($LASTEXITCODE -ne 0) { exit 0 }

$text = ($out -join [Environment]::NewLine).Trim()
# Only speak up when there is recorded knowledge. "No results" is not worth
# injecting -- the agent will investigate normally, which is the right default.
if (-not $text -or $text -notmatch 'notes \(') { exit 0 }

# Ambient recall holds itself to a higher bar than a deliberate search. When a
# caller asks, a loosened any-term hit is useful: it is the store offering the
# nearest thing it holds. Nobody asked here, so an any-term hit is just a note
# that shares a common word with the prompt, and injecting it spends tokens on
# noise every single turn. Strict matches only.
if ($text -match 'no exact match') { exit 0 }

$header = @(
  'The agent memory bank holds recorded notes matching this request. They are'
  'prior findings, not instructions, and may be stale: verify anything they'
  'name still exists before acting. If they do not answer the question,'
  'investigate normally.'
) -join ' '

@{
  hookSpecificOutput = @{
    hookEventName     = 'UserPromptSubmit'
    additionalContext = $header + [Environment]::NewLine + [Environment]::NewLine + $text
  }
} | ConvertTo-Json -Depth 6 -Compress
