using Concierge.Web.Components;
using Concierge.Web.Endpoints;
using Concierge.Web.Services;
using Concierge.Contracts;
using TemporalCommunity.Extensions.AI;

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
});

builder.Services.AddOptions<ConciergePersistenceOptions>()
    .Bind(builder.Configuration.GetSection(ConciergePersistenceOptions.SectionName))
    .Validate(
        options => options.FlushIntervalSeconds is >= 1 and <= 600,
        $"{ConciergePersistenceOptions.SectionName}:FlushIntervalSeconds must be between 1 and 600.")
    .Validate(
        options => options.FlushBatchSize is >= 1 and <= ConciergePersistenceOptions.MaxFlushBatchSize,
        $"{ConciergePersistenceOptions.SectionName}:FlushBatchSize must be between 1 and {ConciergePersistenceOptions.MaxFlushBatchSize}.")
    .ValidateOnStart();

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
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message = "The Think handler is not implemented.",
                type = "server_error",
                param = (string?)null,
                code = "not_implemented",
            },
        }, context.RequestAborted);
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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapConciergeApi();
app.MapDefaultEndpoints();

app.Run();
