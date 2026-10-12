#:package JsonSchema.Net@9.4.0

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run --file validate.cs -- <evaluation.json> <schema.json>");
    return 2;
}

var dataPath = Path.GetFullPath(args[0]);
var schemaPath = Path.GetFullPath(args[1]);
var errors = new List<string>();

JsonDocument LoadJson(string path, string label)
{
    try
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }
    catch (Exception ex)
    {
        errors.Add($"{label}: cannot parse '{path}': {ex.Message}");
        return JsonDocument.Parse("{}");
    }
}

using var data = LoadJson(dataPath, "data");
using var schema = LoadJson(schemaPath, "schema");
var root = data.RootElement;

if (schema.RootElement.ValueKind != JsonValueKind.Object ||
    !schema.RootElement.TryGetProperty("$schema", out var schemaDialect) ||
    schemaDialect.GetString() != "https://json-schema.org/draft/2020-12/schema")
{
    errors.Add("schema: expected JSON Schema draft 2020-12.");
}
else
{
    try
    {
        var jsonSchema = JsonSchema.FromText(schema.RootElement.GetRawText());
        var schemaResults = jsonSchema.Evaluate(root, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });

        if (!schemaResults.IsValid)
        {
            errors.AddRange((schemaResults.Details ?? [])
                .Where(result => !result.IsValid && result.Errors is not null)
                .SelectMany(result => result.Errors!.Values.Select(message =>
                    $"schema validation at {result.InstanceLocation}: {message}")));
        }
    }
    catch (Exception ex)
    {
        errors.Add($"schema: cannot build or evaluate '{schemaPath}': {ex.Message}");
    }
}

if (errors.Count > 0)
{
    Console.Error.WriteLine($"Validation failed with {errors.Count} error(s):");
    foreach (var error in errors.Distinct()) Console.Error.WriteLine($"- {error}");
    return 1;
}

string RequiredString(JsonElement parent, string name, string context)
{
    if (!parent.TryGetProperty(name, out var value) ||
        value.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(value.GetString()))
    {
        errors.Add($"{context}: '{name}' must be a non-empty string.");
        return "";
    }

    return value.GetString()!;
}

JsonElement RequiredObject(JsonElement parent, string name, string context)
{
    if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
    {
        errors.Add($"{context}: '{name}' must be an object.");
        return default;
    }

    return value;
}

JsonElement RequiredArray(JsonElement parent, string name, string context)
{
    if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
    {
        errors.Add($"{context}: '{name}' must be an array.");
        return default;
    }

    return value;
}

int RequiredInt(JsonElement parent, string name, string context)
{
    if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
    {
        errors.Add($"{context}: '{name}' must be an integer.");
        return 0;
    }

    return result;
}

double RequiredDouble(JsonElement parent, string name, string context)
{
    if (!parent.TryGetProperty(name, out var value) || !value.TryGetDouble(out var result))
    {
        errors.Add($"{context}: '{name}' must be numeric.");
        return 0;
    }

    return result;
}

void ValidateLocalReference(string reference, string context)
{
    var parts = reference.Split('#', 2);
    var targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dataPath)!, parts[0]));
    if (!File.Exists(targetPath))
    {
        errors.Add($"{context}: local link target does not exist: {reference}");
        return;
    }

    if (parts.Length == 1 || string.IsNullOrWhiteSpace(parts[1]) ||
        !targetPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var expectedAnchor = parts[1];
    var headings = File.ReadLines(targetPath)
        .Where(line => Regex.IsMatch(line, @"^#{1,6}\s+"))
        .Select(line => Regex.Replace(line, @"^#{1,6}\s+", ""))
        .Select(line => Regex.Replace(line.ToLowerInvariant(), @"[^\p{L}\p{N}\s-]", ""))
        .Select(line => Regex.Replace(line.Trim(), @"\s+", "-"))
        .ToHashSet(StringComparer.Ordinal);
    if (!headings.Contains(expectedAnchor))
    {
        errors.Add($"{context}: Markdown anchor does not resolve: {reference}");
    }
}

