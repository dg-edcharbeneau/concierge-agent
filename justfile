# Shortcuts for common developer commands. Run `just` to list recipes.

eval_dir := "tests/Concierge.Evaluation"

_default:
    @just --list

# Start the Aspire AppHost (Postgres, Temporal, WorkflowServer and Web)
run:
    dotnet run --project src/Concierge.AppHost

# Destructively reset this AppHost's persistent database and Temporal history volumes.
reset:
    #!/usr/bin/env bash
    set -uo pipefail
    apphost=src/Concierge.AppHost/Concierge.AppHost.csproj
    # Aspire removes volumes right after the AppHost exits, while its containers may still be
    # tearing down ("volume is in use"). Later attempts only clean up, so retry until they're gone.
    for attempt in 1 2 3 4 5 6; do
        if aspire stop --non-interactive --force --volumes --apphost "$apphost"; then
            exit 0
        fi
        echo "Volumes still in use; retrying in 5s (attempt $attempt of 6)." >&2
        sleep 5
    done
    echo "Reset failed: volumes are still in use after 30s. Check 'docker ps -a' for containers holding them." >&2
    exit 1

# Run all tests (Docker must be running)
test:
    dotnet test Concierge.slnx

# Live-check Geoapify place resolution and Open-Meteo weather (uses AppHost user-secrets)
live-place-weather:
    dotnet run --file tests/Concierge.LiveChecks/place-weather-smoke.cs

# Live-check a Geoapify driving route from the hotel to South Beach (uses AppHost user-secrets)
live-driving-route:
    dotnet run --file tests/Concierge.LiveChecks/driving-route-smoke.cs

# Live-check Step 6 weather, route and ride actions through the running AppHost's Web API
live-concierge-actions:
    dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs

# Weather acceptance: fresh stays x repeated rounds; fails unless every turn fetches and answers the requested dates
live-weather-reliability stays="5" rounds="4":
    dotnet run --file tests/Concierge.LiveChecks/weather-reliability.cs {{stays}} {{rounds}}

# Validate the retrieval evaluation package against its schema
eval-validate:
    dotnet run --file {{eval_dir}}/validate.cs {{eval_dir}}/hotel-zgy-eval.json {{eval_dir}}/hotel-zgy-eval.schema.json

# Run the retrieval evaluation; each run needs a new run id
eval-run run_id=("local-" + datetime("%Y%m%d-%H%M%S")):
    dotnet run --project {{eval_dir}}/runner/Concierge.Evaluation.Runner.csproj -- --run-id {{run_id}}
