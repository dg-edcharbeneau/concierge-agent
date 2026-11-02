using Concierge.Web.Components;
using Concierge.Web.Endpoints;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Concierge.Contracts;
using TemporalCommunity.Extensions.AI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
var thinkPort = int.TryParse(builder.Configuration["THINK_PORT"], out var configuredThinkPort)
    && configuredThinkPort is > 0 and <= 65535
        ? configuredThinkPort
        : (int?)null;

if (thinkPort is not null)
{
    var existingUrls = builder.Configuration["ASPNETCORE_URLS"];
    string[] urls = string.IsNullOrWhiteSpace(existingUrls)
        ? ["https://localhost:5001"]
        : existingUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var endpointConfiguration = urls
        .Select((url, index) => new KeyValuePair<string, string?>(
            $"Kestrel:Endpoints:Web{index}:Url",
            url))
        .Append(new KeyValuePair<string, string?>(
            "Kestrel:Endpoints:Think:Url",
            $"https://localhost:{thinkPort}"));

    builder.Configuration.AddInMemoryCollection(endpointConfiguration);
}

var temporalOptions = TemporalWorkerConnection.Resolve();
builder.Services.AddTemporalClient(options =>
{
    TemporalWorkerConnection.ApplyTo(temporalOptions, options);
    options.DataConverter = DurableAIDataConverter.Instance;
    TemporalTelemetry.ApplyTo(options);
});
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource([.. TemporalTelemetry.ActivitySourceNames]))
    .WithMetrics(metrics => metrics.AddMeter(TemporalTelemetry.MeterName));

builder.Services.AddOptions<ConciergePersistenceOptions>()
    .Bind(builder.Configuration.GetSection(ConciergePersistenceOptions.SectionName))
    .Validate(
        options => options.FlushIntervalSeconds is >= 1 and <= 600,
        $"{ConciergePersistenceOptions.SectionName}:FlushIntervalSeconds must be between 1 and 600.")
    .Validate(
        options => options.FlushBatchSize is >= 1 and <= ConciergePersistenceOptions.MaxFlushBatchSize,
        $"{ConciergePersistenceOptions.SectionName}:FlushBatchSize must be between 1 and {ConciergePersistenceOptions.MaxFlushBatchSize}.")
    .ValidateOnStart();

builder.Services.AddOptions<DeepgramVoiceOptions>()
    .Bind(builder.Configuration.GetSection(DeepgramVoiceOptions.SectionName))
    .Validate(
        options => options.IdleTimeoutMinutes is >= 1 and <= 60,
        $"{DeepgramVoiceOptions.SectionName}:IdleTimeoutMinutes must be between 1 and 60.")
    .Validate(
        options => VoiceAgentSettings.IsValidSpeakSpeed(options.SpeakModel, options.Speed),
        $"{DeepgramVoiceOptions.SectionName}:Speed must match the selected TTS family "
        + "(Flux: 0.5–1.5 in 0.05 steps; Aura: 0.7–1.5).")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<VoiceSessionRegistry>();
builder.Services.AddScoped<IConciergeService, ConciergeService>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Keep Think outside UI error handling, status-page reexecution, and antiforgery.
app.MapWhen(
    context => thinkPort is not null && context.Connection.LocalPort == thinkPort.Value,
    branch => branch.Run(async context =>
    {
        if (!context.Request.Path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
            || !HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Run is terminal even when WebApplication's implicit routing already selected Razor.
        await ThinkEndpoint.HandleAsync(context);
    }));

app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next(context);
});

// Configure the UI request pipeline only after the listener boundary.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();
app.UseWebSockets();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapConciergeApi();
app.Map(VoiceRelayEndpoint.Path, VoiceRelayEndpoint.HandleAsync);
app.MapDefaultEndpoints();

app.Run();