var requiredTopLevel = new[]
{
    "packageId", "scope", "retrievalPolicy", "initialGates",
    "answerSupportRules", "sourceIdConvention", "humanReview",
    "provenance", "plannedSources", "corpusSnapshot", "cases"
};
foreach (var property in requiredTopLevel)
{
    if (!root.TryGetProperty(property, out _))
    {
        errors.Add($"data: missing top-level property '{property}'.");
    }
}

var schemaReference = RequiredString(root, "$schema", "data");
if (!string.IsNullOrEmpty(schemaReference))
{
    ValidateLocalReference(schemaReference, "data.$schema");
    if (!string.Equals(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dataPath)!, schemaReference)),
                       schemaPath, StringComparison.Ordinal))
    {
        errors.Add("data.$schema does not resolve to the schema argument.");
    }
}

var retrieval = RequiredObject(root, "retrievalPolicy", "data");
var finalK = RequiredInt(retrieval, "finalK", "retrievalPolicy");
if (finalK < 1) errors.Add("retrievalPolicy.finalK must be positive.");
if (RequiredInt(retrieval, "rrfRankBase", "retrievalPolicy") != 1)
    errors.Add("retrievalPolicy.rrfRankBase must be 1.");
if (RequiredInt(retrieval, "rrfK", "retrievalPolicy") != 60)
    errors.Add("retrievalPolicy.rrfK must be 60.");

var sourcePattern = new Regex(@"^zgy-[a-z0-9]+(?:-[a-z0-9]+)*$");
var sectionPattern = new Regex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$");
var sourceSections = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
var sourceContentStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
var sources = RequiredArray(root, "plannedSources", "data");
if (sources.ValueKind == JsonValueKind.Array)
{
    foreach (var source in sources.EnumerateArray())
    {
        var sourceId = RequiredString(source, "sourceId", "plannedSource");
        if (!sourcePattern.IsMatch(sourceId))
            errors.Add($"plannedSource '{sourceId}': invalid source ID.");
        if (sourceSections.ContainsKey(sourceId))
            errors.Add($"plannedSource '{sourceId}': duplicate source ID.");

        var sections = new HashSet<string>(StringComparer.Ordinal);
        var requiredSections = RequiredArray(source, "requiredSections", $"plannedSource '{sourceId}'");
        if (requiredSections.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in requiredSections.EnumerateArray())
            {
                var sectionId = section.ValueKind == JsonValueKind.String ? section.GetString()! : "";
                if (!sectionPattern.IsMatch(sectionId))
                    errors.Add($"plannedSource '{sourceId}': invalid section ID '{sectionId}'.");
                if (!sections.Add(sectionId))
                    errors.Add($"plannedSource '{sourceId}': duplicate section ID '{sectionId}'.");
            }
        }
        if (sections.Count == 0) errors.Add($"plannedSource '{sourceId}': no required sections.");
        sourceSections[sourceId] = sections;

        var contentStatus = RequiredString(source, "contentStatus", $"plannedSource '{sourceId}'");
        if (contentStatus is not ("authored_unreviewed" or "approved"))
            errors.Add($"plannedSource '{sourceId}': contentStatus must be authored_unreviewed or approved.");
        sourceContentStatuses[sourceId] = contentStatus;
        var trace = RequiredString(source, "trace", $"plannedSource '{sourceId}'");
        if (trace.Length > 0) ValidateLocalReference(trace, $"plannedSource '{sourceId}'");
        RequiredString(source, "notes", $"plannedSource '{sourceId}'");
    }
}

