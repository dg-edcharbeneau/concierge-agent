#!/usr/bin/env bash
# Stores one AppHost user-secret from a hidden prompt, so the value never reaches
# shell history, an agent transcript, or terminal scrollback.
set -euo pipefail

if [ $# -ne 1 ] || [ -z "$1" ]; then
    echo "Usage: $0 <setting>   e.g. $0 Parameters:geoapify-api-key" >&2
    exit 2
fi
setting=$1

if [ ! -t 0 ]; then
    echo "No terminal attached. Run this yourself in an interactive terminal so the value is typed hidden." >&2
    exit 2
fi

root=$(git rev-parse --show-toplevel 2>/dev/null) || {
    echo "Run this from inside the concierge-agent repository." >&2
    exit 2
}
apphost_project=$(sed -n 's/.*"path": *"\([^"]*\)".*/\1/p' "$root/aspire.config.json")
if [ -z "$apphost_project" ]; then
    echo "Could not read appHost.path from $root/aspire.config.json." >&2
    exit 2
fi

read -r -s -p "Value for $setting (input hidden): " value
echo

# Copy-paste often carries a trailing newline or space, which providers reject with a 401.
trimmed="${value#"${value%%[![:space:]]*}"}"
trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
if [ -z "$trimmed" ]; then
    echo "Empty value; nothing stored." >&2
    exit 1
fi

dotnet user-secrets set "$setting" "$trimmed" --project "$root/$(dirname "$apphost_project")" >/dev/null
note=""
[ "$trimmed" != "$value" ] && note=", surrounding whitespace removed"
echo "Stored $setting (${#trimmed} characters$note)."
echo "Restart the AppHost for it to take effect."
unset value trimmed
