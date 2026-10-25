#:project ../../src/Concierge.WorkflowServer/Concierge.WorkflowServer.csproj
// The transitive OllamaSharp generator targets a newer compiler; it is not used by this check.
#:property NoWarn=$(NoWarn);CS9057
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

// Opt-in Step 6 check against the already-running Aspire AppHost and its configured model.
// Mocked unit tests cannot prove the live model actually calls the tools, so this drives real
// guest turns through the Web API and reads the tool calls back from Temporal history.
// Aspire resource environment values stay in memory and are never written to the console.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.Workflows;
using Npgsql;
using TemporalCommunity.Extensions.AI;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

if (args is ["selftest"])
{
    return RunReportSelfTests();
}

var repoRoot = Path.GetFullPath(Path.Combine(ScriptDirectory(), "..", ".."));
var stage = "reading Aspire resource metadata";
var report = new CheckReport();
try
{
    var runtime = await ReadRuntimeAsync(repoRoot);

    stage = "seeding a temporary verified guest";
    var familyName = $"Livecheck{Random.Shared.Next(100_000, 999_999)}";
    var roomNumber = string.Empty;
    var stayId = Guid.NewGuid();
    for (var attempt = 0; ; attempt++)
    {
        // Fictional 9xxx rooms; earlier runs may still hold a room, which the overlap constraint rejects.
        roomNumber = Random.Shared.Next(9000, 10_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await SeedGuestAsync(runtime.ConciergeConnectionString, stayId, familyName, roomNumber);
            break;
        }
        catch (PostgresException exception) when (exception.SqlState == "23P01" && attempt < 5)
        {
        }
    }

    stage = "connecting to Temporal";
    var temporalClient = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions
    {
        TargetHost = runtime.TemporalAddress,
        Namespace = runtime.TemporalNamespace,
        DataConverter = DurableAIDataConverter.Instance,
    });
    var workflowId = ConciergeStayWorkflowIds.ForStay(stayId);
    Console.WriteLine($"WORKFLOW  {workflowId}");

    // Only the local Aspire dev certificate is accepted, and only for loopback.
    using var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true,
    };
    using var http = new HttpClient(handler)
    {
        BaseAddress = new Uri(runtime.WebUrl),
        // Above the workflow's 180s action-turn deadline so slow turns surface the workflow reply.
        Timeout = TimeSpan.FromSeconds(200),
    };

    stage = "verifying the guest session";
    var sessionResponse = await http.PostAsJsonAsync(
        "/api/concierge/sessions", new ConciergeVerificationRequest(familyName, roomNumber));
    if (!sessionResponse.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"Session verification returned {(int)sessionResponse.StatusCode}: {await sessionResponse.Content.ReadAsStringAsync()}");
    }

    var session = (await sessionResponse.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);

    // SeedGuestAsync books one night: check-in today, checkout tomorrow (hotel-local).
    var checkoutDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
        DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime)
        .AddDays(1).DayOfWeek.ToString();
    var dayAfterTomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
        DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime)
        .AddDays(2).DayOfWeek.ToString();
    var threeDaysOut = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
        DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime)
        .AddDays(3).DayOfWeek.ToString();
    var fourDaysOut = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
        DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime)
        .AddDays(4).DayOfWeek.ToString();
    Scenario[] scenarios =
    [
        new("What can you help with?", [], ["weather", "direction|route|travel", "ride", "nearby|near"]),
        new("What's the weather like today?", ["GetWeather"], ["°F|degrees"]),
        new("Will it rain in Fort Lauderdale tomorrow?", ["GetWeather"], ["Fort Lauderdale"]),
        new("How long is the drive to South Beach?", ["GetRoute"], ["mi", "min"]),
        new("What is the nearest pharmacy?", ["FindNearbyPlaces"], ["pharmac", "mile"]),
        new("What is the nearest movie theater?", ["FindNearbyPlaces"], ["theater|cinema", "mile"]),
        new("What are Wayan Miami's hours?", ["GetPlaceOpeningHours"], ["Wayan", "5:30 PM|17:30"]),
        // Answered from the verified stay dates in the guest context; no tool is needed.
        new("How long is my stay and what day am I checking out?", [], ["one night|1 night", checkoutDay]),
        new("How much is a ride?", [], ["complimentary|no charge|no cost"]),
        new("Can you Book me ride to South Beach please?", ["ProposeRide"], ["No ride has been booked"]),
        new("yes, confirm", ["ConfirmRide"], ["all set|booked|pickup"]),
        // Without a ride id the instructions route status through GetRideHistory first.
        // An immediate ride is picked up at booking time, so the reply must give its arrival time.
        new("Is my ride almost here?", ["GetRideStatus|GetRideHistory"], ["should arrive around"]),
        new("Please book me a ride to South Beach tomorrow at 10 am.", ["ProposeRide"], ["10:00 AM|10 AM|10am|10 a.m."]),
        new("confirm ride", ["ConfirmRide"], ["all set|booked|pickup"]),
        new("Show me my upcoming rides.", ["GetRideHistory"], ["10:00 AM|10 AM|10am|10 a.m."]),
        // Cancellation needs ride ids, so the model must request history with purpose "select".
        new("Cancel my ride tomorrow at 10 AM, please.", ["GetRideHistory", "ProposeRideCancellation"], ["Would you like me to cancel it"]),
        new("Yes, cancel that one.", ["ConfirmRideCancellation"], ["cancel"]),
        // Phrasings from live session 0021 that previously failed.
        new("What's the weather for the next two days?", ["GetWeather"], ["today", "tomorrow"]),
        // Live 2026-10-21: the model called today/tonight/tomorrow and the workflow refused it.
        new("what's the weather for the next 3 days in plantation?", ["GetWeather"], ["today", "tomorrow", dayAfterTomorrow, "Plantation"]),
        // External review 2026-10-22: an explicit start day must not be replaced by today.
        new("What's the weather for the next three days starting tomorrow?", ["GetWeather"], ["tomorrow", dayAfterTomorrow, threeDaysOut]),
        new($"What's the weather for the next three days starting {dayAfterTomorrow}?", ["GetWeather"], [dayAfterTomorrow, threeDaysOut, fourDaysOut]),
        new("How long is the drive to South Beach, Miami Beach, FL 33139?", ["GetRoute"], ["South Beach"]),
        new("Book me a ride to South Beach.", ["ProposeRide"], ["No ride has been booked"]),
        // An offer survives one intervening turn without a ride action, so acceptance after a
        // deferral books the offered ride.
        new("I need a moment to think.", [], [], ForbidRideExecution: true),
        new("I'd like to go ahead with that ride.", ["ConfirmRide"], ["all set|booked|pickup"]),
        // Live voice 2026-10-21: a filler-led request, then the answer to the concierge's question.
        // The previous offer was just booked, so nothing is pending to confirm.
        new("Okay. Um, could you book me a ride?", [], [], ForbidRideExecution: true),
        new("To South Beach, right now.", ["ProposeRide"], ["No ride has been booked"]),
        new("That sounds good; please arrange it.", ["ConfirmRide"], ["all set|booked|pickup"]),
        // The preceding turn confirmed a South Beach ride; an explicit repeat request must
        // create a fresh offer rather than selecting ride history.
        .. SouthBeachRepeatBookingScenarios("Yes, please.", "south-beach-yes"),
        // Each acknowledgement immediately follows its own successful booking; putting them
        // back-to-back would test the just-consumed-offer state only for the first phrase.
        .. SouthBeachRepeatBookingScenarios("Thanks.", "south-beach-thanks"),
        // Live 2026-10-22: Geoapify returned two building records plus the venue for one address
        // and the guest was asked to choose between three copies of the same place.
        new("Book me a ride to Wayan Miami, 50 Northwest 24th Street.", ["ProposeRide"], ["No ride has been booked", "Wayan"]),
        // Geoapify may clarify or fail to resolve this address; only a real pending offer may be confirmed.
        // Keep this last so a successful proposal cannot affect any later scenario.
        new("Book me a ride to Walgreens, 3103 Biscayne Boulevard.", ["ProposeRide"], ["No ride has been booked"], AllowRideProposalFailure: true),
    ];

    var seenScheduled = new HashSet<long>();
    var prerequisiteTracker = new ActionPrerequisiteTracker();
    for (var index = 0; index < scenarios.Length; index++)
    {
        var scenario = scenarios[index];
        var blockedReason = scenario.RequiresActionPrerequisite is { } prerequisite
            ? prerequisiteTracker.GetBlockReason(prerequisite)
            : null;
        stage = $"turn {index + 1} ({scenario.Message})";
        if (blockedReason is not null)
        {
            report.SkipScenario(
                $"turn {index + 1} not sent: dependent action checks blocked; {blockedReason}");
            if (scenario.ProvidesActionPrerequisite is { } skippedPrerequisite)
            {
                prerequisiteTracker.Complete(
                    skippedPrerequisite,
                    false,
                    $"turn {index + 1} was not sent because {blockedReason}");
            }

            continue;
        }

        var failuresAtScenarioStart = report.FailureCount;
        var stopwatch = Stopwatch.StartNew();
        var turnId = $"step6-live-{index}-{Guid.NewGuid():N}";
        using var turnResponse = await PostTurnWithTransientRetryAsync(
            http,
            new ConciergeTurnRequest(turnId, scenario.Message, "web"));
        var body = await turnResponse.Content.ReadAsStringAsync();
        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"GUEST  {scenario.Message}");
        if (!turnResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"HTTP   {(int)turnResponse.StatusCode} {body}");
            report.Fail($"turn {index + 1}: HTTP {(int)turnResponse.StatusCode}");
            if (scenario.ProvidesActionPrerequisite is { } failedPrerequisite)
            {
                prerequisiteTracker.Complete(
                    failedPrerequisite,
                    false,
                    $"turn {index + 1} did not complete because the HTTP request failed");
            }
            report.CompleteScenario(failuresAtScenarioStart);
            continue;
        }

        var answer = JsonSerializer.Deserialize<ConciergeTurnResult>(body, JsonSerializerOptions.Web)!.AssistantMessage;
        var calls = await ReadNewToolCallsAsync(temporalClient, workflowId, seenScheduled);
        foreach (var call in calls)
        {
            Console.WriteLine($"TOOL   {call.Name} {Clip(call.Arguments, 160)} => {Clip(call.Result, 220)}");
        }

        Console.WriteLine($"AGENT  {answer}  ({stopwatch.Elapsed.TotalSeconds:F1}s)");

        var expectedToolsSucceeded = scenario.ExpectedTools.All(expected =>
            calls.Any(call => expected.Split('|').Contains(call.Name)
                && ToolCallSucceeded(call, scenario)));
        foreach (var expected in scenario.ExpectedTools)
        {
            if (!calls.Any(call => expected.Split('|').Contains(call.Name)))
            {
                var label = scenario.ProvidesActionPrerequisite is null
                    ? "expected action"
                    : "primary action-selection failure";
                report.Fail($"turn {index + 1}: {label} {expected}; called [{string.Join(", ", calls.Select(call => call.Name))}]");
            }
        }

        foreach (var call in calls.Where(call => call.Failed
            || (!scenario.AllowRideProposalFailure || call.Name != "ProposeRide")
                && (call.Result.Contains("NeedsClarification", StringComparison.Ordinal)
                    || call.Result.Contains("Unavailable\"", StringComparison.Ordinal))))
        {
            report.Fail($"turn {index + 1}: {call.Name} did not succeed ({Clip(call.Result, 120)})");
        }

        if (scenario.ForbidRideExecution
            && calls.Any(call => call.Name is "ConfirmRide" or "ConfirmRideCancellation" or "CancelRide"))
        {
            report.Fail($"turn {index + 1}: a non-confirming turn executed a ride action");
        }

        if (scenario.ForbidRideProposal && calls.Any(call => call.Name is "ProposeRide"))
        {
            report.Fail($"turn {index + 1}: a turn after a booking re-offered a ride");
        }

        if (StringComparer.Ordinal.Equals(scenario.Message, "How much is a ride?")
            && calls.Any(call => call.Name is "ProposeRide" or "ConfirmRide"))
        {
            report.Fail($"turn {index + 1}: a fare question triggered a ride proposal or booking");
        }

        if (StringComparer.Ordinal.Equals(scenario.Message, "What are Wayan Miami's hours?")
            && calls.Any(call => call.Name == "FindNearbyPlaces"))
        {
            report.Fail($"turn {index + 1}: named-place opening hours fell back to nearby-place search");
        }

        if (StringComparer.Ordinal.Equals(scenario.Message, "How much is a ride?")
            && (answer.TrimEnd().EndsWith("?", StringComparison.Ordinal)
                || answer.Contains("would you like me to arrange", StringComparison.OrdinalIgnoreCase)
                || answer.Contains("let me know if", StringComparison.OrdinalIgnoreCase)))
        {
            report.Fail($"turn {index + 1}: fare answer contains an unsolicited follow-up");
        }

        foreach (var phrase in scenario.AnswerMentions)
        {
            if (scenario.ExpectedTools.Length > 0 && !expectedToolsSucceeded)
            {
                report.BlockedCheck(
                    $"turn {index + 1}: answer mention '{phrase}' blocked; expected action did not succeed");
            }
            else if (!phrase.Split('|').Any(option => answer.Contains(option, StringComparison.OrdinalIgnoreCase)))
            {
                report.Fail($"turn {index + 1}: answer does not mention '{phrase}'");
            }
        }

        if (answer.Contains("+00:00", StringComparison.Ordinal) || answer.Contains("UTC", StringComparison.Ordinal))
        {
            report.Fail($"turn {index + 1}: answer states a UTC offset to the guest");
        }

        // Ids are opaque handles; reading them aloud is useless to a voice guest.
        if (System.Text.RegularExpressions.Regex.IsMatch(answer, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-"))
        {
            report.Fail($"turn {index + 1}: answer reads out an id");
        }

        if (answer.Contains("couldn't safely fit", StringComparison.OrdinalIgnoreCase))
        {
            report.Fail($"turn {index + 1}: context budget refusal");
        }

        if (answer.Contains("within the allowed steps", StringComparison.OrdinalIgnoreCase))
        {
            report.Fail($"turn {index + 1}: per-turn tool cap reached");
        }

        if (answer.Contains("simulat", StringComparison.OrdinalIgnoreCase))
        {
            report.Fail($"turn {index + 1}: answer describes a ride as simulated");
        }

        if (answer.Contains("United States of America", StringComparison.OrdinalIgnoreCase))
        {
            report.Fail($"turn {index + 1}: answer includes the full country name in a place");
        }

        if (scenario.ExpectedTools.Any(tool => tool.Contains("GetWeather", StringComparison.Ordinal)
                || tool.Contains("GetRoute", StringComparison.Ordinal))
            && !expectedToolsSucceeded)
        {
            report.BlockedCheck($"turn {index + 1}: complete weather/route sentence check blocked; expected tool did not succeed");
        }
        else if (scenario.ExpectedTools.Any(tool => tool.Contains("GetWeather", StringComparison.Ordinal)
                || tool.Contains("GetRoute", StringComparison.Ordinal))
            && !System.Text.RegularExpressions.Regex.IsMatch(answer.Trim(), @"[.!?]$"))
        {
            report.Fail($"turn {index + 1}: weather or route answer is not a complete sentence");
        }

        if (scenario.ProvidesActionPrerequisite is { } providedPrerequisite)
        {
            var succeeded = blockedReason is null && expectedToolsSucceeded;
            var reason = blockedReason is not null
                ? $"turn {index + 1} was blocked because {blockedReason}"
                : $"turn {index + 1} did not successfully perform expected action(s): {string.Join(", ", scenario.ExpectedTools)}";
            prerequisiteTracker.Complete(providedPrerequisite, succeeded, reason);
        }

        report.CompleteScenario(failuresAtScenarioStart);
    }

    stage = "ending the temporary stay";
    await http.PostAsync("/api/concierge/end", null);
    try
    {
        await temporalClient.GetWorkflowHandle(workflowId).TerminateAsync("Step 6 live-check cleanup");
    }
    catch
    {
        Console.Error.WriteLine("WARN  Could not terminate the temporary stay workflow.");
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL  Step 6 live check during {stage}: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

Console.WriteLine();
Console.WriteLine(report.Summary);
if (report.Failed > 0)
{
    foreach (var failure in report.Failures)
    {
        Console.Error.WriteLine($"FAIL  {failure}");
    }

    return 1;
}

Console.WriteLine("Step 6 live check passed.");
return 0;

bool ToolCallSucceeded(ToolCall call, Scenario scenario) =>
    !call.Failed
    && (scenario.AllowRideProposalFailure && call.Name == "ProposeRide"
        || !call.Result.Contains("NeedsClarification", StringComparison.Ordinal)
            && !call.Result.Contains("Unavailable\"", StringComparison.Ordinal));

int RunReportSelfTests()
{
    var passed = 0;
    var failed = 0;

    void Test(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception exception)
        {
            failed++;
            Console.Error.WriteLine($"FAIL  {name}: {exception.Message}");
        }
    }

    Test("root action-selection failure is counted once", () =>
    {
        var report = new CheckReport();
        var failureCount = report.FailureCount;
        report.Fail("turn 29: primary action-selection failure ProposeRide; called [GetRideHistory]");
        report.CompleteScenario(failureCount);
        Require(report.Failed == 1 && report.Failures.Count == 1, "expected one primary failure");
        Require(report.Summary == "Passed: 0, Failed: 1, Skipped: 0",
            "summary does not count the root action-selection failure once");
    });

    foreach (var acknowledgement in new[] { "Yes, please.", "Thanks." })
    {
        Test($"South Beach chain blocks downstream actions ({acknowledgement})", () =>
        {
            var chain = SouthBeachRepeatBookingScenarios(acknowledgement, "south-beach-selftest");
            Require(chain[0].Message == "Okay. Um, could you book me a ride to South Beach?"
                && chain[0].ExpectedTools.SequenceEqual(["ProposeRide"])
                && chain[0].AnswerMentions.SequenceEqual(["No ride has been booked"]),
                "request step does not encode the required new proposal");
            Require(chain[1].Message == "Book it for right now."
                && chain[1].ExpectedTools.SequenceEqual(["ConfirmRide"])
                && chain[1].RequiresActionPrerequisite == chain[0].ProvidesActionPrerequisite,
                "confirmation step is not dependent on the proposal");
            Require(chain[2].Message == acknowledgement
                && chain[2].ForbidRideExecution
                && chain[2].ForbidRideProposal
                && chain[2].RequiresActionPrerequisite == chain[1].ProvidesActionPrerequisite,
                "acknowledgement step is not safely dependent on confirmation");

            var dependencies = new ActionPrerequisiteTracker();
            var report = new CheckReport();
            var failureCount = report.FailureCount;
            report.Fail("turn 29: primary action-selection failure");
            report.CompleteScenario(failureCount);
            dependencies.Complete(
                chain[0].ProvidesActionPrerequisite!,
                false,
                "turn 29 missed ProposeRide; called GetRideHistory");

            var proposalReason = dependencies.GetBlockReason(chain[1].RequiresActionPrerequisite!)!;
            report.SkipScenario($"turn 30 not sent: ConfirmRide blocked; {proposalReason}", emit: false);
            dependencies.Complete(
                chain[1].ProvidesActionPrerequisite!,
                false,
                $"turn 30 was not sent because {proposalReason}");

            var confirmationReason = dependencies.GetBlockReason(chain[2].RequiresActionPrerequisite!)!;
            report.SkipScenario($"turn 31 not sent: acknowledgement checks blocked; {confirmationReason}", emit: false);
            Require(report.Passed == 0 && report.Failed == 1 && report.Skipped == 2,
                "expected one failed root scenario and two skipped dependent scenarios");
            Require(report.BlockedScenarios[0].Contains("GetRideHistory", StringComparison.Ordinal)
                && report.BlockedScenarios[1].Contains("turn 29", StringComparison.Ordinal),
                "blocked output does not explain the propagated root failure");
            Require(report.Summary == "Passed: 0, Failed: 1, Skipped: 2",
                "summary does not report one root failure and two skipped turns");
        });
    }

    Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: 0");
    return failed == 0 ? 0 : 1;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static Scenario[] SouthBeachRepeatBookingScenarios(string acknowledgement, string prerequisitePrefix) =>
[
    new("Okay. Um, could you book me a ride to South Beach?", ["ProposeRide"], ["No ride has been booked"],
        ProvidesActionPrerequisite: $"{prerequisitePrefix}-proposal"),
    new("Book it for right now.", ["ConfirmRide"], ["all set|booked|pickup"],
        RequiresActionPrerequisite: $"{prerequisitePrefix}-proposal",
        ProvidesActionPrerequisite: $"{prerequisitePrefix}-confirmation"),
    new(acknowledgement, [], [], ForbidRideExecution: true, ForbidRideProposal: true,
        RequiresActionPrerequisite: $"{prerequisitePrefix}-confirmation"),
];

static async Task<List<ToolCall>> ReadNewToolCallsAsync(
    ITemporalClient client,
    string workflowId,
    HashSet<long> seenScheduled)
{
    var history = await client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
    var outcomes = new Dictionary<long, (string Result, bool Failed)>();
    foreach (var historyEvent in history.Events)
    {
        if (historyEvent.EventType == EventType.ActivityTaskCompleted)
        {
            var attributes = historyEvent.ActivityTaskCompletedEventAttributes;
            outcomes[attributes.ScheduledEventId] = (
                attributes.Result?.Payloads_.Count > 0 ? attributes.Result.Payloads_[0].Data.ToStringUtf8() : string.Empty,
                false);
        }
        else if (historyEvent.EventType == EventType.ActivityTaskFailed)
        {
            var attributes = historyEvent.ActivityTaskFailedEventAttributes;
            outcomes[attributes.ScheduledEventId] = (attributes.Failure?.Message ?? "failed", true);
        }
    }

    var calls = new List<ToolCall>();
    foreach (var historyEvent in history.Events.Where(item => item.EventType == EventType.ActivityTaskScheduled))
    {
        var attributes = historyEvent.ActivityTaskScheduledEventAttributes;
        if (attributes.ActivityType.Name != "TemporalCommunity.Extensions.AI.InvokeFunction"
            || !seenScheduled.Add(historyEvent.EventId))
        {
            continue;
        }

        using var input = JsonDocument.Parse(attributes.Input.Payloads_[0].Data.ToStringUtf8());
        var name = input.RootElement.GetProperty("functionName").GetString()!;
        var arguments = input.RootElement.TryGetProperty("arguments", out var args)
            ? StripTrusted(args)
            : string.Empty;
        var (result, failed) = outcomes.GetValueOrDefault(historyEvent.EventId, ("<no result>", true));
        calls.Add(new ToolCall(name, arguments, result, failed));
    }

    return calls;
}

static async Task<HttpResponseMessage> PostTurnWithTransientRetryAsync(
    HttpClient http,
    ConciergeTurnRequest request)
{
    for (var attempt = 0; ; attempt++)
    {
        var response = await http.PostAsJsonAsync("/api/concierge/turns", request);
        if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable || attempt == 1)
        {
            return response;
        }

        // The stable turn ID makes a retry safe if the server accepted the first request but
        // could not return its response. Do not retry persistent or non-503 failures.
        response.Dispose();
        Console.WriteLine("RETRY  Concierge returned HTTP 503; retrying the same idempotent turn once.");
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}

// Trusted identity values are injected by the workflow, not the model; keep them out of console output.
static string StripTrusted(JsonElement arguments)
{
    var visible = arguments.ValueKind == JsonValueKind.Object
        ? arguments.EnumerateObject()
            .Where(property => !property.Name.StartsWith("__", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => property.Value)
        : [];
    return JsonSerializer.Serialize(visible);
}

static string Clip(string value, int length) =>
    value.Length <= length ? value : value[..length] + "…";

static async Task SeedGuestAsync(string connectionString, Guid stayId, string familyName, string roomNumber)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    // One transaction so a room-overlap retry does not leave an orphan guest row.
    await using var transaction = await connection.BeginTransactionAsync();
    var guestId = Guid.NewGuid();
    // The schema requires stay boundaries at hotel-local midnight; mirror the seeded fixtures.
    var hotel = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, hotel).DateTime);
    DateTimeOffset Midnight(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), hotel));
    await using (var command = new NpgsqlCommand(
        "INSERT INTO guests (guest_id, given_name, family_name) VALUES ($1, 'Live', $2);", connection, transaction))
    {
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(familyName);
        await command.ExecuteNonQueryAsync();
    }

    await using (var command = new NpgsqlCommand(
        """
        INSERT INTO stays (
            stay_id, external_stay_key, guest_id, room_number,
            check_in_utc, check_out_utc, eligibility_ends_at_utc)
        VALUES ($1, $2, $3, $4, $5, $6, $7);
        """, connection, transaction))
    {
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue($"livecheck-{stayId:N}");
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(roomNumber);
        command.Parameters.AddWithValue(Midnight(today));
        command.Parameters.AddWithValue(Midnight(today.AddDays(1)));
        command.Parameters.AddWithValue(Midnight(today.AddDays(2)));
        await command.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();
}