var allowedCategories = new HashSet<string>
{
    "exact_term", "paraphrase", "multi_source", "no_evidence"
};
var categoryIdSegments = new Dictionary<string, string>
{
    ["exact_term"] = "EXACT",
    ["paraphrase"] = "PARA",
    ["multi_source"] = "MULTI",
    ["no_evidence"] = "NONE"
};
var allowedStatuses = new HashSet<string>
{
    "pending_human_review", "pending_corpus_confirmation", "active", "retired"
};
var caseIds = new HashSet<string>(StringComparer.Ordinal);
var caseStatuses = new Dictionary<string, (string Category, string Status, HashSet<string> EvidenceSources)>(
    StringComparer.Ordinal);
var categoryCounts = allowedCategories.ToDictionary(category => category, _ => 0);
var statusCounts = allowedStatuses.ToDictionary(status => status, _ => 0);
var answerableCases = 0;
var noEvidenceCases = 0;
var expectedEvidenceCount = 0;
var cases = RequiredArray(root, "cases", "data");

if (cases.ValueKind == JsonValueKind.Array)
{
    foreach (var item in cases.EnumerateArray())
    {
        var id = RequiredString(item, "id", "case");
        if (!caseIds.Add(id)) errors.Add($"case '{id}': duplicate case ID.");
        var context = $"case '{id}'";
        var category = RequiredString(item, "category", context);
        var status = RequiredString(item, "status", context);
        var question = RequiredString(item, "question", context);
        if (question.Trim().Length == 0) errors.Add($"{context}: empty question.");
        if (!allowedCategories.Contains(category))
            errors.Add($"{context}: invalid category '{category}'.");
        else
        {
            categoryCounts[category]++;
            if (!id.StartsWith($"ZGY-{categoryIdSegments[category]}-", StringComparison.Ordinal))
                errors.Add($"{context}: ID prefix does not match category '{category}'.");
        }
        if (!allowedStatuses.Contains(status))
            errors.Add($"{context}: invalid status '{status}'.");
        else
            statusCounts[status]++;

        var evidence = RequiredArray(item, "expectedEvidence", context);
        var evidenceKeys = new HashSet<string>(StringComparer.Ordinal);
        var evidenceSources = new HashSet<string>(StringComparer.Ordinal);
        if (evidence.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in evidence.EnumerateArray())
            {
                var sourceId = RequiredString(entry, "sourceId", context);
                evidenceSources.Add(sourceId);
                var sectionId = RequiredString(entry, "sectionId", context);
                var key = $"{sourceId}#{sectionId}";
                if (!evidenceKeys.Add(key)) errors.Add($"{context}: duplicate evidence '{key}'.");
                if (!sourceSections.TryGetValue(sourceId, out var sections) || !sections.Contains(sectionId))
                    errors.Add($"{context}: evidence '{key}' is absent from the planned source registry.");
            }
        }

        var label = RequiredString(item, "answerSupportLabel", context);
        if (category == "no_evidence")
        {
            noEvidenceCases++;
            if (evidenceKeys.Count != 0) errors.Add($"{context}: no-evidence case must have no expected evidence.");
            if (label != "staff_fallback") errors.Add($"{context}: no-evidence label must be staff_fallback.");
            if (status == "pending_human_review")
                errors.Add($"{context}: no-evidence case cannot await authored-content review.");
        }
        else if (allowedCategories.Contains(category))
        {
            answerableCases++;
            expectedEvidenceCount += evidenceKeys.Count;
            if (evidenceKeys.Count == 0) errors.Add($"{context}: answerable case has no expected evidence.");
            if (label != "supported") errors.Add($"{context}: answerable label must be supported.");
            if (status == "pending_corpus_confirmation")
                errors.Add($"{context}: answerable case cannot use pending_corpus_confirmation.");
            if (category == "multi_source" && evidenceKeys.Count < 2)
                errors.Add($"{context}: multi-source case requires at least two evidence keys.");
            // Two sections of one document test section separation, not cross-source retrieval.
            if (category == "multi_source" && evidenceSources.Count < 2)
                errors.Add($"{context}: multi-source case requires evidence from at least two distinct sources.");
        }
        caseStatuses[id] = (category, status, evidenceSources);

        var trace = RequiredString(item, "trace", context);
        if (trace.Length > 0) ValidateLocalReference(trace, context);
        RequiredString(item, "seedAuthoringNotes", context);
        RequiredArray(item, "expectedClaims", context);
    }
}

