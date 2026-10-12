using Concierge.Web.Components;
using Concierge.Web.Endpoints;
using Concierge.Web.Services;
using Concierge.Contracts;
using TemporalCommunity.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var temporalOptions = TemporalWorkerConnection.Resolve();
builder.Services.AddTemporalClient(options =>
{
    TemporalWorkerConnection.ApplyTo(temporalOptions, options);
    options.DataConverter = DurableAIDataConverter.Instance;
});
builder.Services.AddOptions<ConciergePersistenceOptions>()
    .Bind(builder.Configuration.GetSection(ConciergePersistenceOptions.SectionName))
    .Validate(options => options.FlushIntervalSeconds is >= 1 and <= 600)
    .ValidateOnStart();
builder.Services.AddScoped<IConciergeService, ConciergeService>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapConciergeApi();
app.MapDefaultEndpoints();

app.Run();
