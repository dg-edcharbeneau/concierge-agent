# Shortcuts for common developer commands. Run `just` to list recipes.

eval_dir := "tests/Concierge.Evaluation"

_default:
    @just --list

# Start the Aspire AppHost (Postgres, Temporal, WorkflowServer and Web)
run:
    dotnet run --project src/Concierge.AppHost

# Run all tests (Docker must be running)
test:
    dotnet test Concierge.slnx

# Validate the retrieval evaluation package against its schema
eval-validate:
    dotnet run --file {{eval_dir}}/validate.cs {{eval_dir}}/hotel-zgy-eval.json {{eval_dir}}/hotel-zgy-eval.schema.json

# Run the retrieval evaluation; each run needs a new run id
eval-run run_id=("local-" + datetime("%Y%m%d-%H%M%S")):
    dotnet run --project {{eval_dir}}/runner/Concierge.Evaluation.Runner.csproj -- --run-id {{run_id}}