static async Task<AspireRuntime> ReadRuntimeAsync(string repoRoot)
{
    var appHost = Path.Combine(repoRoot, "src", "Concierge.AppHost", "Concierge.AppHost.csproj");
    var startInfo = new ProcessStartInfo("aspire")
    {
        WorkingDirectory = repoRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in new[]
    {
        "describe", "--apphost", appHost, "--format", "Json", "--non-interactive", "--nologo",
    })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the Aspire CLI.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var json = await outputTask;
    _ = await errorTask;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Aspire describe returned exit code {process.ExitCode}; start the AppHost with 'aspire start' first.");
    }
    if (string.IsNullOrWhiteSpace(json))
    {
        throw new InvalidOperationException(
            "Aspire describe returned no resource metadata; start the AppHost with 'aspire start' first.");
    }

    using var document = JsonDocument.Parse(json);
    var resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();
    var workflowServer = Resource(resources, "workflow-server");
    var environment = workflowServer.GetProperty("environment");
    var web = Resource(resources, "web");
    var webUrl = web.GetProperty("urls").EnumerateArray()
        .Single(endpoint => endpoint.GetProperty("name").GetString() == "https")
        .GetProperty("url")
        .GetString()
        ?? throw new InvalidOperationException("The Web HTTPS endpoint is unavailable.");

    return new AspireRuntime(
        RequiredValue(environment, "ConnectionStrings__concierge"),
        RequiredValue(environment, "TEMPORAL_ADDRESS"),
        RequiredValue(environment, "TEMPORAL_NAMESPACE"),
        webUrl);
}

