using Concierge.Contracts;
using Concierge.RideService;
using Concierge.RideService.Data;
using Concierge.RideService.Workflows;
using Temporalio.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("rides")
    ?? throw new InvalidOperationException(
        "Connection string 'rides' is required. Configure it through Aspire or secure deployment configuration.");
builder.Services.AddSingleton(Npgsql.NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<RideDatabaseMigrator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TimeZoneInfo.FindSystemTimeZoneById(
    builder.Configuration["Hotel:TimeZoneId"]
    ?? throw new InvalidOperationException("Hotel:TimeZoneId is required.")));
builder.Services.AddSingleton<RideRepository>();
builder.Services.AddSingleton<RideBookingService>();

var temporalOptions = TemporalWorkerConnection.Resolve();
builder.Services.AddTemporalClient(options =>
    TemporalWorkerConnection.ApplyTo(temporalOptions, options));
builder.Services.AddHostedTemporalWorker(RideWorkflowNames.TaskQueue)
    .AddWorkflow<RideBookingWorkflow>()
    .AddScopedActivities<RideBookingActivities>()
    .AddScopedActivities<RideStreamPublishingActivities>();

var app = builder.Build();
await app.Services.GetRequiredService<RideDatabaseMigrator>()
    .MigrateAsync(app.Lifetime.ApplicationStopping);
app.MapDefaultEndpoints();
app.MapRideEndpoints();
await app.RunAsync();
