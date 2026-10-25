using System.ComponentModel;
using System.Net;
using System.ComponentModel.DataAnnotations;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;

namespace Concierge.WorkflowServer.AI;

/// <summary>Tool names, model declarations, and durable invocation stubs for stay turns.</summary>
internal static class ConciergeToolDefinitions
{
    public const string ModelOptionKey = "concierge.actions.tools";
    public const string TrustedGuestIdKey = "__concierge_verified_guest_id";
    public const string TrustedStayIdKey = "__concierge_verified_stay_id";
    public const string TrustedCheckoutDateKey = "__concierge_verified_checkout_date";
    public const string TrustedProposalKey = "__concierge_pending_ride_proposal";
    public const string TrustedTurnStartedUtcKey = "__concierge_turn_started_utc";

    public const string GetWeather = "GetWeather";
    public const string GetRoute = "GetRoute";
    public const string FindNearbyPlaces = "FindNearbyPlaces";
    public const string GetPlaceOpeningHours = "GetPlaceOpeningHours";
    public const string ProposeRide = "ProposeRide";
    public const string ConfirmRide = "ConfirmRide";
    public const string GetRideStatus = "GetRideStatus";
    public const string GetRideHistory = "GetRideHistory";
    public const string ProposeRideCancellation = "ProposeRideCancellation";
    public const string ConfirmRideCancellation = "ConfirmRideCancellation";