var provenance = RequiredArray(root, "provenance", "data");
if (provenance.ValueKind == JsonValueKind.Array)
{
    foreach (var item in provenance.EnumerateArray())
    {
        var id = RequiredString(item, "id", "provenance");
        var path = RequiredString(item, "path", $"provenance '{id}'");
        if (path.Length > 0) ValidateLocalReference(path, $"provenance '{id}'");
    }
}

string? NullableString(JsonElement parent, string name) =>
    parent.GetProperty(name).ValueKind == JsonValueKind.Null
        ? null
        : parent.GetProperty(name).GetString();

var humanReview = RequiredObject(root, "humanReview", "data");
var reviewStatus = RequiredString(humanReview, "status", "humanReview");
var reviewedBy = NullableString(humanReview, "reviewedBy");
var reviewedOn = NullableString(humanReview, "reviewedOn");
if (reviewStatus == "pending" && (reviewedBy is not null || reviewedOn is not null))
    errors.Add("humanReview: pending review must not identify a reviewer or review date.");
if (reviewStatus != "pending" &&
    (string.IsNullOrWhiteSpace(reviewedBy) || string.IsNullOrWhiteSpace(reviewedOn)))
    errors.Add($"humanReview: {reviewStatus} review requires reviewedBy and reviewedOn.");

if (reviewStatus == "pending")
{
    foreach (var (sourceId, contentStatus) in sourceContentStatuses)
    {
        if (contentStatus != "authored_unreviewed")
            errors.Add($"plannedSource '{sourceId}': pending package review requires contentStatus authored_unreviewed.");
    }

    foreach (var (caseId, state) in caseStatuses)
    {
        if (state.Status == "active")
            errors.Add($"case '{caseId}': cannot be active while humanReview is pending.");
        if (state.Category != "no_evidence" && state.Status is not ("pending_human_review" or "retired"))
            errors.Add($"case '{caseId}': authored answerable content awaiting review must use pending_human_review or retired.");
    }
}
else if (reviewStatus == "approved")
{
    foreach (var (sourceId, contentStatus) in sourceContentStatuses)
    {
        if (contentStatus == "authored_unreviewed")
            errors.Add($"plannedSource '{sourceId}': approved package review cannot contain authored_unreviewed content.");
    }

    foreach (var (caseId, state) in caseStatuses)
    {
        if (state.Status is "pending_human_review" or "pending_corpus_confirmation")
            errors.Add($"case '{caseId}': approved package review cannot retain {state.Status} status.");
    }
}

foreach (var (caseId, state) in caseStatuses)
{
    if (state.Status == "pending_human_review" && reviewStatus != "pending")
        errors.Add($"case '{caseId}': pending_human_review status requires humanReview pending.");
}

foreach (var (caseId, state) in caseStatuses.Where(pair => pair.Value.Status == "active"))
{
    if (reviewStatus != "approved")
        errors.Add($"case '{caseId}': active status requires approved humanReview.");

    foreach (var sourceId in state.EvidenceSources)
    {
        if (!sourceContentStatuses.TryGetValue(sourceId, out var contentStatus) ||
            contentStatus != "approved")
        {
            errors.Add($"case '{caseId}': active status requires every expected source to have contentStatus approved; '{sourceId}' does not.");
        }
    }
}

// Corpus snapshot: the recorded snapshot must match every Markdown file under the corpus root, byte for byte.
var hasSnapshot = root.TryGetProperty("corpusSnapshot", out var snapshot) &&
                  snapshot.ValueKind == JsonValueKind.Object;
if (!hasSnapshot)
    errors.Add("corpusSnapshot: package requires a recorded corpus snapshot.");
