using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 3)
{
    Console.Error.WriteLine(
        "Usage: dotnet run --file validate-negative-tests.cs -- <validator.cs> <evaluation.json> <schema.json>");
    return 2;
}

var validatorPath = Path.GetFullPath(args[0]);
var dataPath = Path.GetFullPath(args[1]);
var schemaPath = Path.GetFullPath(args[2]);
var packageDirectory = Path.GetDirectoryName(dataPath)!;
var baseline = JsonNode.Parse(File.ReadAllText(dataPath))!.AsObject();

var cases = new (string Name, Action<JsonObject> Mutate, string ExpectedError)[]
{
    ("invalid packageId",
        root => root["packageId"] = "different-package",
        "/packageId"),
    ("extra top-level property",
        root => root["unexpected"] = true,
        "unexpected"),
    ("invalid case ID",
        root => root["cases"]![0]!["id"] = "bad-id",
        "/cases/0/id"),
    ("case ID/category mismatch",
        root => root["cases"]![0]!["id"] = "ZGY-NONE-999",
        "ID prefix does not match category"),
    ("wrong expectedClaims item type",
        root => root["cases"]![0]!["expectedClaims"] = new JsonArray(42),
        "/cases/0/expectedClaims/0"),
    ("invalid pending human-review metadata",
        root => root["humanReview"]!["status"] = "pending",
        "/humanReview/reviewedBy"),
    ("changed recall threshold",
        root => root["initialGates"]!["recallAtK"]!["threshold"] = 0.8,
        "/initialGates/recallAtK/threshold"),
    ("changed answer-support threshold",
        root => root["initialGates"]!["answerSupport"]!["threshold"] = 0.9,
        "/initialGates/answerSupport/threshold"),
    ("changed no-evidence threshold",
        root => root["initialGates"]!["noEvidenceBehavior"]!["threshold"] = 0.9,
        "/initialGates/noEvidenceBehavior/threshold"),
    ("multi-source case from a single source",
        root =>
        {
            var multi = root["cases"]!.AsArray().First(c => (string?)c!["id"] == "ZGY-MULTI-002")!;
            multi["expectedEvidence"]![1]!["sourceId"] = "zgy-policies";
            multi["expectedEvidence"]![1]!["sectionId"] = "late-checkout";
        },
        "at least two distinct sources"),
    ("derived denominator inconsistency",
        root =>
        {
            var addedCase = root["cases"]![0]!.DeepClone();
            addedCase["id"] = "ZGY-EXACT-999";
            root["cases"]!.AsArray().Add(addedCase);
        },
        "does not match derived"),
    ("authored answerable case still pending seed content",
        root => root["cases"]![0]!["status"] = "pending_seed_content",
        "/cases/0/status"),
    ("planned source still marked as requiring authoring",
        root => root["plannedSources"]![0]!["contentStatus"] = "step4_must_author",
        "/plannedSources/0/contentStatus"),
    ("active case while human review is pending",
        root =>
        {
            root["humanReview"]!["status"] = "pending";
            root["humanReview"]!["reviewedBy"] = null;
            root["humanReview"]!["reviewedOn"] = null;
        },
        "schema validation at /cases: The subschema passed evaluation but was expected to fail"),
    ("unreviewed source content in approved package",
        root => root["plannedSources"]![0]!["contentStatus"] = "authored_unreviewed",
        "approved package review cannot contain authored_unreviewed content"),
    ("pending human-review case in approved package",
        root => root["cases"]![0]!["status"] = "pending_human_review",
        "approved package review cannot retain pending_human_review status"),
    ("unconfirmed no-evidence case in approved package",
        root => root["cases"]!.AsArray().First(c => (string?)c!["category"] == "no_evidence")!["status"] =
            "pending_corpus_confirmation",
        "approved package review cannot retain pending_corpus_confirmation status"),
    ("package without corpus snapshot",
        root => root.Remove("corpusSnapshot"),
        "corpusSnapshot"),
    ("snapshot hash mismatch",
        root => root["corpusSnapshot"]!["files"]![0]!["sha256"] = new string('0', 64),
        "sha256 mismatch"),
    ("snapshot byte-length mismatch",
        root => root["corpusSnapshot"]!["files"]![0]!["byteLength"] = 1,
        "byteLength mismatch"),
    ("corpus file missing from snapshot",
        root => root["corpusSnapshot"]!["files"]!.AsArray().RemoveAt(0),
        "is not recorded in the snapshot"),
    ("snapshot file missing from corpus",
        root => root["corpusSnapshot"]!["files"]![0]!["path"] = "knowledge/hotel-zgy/does-not-exist.md",
        "recorded file is missing from the corpus"),
    ("snapshot sourceId mismatch with front matter",
        root =>
        {
            var file = root["corpusSnapshot"]!["files"]!.AsArray()
                .First(f => (string?)f!["sourceId"] == "zgy-parking")!;
            file["sourceId"] = "zgy-parking-garage";
        },
        "sourceId mismatch"),
    ("planned source path disagrees with snapshot",
        root => root["plannedSources"]![0]!["step4SuggestedPath"] = "knowledge/hotel-zgy/elsewhere.md",
        "does not match snapshot path")
};

var failures = new List<string>();
foreach (var test in cases)
{
    var mutated = baseline.DeepClone().AsObject();
    test.Mutate(mutated);
    var temporaryPath = Path.Combine(packageDirectory, $".validator-negative-{Guid.NewGuid():N}.json");

    try
    {
        File.WriteAllText(temporaryPath, mutated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(validatorPath);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(temporaryPath);
        startInfo.ArgumentList.Add(schemaPath);

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var combinedOutput = standardOutput + standardError;

        if (process.ExitCode == 0)
            failures.Add($"{test.Name}: mutation unexpectedly passed.");
        else if (!combinedOutput.Contains(test.ExpectedError, StringComparison.Ordinal))
            failures.Add($"{test.Name}: expected error containing '{test.ExpectedError}', got:{Environment.NewLine}{combinedOutput}");
        else
            Console.WriteLine($"PASS negative: {test.Name}");
    }
    finally
    {
        File.Delete(temporaryPath);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Negative validation failed with {failures.Count} error(s):");
    foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine($"PASS all {cases.Length} negative validation cases");
return 0;
