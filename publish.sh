#!/bin/sh
# Publish agent-memory-bank without stopping the servers that are running it.
#
# The Linux and macOS counterpart of publish.ps1, with the same layout: each
# publish goes to its own folder under releases/, and publish is a symlink to
# the newest one. A running server keeps the release it started from; every
# new launch gets the new one. Client configs keep pointing at
# publish/agent-memory-bank and never change.
#
# A release is pruned only when lsof shows nothing using it. Without lsof it
# cannot tell, so it keeps every release and says so: a server that has not
# yet loaded SQLite would fail to find it in a deleted release.
#
#   sh publish.sh
#   CONFIGURATION=Debug KEEP=5 sh publish.sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
configuration=${CONFIGURATION:-Release}
keep=${KEEP:-3}            # releases kept besides the new one
exe=agent-memory-bank
releases="$root/releases"
stamp=$(date +%Y%m%d-%H%M%S)
target="$releases/$stamp"

if ! dotnet publish "$root/src/AgentMemoryBank.McpServer" -c "$configuration" -o "$target" --nologo; then
    echo "dotnet publish failed; publish/ was not changed" >&2
    exit 1
fi

# Smoke-test the release before anything points at it.
if ! "$target/$exe" help >/dev/null; then
    echo "$exe help failed in $target; publish/ was not changed" >&2
    exit 1
fi

if [ -d "$root/hooks" ]; then
    cp -R "$root/hooks" "$target/hooks"
fi

link="$root/publish"
if [ -L "$link" ]; then
    rm "$link"              # removes the link only, never the release it points at
elif [ -e "$link" ]; then
    mv "$link" "$releases/legacy-$stamp"
fi
ln -s "$target" "$link"
echo "publish -> releases/$stamp"

in_use() {
    # lsof exits 0 when it finds an open file under the directory.
    lsof +D "$1" >/dev/null 2>&1
}

if ! command -v lsof >/dev/null 2>&1; then
    echo "lsof not found: kept every release (cannot tell which ones running servers use)"
    exit 0
fi

ls -1 "$releases" | grep -E '^[0-9]{8}-[0-9]{6}$' | grep -vx "$stamp" | sort -r | tail -n +"$((keep + 1))" |
while IFS= read -r old; do
    if in_use "$releases/$old"; then
        echo "kept releases/$old: a running server still uses it"
    elif rm -rf "${releases:?}/$old"; then
        echo "removed releases/$old"
    else
        echo "could not fully remove releases/$old"
    fi
done