if (hasSnapshot)
{
    var snapshotRoot = RequiredString(snapshot, "root", "corpusSnapshot").TrimEnd('/');
    var repoRootRelative = RequiredString(snapshot, "repositoryRootFromPackage", "corpusSnapshot");
    var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dataPath)!, repoRootRelative));
    var corpusRoot = Path.GetFullPath(Path.Combine(repositoryRoot, snapshotRoot));
    var frontMatterSourceId = new Regex(@"(?m)^sourceId:\s*(.+?)\s*$", RegexOptions.CultureInvariant);

    if (!Directory.Exists(corpusRoot))
    {
        errors.Add($"corpusSnapshot: corpus root does not exist: {snapshotRoot}");
    }
    else
    {
        var actualFiles = Directory.GetFiles(corpusRoot, "*.md", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(repositoryRoot, file).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);
        var snapshotPaths = new HashSet<string>(StringComparer.Ordinal);
        var snapshotSourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = RequiredArray(snapshot, "files", "corpusSnapshot");
        if (files.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in files.EnumerateArray())
            {
                var path = RequiredString(entry, "path", "corpusSnapshot.files");
                var context = $"corpusSnapshot file '{path}'";
                var sourceId = RequiredString(entry, "sourceId", context);
                var sha256 = RequiredString(entry, "sha256", context);
                var byteLength = RequiredInt(entry, "byteLength", context);
                if (!snapshotPaths.Add(path)) errors.Add($"{context}: duplicate path.");
                if (!path.StartsWith(snapshotRoot + "/", StringComparison.Ordinal))
                    errors.Add($"{context}: path is outside the corpus root '{snapshotRoot}'.");
                if (snapshotSourceIds.TryGetValue(sourceId, out var otherPath))
                    errors.Add($"{context}: sourceId '{sourceId}' is also recorded for '{otherPath}'.");
                else
                    snapshotSourceIds[sourceId] = path;

                if (!actualFiles.Contains(path))
                {
                    errors.Add($"{context}: recorded file is missing from the corpus.");
                    continue;
                }

                var bytes = File.ReadAllBytes(Path.Combine(repositoryRoot, path));
                var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                    .ToLowerInvariant();
                if (!string.Equals(actualHash, sha256, StringComparison.Ordinal))
                    errors.Add($"{context}: sha256 mismatch (recorded {sha256}, actual {actualHash}).");
                if (bytes.Length != byteLength)
                    errors.Add($"{context}: byteLength mismatch (recorded {byteLength}, actual {bytes.Length}).");

                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var frontMatterEnd = text.StartsWith("---", StringComparison.Ordinal)
                    ? text.IndexOf("\n---", StringComparison.Ordinal)
                    : -1;
                var declared = frontMatterEnd < 0 ? null : frontMatterSourceId.Match(text[..frontMatterEnd]);
                if (declared is null || !declared.Success)
                    errors.Add($"{context}: file front matter declares no sourceId.");
                else if (!string.Equals(declared.Groups[1].Value.Trim('"', '\''), sourceId, StringComparison.Ordinal))
                    errors.Add($"{context}: sourceId mismatch (recorded '{sourceId}', front matter '{declared.Groups[1].Value}').");
            }
        }

        foreach (var missing in actualFiles.Except(snapshotPaths).Order(StringComparer.Ordinal))
            errors.Add($"corpusSnapshot: corpus file '{missing}' is not recorded in the snapshot.");

        if (sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                var sourceId = source.GetProperty("sourceId").GetString() ?? "";
                var suggestedPath = source.GetProperty("step4SuggestedPath").GetString() ?? "";
                if (!snapshotSourceIds.TryGetValue(sourceId, out var recordedPath))
                    errors.Add($"plannedSource '{sourceId}': source is not recorded in the corpus snapshot.");
                else if (!string.Equals(recordedPath, suggestedPath, StringComparison.Ordinal))
                    errors.Add($"plannedSource '{sourceId}': step4SuggestedPath '{suggestedPath}' does not match snapshot path '{recordedPath}'.");
            }
        }
    }
}