static JsonElement Resource(JsonElement[] resources, string name) =>
    resources.SingleOrDefault(resource =>
        resource.GetProperty("name").GetString() is { } resourceName
        && (resourceName == name || resourceName.StartsWith(name + "-", StringComparison.Ordinal)
            && !resourceName[(name.Length + 1)..].Contains('-')))
    is { ValueKind: JsonValueKind.Object } found
        ? found
        : throw new InvalidOperationException($"Aspire resource '{name}' is not running.");

static string RequiredValue(JsonElement element, string name)
{
    if (!element.TryGetProperty(name, out var property)
        || property.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(property.GetString()))
    {
        throw new InvalidOperationException($"Aspire resource setting '{name}' is unavailable.");
    }

    return property.GetString()!;
}

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

sealed record AspireRuntime(
    string ConciergeConnectionString,
    string TemporalAddress,
    string TemporalNamespace,
    string WebUrl);

sealed record Scenario(
    string Message,
    string[] ExpectedTools,
    string[] AnswerMentions,
    bool ForbidRideExecution = false,
    bool ForbidRideProposal = false,
    bool AllowRideProposalFailure = false,
    string? RequiresActionPrerequisite = null,
    string? ProvidesActionPrerequisite = null);

sealed record ToolCall(string Name, string Arguments, string Result, bool Failed);

