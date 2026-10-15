<#
.SYNOPSIS
  Publish agent-memory-bank without stopping the servers that are running it.

.DESCRIPTION
  Every agent session runs its own publish\agent-memory-bank.exe (the hooks
  launch it too), and Windows will not
  overwrite a loaded image, so publishing in place meant killing every server
  by image name first. Some clients (OpenCode) never respawn a dead MCP server,
  so that left those agents without the tools until they were restarted.

  Instead each publish goes to its own folder under releases\, and publish\ is
  a junction to the newest one. A running server keeps the release it started
  from; every new launch gets the new one. Client configs keep pointing at
  publish\agent-memory-bank.exe and never change.

  The first run converts an ordinary publish\ folder by moving it under
  releases\. Windows allows renaming a folder whose exe is running, so that
  needs no kill either.

  Caveat, measured: a running server that loads a native library for the first
  time after the switch (SQLite, on its first database call) finds it through
  publish\, so it gets the new release's copy. Releases are complete, so this
  only matters if a publish changes the SQLite package -- but it means an old
  server can hold files in a NEWER release, so pruning checks every image in a
  release, not just its exe.

.EXAMPLE
  pwsh -File publish.ps1
#>
param(
    [string]$Configuration = 'Release',
    # Releases kept besides the new one; older ones are removed once nothing runs them.
    [int]$Keep = 3,
    [string]$Root = $PSScriptRoot,
    [string]$Project = (Join-Path $PSScriptRoot 'src\AgentMemoryBank.McpServer'),
    [string]$Exe = 'agent-memory-bank.exe'
)
$ErrorActionPreference = 'Stop'

$releases = Join-Path $Root 'releases'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$target = Join-Path $releases $stamp

dotnet publish $Project -c $Configuration -o $target --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE; publish\ was not changed" }

# Smoke-test the release before anything points at it.
& (Join-Path $target $Exe) help | Out-Null
if ($LASTEXITCODE -ne 0) { throw "$Exe help failed in $target; publish\ was not changed" }

$link = Join-Path $Root 'publish'
$current = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
if ($current -and $current.LinkType -eq 'Junction') {
    $current.Delete()  # removes the link only, never the release it points at
} elseif ($current) {
    Move-Item -LiteralPath $link -Destination (Join-Path $releases "legacy-$stamp")
}
New-Item -ItemType Junction -Path $link -Target $target | Out-Null
"publish -> releases\$stamp"

# A release is removed only when no process holds any image in it: Windows
# refuses an exclusive open of a loaded exe or dll. Checking only the exe is not
# enough (see the caveat above).
function Test-InUse([string]$dir) {
    foreach ($file in Get-ChildItem -LiteralPath $dir -Recurse -File -Include *.exe, *.dll) {
        try { [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None').Dispose() } catch { return $true }
    }
    return $false
}

Get-ChildItem -LiteralPath $releases -Directory |
    Where-Object FullName -ne $target |
    Sort-Object CreationTime -Descending |
    Select-Object -Skip $Keep |
    ForEach-Object {
        if (Test-InUse $_.FullName) { "kept releases\$($_.Name): a running server still uses it"; return }
        try {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
            "removed releases\$($_.Name)"
        } catch {
            "could not fully remove releases\$($_.Name) (a server started using it mid-check?): $($_.Exception.Message)"
        }
    }