    public static IReadOnlyList<AIFunction> CreateModelTools(bool includeEndChat)
    {
        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (Func<string?, string, string>)WeatherStub,
                Options(GetWeather, "Forecast conditions, high/low F, wind, and rain chance.")),
            AIFunctionFactory.Create(
                (Func<string[], string>)RouteStub,
                Options(GetRoute, "Driving distance, travel time, and directions. No live traffic.")),
            AIFunctionFactory.Create(
                (Func<string, string>)NearbyPlacesStub,
                Options(FindNearbyPlaces, "Nearest places of one type to Hotel ZGY.")),
            AIFunctionFactory.Create(
                (Func<string, string>)PlaceOpeningHoursStub,
                Options(GetPlaceOpeningHours, "Opening hours for one named place near Hotel ZGY.")),
            AIFunctionFactory.Create(
                (Func<string, bool, string?, string?, string>)ProposeRideStub,
                Options(ProposeRide, "Prepares a ride proposal for the guest to confirm; does not book.")),
            AIFunctionFactory.Create(
                (Func<string>)ConfirmRideStub,
                Options(ConfirmRide, "Books the pending ride offer the guest accepted.")),
            AIFunctionFactory.Create(
                (Func<string, string>)RideStatusStub,
                Options(GetRideStatus, "Status of one of the guest's rides.")),
            AIFunctionFactory.Create(
                (Func<string, string?, string>)RideHistoryStub,
                Options(GetRideHistory, "Lists the guest's past or upcoming rides.")),
            AIFunctionFactory.Create(
                (Func<string, string>)ProposeRideCancellationStub,
                Options(ProposeRideCancellation, "Stages an offer to cancel one ride; does not cancel.")),
            AIFunctionFactory.Create(
                (Func<string>)ConfirmRideCancellationStub,
                Options(ConfirmRideCancellation, "Cancels the ride in the accepted cancellation offer.")),
        };

        if (includeEndChat)
        {
            tools.Add(AIFunctionFactory.Create(
                (Func<Task>)(static () => Task.CompletedTask),
                new AIFunctionFactoryOptions
                {
                    Name = ConciergeEndChat.FunctionName,
                    Description = "End chat on request.",
                }));
        }

        return tools;
    }

    /// <summary>
    /// Produces worker-side implementations. Their declaration is not forwarded to the model:
    /// model schemas are independently materialized in the model activity from the serializable
    /// option marker. AIFunctionArguments and IServiceProvider are MEAI-injected parameters and
    /// therefore stay out of the model-visible JSON schema.
    /// </summary>
    public static IReadOnlyList<AIFunction> CreateActivityTools() =>
    [
        AIFunctionFactory.Create(
            (Func<string?, string, AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)WeatherActivity,
            Options(GetWeather, "Gets forecast data.")),
        AIFunctionFactory.Create(
            (Func<string[], AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)RouteActivity,
            Options(GetRoute, "Gets route data.")),
        AIFunctionFactory.Create(
            (Func<string, AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)NearbyPlacesActivity,
            Options(FindNearbyPlaces, "Gets nearby place data.")),
        AIFunctionFactory.Create(
            (Func<string, AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)PlaceOpeningHoursActivity,
            Options(GetPlaceOpeningHours, "Gets opening hours for one named place.")),
        AIFunctionFactory.Create(
            (Func<string, bool, AIFunctionArguments, IServiceProvider, CancellationToken, string?, string?, Task<string>>)ProposeRideActivity,
            Options(ProposeRide, "Builds a ride proposal without booking.")),
        AIFunctionFactory.Create(
            (Func<string, AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)ConfirmRideActivity,
            Options(ConfirmRide, "Books a confirmed proposal.")),
        AIFunctionFactory.Create(
            (Func<string, AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)RideStatusActivity,
            Options(GetRideStatus, "Reads a scoped ride.")),
        AIFunctionFactory.Create(
            (Func<string, AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)RideHistoryActivity,
            Options(GetRideHistory, "Lists scoped rides.")),
        AIFunctionFactory.Create(
            (Func<string, AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)ProposeRideCancellationActivity,
            Options(ProposeRideCancellation, "Reads a scoped ride and stages a cancellation offer without cancelling.")),
        AIFunctionFactory.Create(
            (Func<string, AIFunctionArguments, IServiceProvider, CancellationToken, Task<string>>)ConfirmRideCancellationActivity,
            Options(ConfirmRideCancellation, "Cancels the workflow-owned offered ride.")),
    ];

    public static AIFunction CreateDurableInvoker(string name, TimeSpan activityTimeout) =>
        AIFunctionFactory.Create(
            (Func<AIFunctionArguments, string?>)(static _ => null),
            Options(name, "Validated concierge action."))
        .AsDurable(new DurableExecutionOptions
        {
            ActivityTimeout = activityTimeout,
            RetryPolicy = RetryPolicyFor(name),
        });

    // Weather is read-only, so transient provider failures retry. Ride actions stay single-attempt
    // so a side effect is never repeated.
    internal static RetryPolicy RetryPolicyFor(string name) =>
        StringComparer.Ordinal.Equals(name, GetWeather)
            ? new RetryPolicy
            {
                MaximumAttempts = 3,
                InitialInterval = TimeSpan.FromSeconds(1),
                BackoffCoefficient = 2,
                MaximumInterval = TimeSpan.FromSeconds(2),
            }
            : new RetryPolicy { MaximumAttempts = 1 };

    internal const string TransientProviderFailureType = "TransientProviderFailure";

    public static AIFunction FindModelTool(string name, bool includeEndChat) =>
        CreateModelTools(includeEndChat).SingleOrDefault(tool =>
            StringComparer.Ordinal.Equals(tool.Name, name))
        ?? throw new InvalidOperationException("The requested concierge tool is not registered.");

    public static int EstimateToolDefinitionTokens(bool includeEndChat)
    {
        var bytes = CreateModelTools(includeEndChat).Sum(tool =>
            System.Text.Encoding.UTF8.GetByteCount(tool.Name ?? string.Empty)
            + System.Text.Encoding.UTF8.GetByteCount(tool.Description ?? string.Empty)
            + System.Text.Encoding.UTF8.GetByteCount(tool.JsonSchema.ToString())
            + 32);
        // The existing context estimator treats one UTF-8 byte as one token; retain that
        // conservative policy for schemas instead of relying on provider-side token counts.
        return bytes;
    }

    private static AIFunctionFactoryOptions Options(string name, string description) => new()
    {
        Name = name,
        Description = description,
    };

    private static string WeatherStub(
        [Description("Place name; omit for Hotel ZGY.")] string? place = null,
        [Description("today, tonight, tomorrow, or a weekday name (its next occurrence, today included)")]
        string period = "today") => string.Empty;
    private static string RouteStub(
        [Description("[destination] from Hotel ZGY, or [origin, destination].")] string[] places) => string.Empty;
    private static string NearbyPlacesStub(
        [AllowedValues(
            "pharmacy", "grocery", "convenience", "restaurant", "cafe", "bar", "fast_food", "atm", "bank",
            "hospital", "clinic", "gas_station", "mall", "museum", "cinema", "park", "beach", "attraction",
            "post_office")]
        string category) => string.Empty;
    private static string PlaceOpeningHoursStub(
        [Description("The specific place name, such as Wayan Miami.")] string placeName) => string.Empty;
    private static string ProposeRideStub(
        string dropoff,
        [Description("true for now; false to schedule with pickupAt.")] bool immediate,
        [Description("Omit for Hotel ZGY.")] string? pickup = null,
        [Description("ISO 8601 with UTC offset; configured minimum notice.")] string? pickupAt = null) =>
        string.Empty;
    // The workflow binds confirmation to its stored pending proposal; models do not reliably
    // carry opaque ids across turns because working history keeps only spoken text.
    private static string ConfirmRideStub() => string.Empty;
    private static string RideStatusStub(string rideId) => string.Empty;
    private static string RideHistoryStub(
        [Description("past or upcoming")] string when,
        [Description("show (default) or select")] string? purpose = null) =>
        string.Empty;
    private static string ProposeRideCancellationStub(string rideId) => string.Empty;
    private static string ConfirmRideCancellationStub() => string.Empty;

    private static Task<string> WeatherActivity(
        string? place,
        string period,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            GetWeather,
            services,
            cancellationToken,
            (backend, token) => backend.GetWeatherAsync(place, period, TurnStartedUtc(arguments), token));

    private static DateTimeOffset? TurnStartedUtc(AIFunctionArguments arguments) =>
        arguments.TryGetValue(TrustedTurnStartedUtcKey, out var raw)
        && (raw is string text ? text : raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element ? element.GetString() : null) is { } value
        && DateTimeOffset.TryParseExact(value, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static Task<string> RouteActivity(
        string[] places,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            GetRoute,
            services,
            cancellationToken,
            (backend, token) => backend.GetRouteAsync(places, token));

    private static Task<string> NearbyPlacesActivity(
        string category,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            FindNearbyPlaces,
            services,
            cancellationToken,
            (backend, token) => backend.FindNearbyPlacesAsync(category, token));

    private static Task<string> PlaceOpeningHoursActivity(
        string placeName,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            GetPlaceOpeningHours,
            services,
            cancellationToken,
            (backend, token) => backend.GetPlaceOpeningHoursAsync(placeName, token));

    private static Task<string> ProposeRideActivity(
        string dropoff,
        bool immediate,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken,
        string? pickup = null,
        string? pickupAt = null) =>
        RunActivityAsync(
            ProposeRide,
            services,
            cancellationToken,
            (backend, token) => backend.ProposeRideAsync(pickup, dropoff, immediate, pickupAt, token));

    private static Task<string> ConfirmRideActivity(
        string proposalId,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            ConfirmRide,
            services,
            cancellationToken,
            (backend, token) => backend.ConfirmRideAsync(proposalId, arguments, token));

    private static Task<string> RideStatusActivity(
        string rideId,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            GetRideStatus,
            services,
            cancellationToken,
            (backend, token) => backend.GetRideStatusAsync(rideId, arguments, token));

    private static Task<string> RideHistoryActivity(
        string when,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            GetRideHistory,
            services,
            cancellationToken,
            (backend, token) => backend.GetRideHistoryAsync(when, arguments, token));

    private static Task<string> ProposeRideCancellationActivity(
        string rideId,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            ProposeRideCancellation,
            services,
            cancellationToken,
            (backend, token) => backend.GetRideStatusAsync(rideId, arguments, token));

    private static Task<string> ConfirmRideCancellationActivity(
        string rideId,
        AIFunctionArguments arguments,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        RunActivityAsync(
            ConfirmRideCancellation,
            services,
            cancellationToken,
            (backend, token) => backend.CancelRideAsync(rideId, arguments, token));

    // Leaves time to return the curated result or failure before StartToClose expires. Provisional.
    internal static readonly TimeSpan ActivityCleanupMargin = TimeSpan.FromMilliseconds(500);

    private static async Task<string> RunActivityAsync(
        string toolName,
        IServiceProvider services,
        CancellationToken cancellationToken,
        Func<IConciergeActionToolBackend, CancellationToken, Task<string>> operation)
    {
        // The workflow shortens StartToClose near the turn deadline; honor that shorter allowance
        // locally so provider and ride-service calls stop instead of outliving the attempt.
        using var allowance = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (ActivityExecutionContext.HasCurrent
            && RemainingActivityAllowance(
                ActivityExecutionContext.Current.Info.StartedTime,
                ActivityExecutionContext.Current.Info.StartToCloseTimeout,
                DateTime.UtcNow) is { } remaining)
        {
            allowance.CancelAfter(remaining);
        }

        try
        {
            return await operation(
                services.GetRequiredService<IConciergeActionToolBackend>(),
                allowance.Token);
        }
        catch (Exception exception) when (!IsTimeout(exception))
        {
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ConciergeActionToolActivity")
                .LogError(exception, "Concierge tool activity {ToolName} failed.", toolName);

            if (RetryPolicyFor(toolName).MaximumAttempts > 1 && IsTransientProviderFailure(exception))
            {
                // Fail the attempt so Temporal's retry policy runs it again. The message is fixed
                // because provider exceptions can carry request details.
                throw new ApplicationFailureException(
                    $"{toolName} provider request failed transiently.",
                    errorType: TransientProviderFailureType);
            }

            // The workflow converts a null tool payload into its curated, guest-safe failure
            // reply. Keep exception details in activity logs, never in model context.
            return "null";
        }
    }

    internal static TimeSpan? RemainingActivityAllowance(
        DateTime startedUtc,
        TimeSpan? startToClose,
        DateTime utcNow)
    {
        if (startToClose is not { } timeout)
        {
            return null;
        }

        var remaining = startedUtc.ToUniversalTime() + timeout - ActivityCleanupMargin - utcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    // Network errors carry no status code; 408, 429, and 5xx responses may succeed on retry.
    internal static bool IsTransientProviderFailure(Exception exception) =>
        exception is HttpRequestException { StatusCode: null }
            or HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests }
            or HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError };

    private static bool IsTimeout(Exception exception) =>
        exception is OperationCanceledException
            or TimeoutException
            or Polly.Timeout.TimeoutRejectedException
        || exception.InnerException is { } inner && IsTimeout(inner);
}