sealed class ActionPrerequisiteTracker
{
    private readonly Dictionary<string, string?> outcomes = new(StringComparer.Ordinal);

    public string? GetBlockReason(string prerequisite) =>
        outcomes.TryGetValue(prerequisite, out var reason)
            ? reason
            : $"required prerequisite '{prerequisite}' was not evaluated";

    public void Complete(string prerequisite, bool succeeded, string failureReason) =>
        outcomes[prerequisite] = succeeded ? null : failureReason;
}

sealed class CheckReport
{
    private readonly List<string> failures = [];
    private readonly List<string> blockedScenarios = [];

    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int FailureCount => failures.Count;
    public IReadOnlyList<string> Failures => failures;
    public IReadOnlyList<string> BlockedScenarios => blockedScenarios;
    public string Summary => $"Passed: {Passed}, Failed: {Failed}, Skipped: {Skipped}";

    public void CompleteScenario(int failuresAtStart)
    {
        if (failures.Count == failuresAtStart)
        {
            Passed++;
        }
        else
        {
            Failed++;
        }
    }

    public void Fail(string message) => failures.Add(message);

    public void SkipScenario(string message, bool emit = true)
    {
        Skipped++;
        blockedScenarios.Add(message);
        if (emit)
        {
            Console.WriteLine($"BLOCKED  {message}");
        }
    }

    public void BlockedCheck(string message) => Console.WriteLine($"BLOCKED  {message}");
}