var gates = RequiredObject(root, "initialGates", "data");
var gateSummaries = new Dictionary<string, (double Threshold, int Denominator, int Minimum)>();
void ValidateGate(string name, int actualDenominator)
{
    var gate = RequiredObject(gates, name, "initialGates");
    var threshold = RequiredDouble(gate, "threshold", $"initialGates.{name}");
    var denominator = RequiredInt(gate, "plannedDenominator", $"initialGates.{name}");
    var minimum = RequiredInt(gate, "minimumPassingCount", $"initialGates.{name}");
    if (threshold is < 0 or > 1) errors.Add($"initialGates.{name}: threshold must be between 0 and 1.");
    if (denominator != actualDenominator)
        errors.Add($"initialGates.{name}: denominator {denominator} does not match derived {actualDenominator}.");
    var expectedMinimum = (int)Math.Ceiling(threshold * denominator);
    if (minimum != expectedMinimum)
        errors.Add($"initialGates.{name}: minimum {minimum} must equal ceiling(threshold * denominator) = {expectedMinimum}.");
    gateSummaries[name] = (threshold, denominator, minimum);
}

var recallGate = RequiredObject(gates, "recallAtK", "initialGates");
if (RequiredInt(recallGate, "k", "initialGates.recallAtK") != finalK)
    errors.Add("initialGates.recallAtK.k must equal retrievalPolicy.finalK.");
ValidateGate("recallAtK", expectedEvidenceCount);
ValidateGate("answerSupport", answerableCases);
ValidateGate("noEvidenceBehavior", noEvidenceCases);

if (categoryCounts.Values.Any(count => count == 0))
    errors.Add("data: every allowed category must have at least one case.");

// Check local links in this package README as part of deterministic package validation.
var readmePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "README.md");
if (!File.Exists(readmePath))
{
    errors.Add("package: README.md is missing.");
}
else
{
    foreach (Match match in Regex.Matches(File.ReadAllText(readmePath), @"\[[^\]]+\]\(([^)]+)\)"))
    {
        var link = match.Groups[1].Value;
        if (Uri.TryCreate(link, UriKind.Absolute, out _)) continue;
        var parts = link.Split('#', 2);
        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(readmePath)!, parts[0]));
        if (!File.Exists(target)) errors.Add($"README: local link target does not exist: {link}");
    }
}

if (errors.Count > 0)
{
    Console.Error.WriteLine($"Validation failed with {errors.Count} error(s):");
    foreach (var error in errors) Console.Error.WriteLine($"- {error}");
    return 1;
}

Console.WriteLine($"PASS {Path.GetFileName(dataPath)}");
Console.WriteLine($"cases={caseIds.Count}; expectedEvidence={expectedEvidenceCount}; retrievalK={finalK}");
Console.WriteLine("categories=" + string.Join(",", categoryCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")));
if (hasSnapshot)
    Console.WriteLine($"corpusSnapshot={snapshot.GetProperty("root").GetString()}; files={snapshot.GetProperty("files").GetArrayLength()}; hashes verified");
Console.WriteLine("statuses=" + string.Join(",", statusCounts.Where(pair => pair.Value > 0).OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")));
string GateSummary(string label, string gateName)
{
    var gate = gateSummaries[gateName];
    return $"{label}>={gate.Threshold.ToString("F2", CultureInfo.InvariantCulture)} ({gate.Minimum}/{gate.Denominator})";
}
Console.WriteLine("gates=" + string.Join(", ",
    GateSummary($"Recall@{recallGate.GetProperty("k").GetInt32()}", "recallAtK"),
    GateSummary("answerSupport", "answerSupport"),
    GateSummary("noEvidence", "noEvidenceBehavior")));
return 0;
