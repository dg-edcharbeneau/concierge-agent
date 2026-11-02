#!/usr/bin/env bash
# Reports Concierge setup state. Required settings are read from the repo's own
# sources (aspire.config.json, AppHost.cs, AppHost appsettings) so the checks follow
# the code instead of a copied list. User-secrets are reported by name only; only
# non-secret AI settings (provider, endpoint, model) are printed with values.
set -uo pipefail

root=$(git rev-parse --show-toplevel 2>/dev/null) || {
    echo "Run this from inside the concierge-agent repository." >&2
    exit 2
}
cd "$root" || exit 2

problems=0
ok()      { printf '  [ok]      %s\n' "$*"; }
missing() { printf '  [missing] %s\n' "$*"; problems=$((problems + 1)); }
info()    { printf '  [info]    %s\n' "$*"; }
have()    { command -v "$1" >/dev/null 2>&1; }

apphost_project=$(sed -n 's/.*"path": *"\([^"]*\)".*/\1/p' aspire.config.json 2>/dev/null)
if [ -z "$apphost_project" ] || [ ! -f "$apphost_project" ]; then
    echo "Could not resolve appHost.path from aspire.config.json." >&2
    exit 2
fi
apphost_dir=$(dirname "$apphost_project")
apphost_cs="$apphost_dir/AppHost.cs"

echo "Tools"
tfm=$(sed -n 's:.*<TargetFramework>net\([0-9][0-9]*\).*:\1:p' "$apphost_project" | head -1)
if have dotnet; then
    if dotnet --list-sdks 2>/dev/null | grep -q "^$tfm\."; then
        ok ".NET $tfm SDK"
    else
        missing ".NET $tfm SDK (AppHost targets net$tfm.0)"
    fi
else
    missing "dotnet (.NET $tfm SDK)"
fi
for tool in aspire just devtunnel docker; do
    if have "$tool"; then ok "$tool"; else missing "$tool CLI (see README prerequisites)"; fi
done

echo "Machine"
if have docker && docker info >/dev/null 2>&1; then
    ok "Docker daemon running"
else
    missing "Docker daemon not reachable; start Docker"
fi
if have dotnet && dotnet dev-certs https --check --trust >/dev/null 2>&1; then
    ok "HTTPS dev certificate trusted"
else
    missing "HTTPS dev certificate not trusted; run: dotnet dev-certs https --trust"
fi
if have devtunnel && devtunnel user show 2>/dev/null | grep -q '^Logged in'; then
    ok "devtunnel logged in"
else
    missing "devtunnel not logged in; run: devtunnel user login"
fi

# Values stay in this variable; only names and allow-listed non-secret values are printed.
secrets=$(dotnet user-secrets list --project "$apphost_dir" 2>/dev/null) || secrets=""
names=$(printf '%s\n' "$secrets" | sed -n 's/^\([^=]*[^ =]\) *= .*/\1/p')
is_set() { printf '%s\n' "$names" | grep -qxF "$1"; }
secret_value() {
    printf '%s\n' "$secrets" | awk -v k="$1" 'index($0, k " = ") == 1 { print substr($0, length(k) + 4); exit }'
}
json_value() {
    perl -MJSON::PP -0777 -e '
        my ($file, $path) = @ARGV;
        open my $fh, "<", $file or exit;
        my $data = eval { JSON::PP->new->relaxed->decode(scalar <$fh>) } or exit;
        for my $part (split /:/, $path) {
            exit unless ref $data eq "HASH" && exists $data->{$part};
            $data = $data->{$part};
        }
        print $data unless ref $data;
    ' "$1" "$2" 2>/dev/null
}
# Same precedence as .NET configuration: env vars > user-secrets > Development > base.
effective() {
    local key=$1 env_name value file
    env_name=${key//:/__}
    value=${!env_name-}
    [ -n "$value" ] && { printf '%s' "$value"; return; }
    value=$(secret_value "$key")
    [ -n "$value" ] && { printf '%s' "$value"; return; }
    for file in "$apphost_dir/appsettings.Development.json" "$apphost_dir/appsettings.json"; do
        value=$(json_value "$file" "$key")
        [ -n "$value" ] && { printf '%s' "$value"; return; }
    done
}

echo "AppHost parameters (from $apphost_cs)"
params=$(perl -0777 -ne 'print "$1\n" while /AddParameter\(\s*"([^"]+)"/g' "$apphost_cs")
if [ -z "$params" ]; then
    info "No AddParameter(\"...\") calls found; AppHost.cs changed shape. Read it to find required parameters."
fi
for param in $params; do
    if is_set "Parameters:$param"; then ok "Parameters:$param is set"; else missing "Parameters:$param"; fi
done

ai=$(perl -0777 -ne 'print "$1 $2\n" while /ConfigureAIProvider\(\s*\w+\s*,\s*\w+\s*,\s*"(\w+)"\s*,\s*"([^"]+)"\s*\)/g' "$apphost_cs")
required_keys=$(perl -0777 -ne 'print "$1\n" if /foreach\s*\(\s*var\s+key\s+in\s+new\s*\[\]\s*\{([^}]*)\}/' "$apphost_cs" | grep -o '[A-Za-z][A-Za-z]*')
if [ -z "$ai" ] || [ -z "$required_keys" ]; then
    info "ConfigureAIProvider calls or their required keys not found; AppHost.cs changed shape. Read it and docs/configuration.md."
fi
ollama_models=""
if have ollama; then
    ollama_models=$(ollama list 2>/dev/null | awk 'NR > 1 { print $1 }')
fi
while read -r capability key_param; do
    [ -n "$capability" ] || continue
    provider=$(effective "AI:$capability:Provider")
    echo "AI $capability (provider: ${provider:-unset})"
    for key in $required_keys; do
        value=$(effective "AI:$capability:$key")
        if [ -n "$value" ]; then ok "AI:$capability:$key = $value"; else missing "AI:$capability:$key"; fi
    done
    case "$(printf '%s' "$provider" | tr '[:upper:]' '[:lower:]')" in
        foundry)
            endpoint=$(effective "AI:$capability:Endpoint")
            case "$endpoint" in
                https://*) ;;
                *) [ -n "$endpoint" ] && missing "AI:$capability:Endpoint must be HTTPS for Foundry" ;;
            esac
            if is_set "Parameters:$key_param"; then
                ok "Parameters:$key_param is set"
            else
                info "Parameters:$key_param not set; DefaultAzureCredential will be used (az login)"
            fi
            ;;
        ollama)
            endpoint=$(effective "AI:$capability:Endpoint")
            model=$(effective "AI:$capability:Model")
            if ! have ollama; then
                missing "ollama CLI (provider is Ollama)"
            elif ! curl -sf --max-time 3 "${endpoint%/}/api/tags" >/dev/null 2>&1; then
                missing "Ollama server not reachable at $endpoint; start Ollama"
            elif printf '%s\n' "$ollama_models" | grep -qxF -e "$model" -e "$model:latest"; then
                ok "Ollama model $model present"
            else
                missing "Ollama model $model; use the README 'Prepare the models' commands"
            fi
            ;;
        *)
            missing "AI:$capability:Provider must be Foundry or Ollama"
            ;;
    esac
done <<< "$ai"

echo
info "Checks assume the Development environment, which 'aspire run' and 'aspire start' use by default."
if [ "$problems" -gt 0 ]; then
    echo "$problems item(s) need action."
    exit 1
fi
echo "Setup looks complete. Start the AppHost and verify health."
