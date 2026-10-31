using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Miguel.Agent;
using Miguel.Agent.Identity;
using Miguel.Agent.Inventory;
using Miguel.Agent.Outbox;
using Miguel.Agent.Security;
using Miguel.Agent.Transport;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "agentsettings.json"),
    optional: false,
    reloadOnChange: true);

if (args.Any(argument => string.Equals(argument, "run-once", StringComparison.OrdinalIgnoreCase)))
{
    builder.Configuration.AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            [$"{AgentOptions.SectionName}:RunOnce"] = "true"
        });
}

builder.Services
    .AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .Validate(
        options => options.HeartbeatInterval >= TimeSpan.FromSeconds(30),
        "HeartbeatInterval no puede ser menor que 30 segundos.")
    .Validate(
        options => options.InventoryInterval >= TimeSpan.FromMinutes(15),
        "InventoryInterval no puede ser menor que 15 minutos.")
    .Validate(
        options => options.SecurityPostureInterval >= TimeSpan.FromHours(1),
        "SecurityPostureInterval no puede ser menor que una hora.")
    .Validate(
        options => options.SecurityEventInterval >= TimeSpan.FromMinutes(1),
        "SecurityEventInterval no puede ser menor que un minuto.")
    .Validate(
        options => options.SecurityEventInitialLookback > TimeSpan.Zero &&
                   options.SecurityEventInitialLookback <= TimeSpan.FromHours(24),
        "SecurityEventInitialLookback debe estar entre un instante y 24 horas.")
    .Validate(
        options => options.SecurityEventBatchSize is >= 1 and <= 500,
        "SecurityEventBatchSize debe estar entre 1 y 500.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentPaths>();
builder.Services.AddSingleton<AgentIdentityStore>();
builder.Services.AddSingleton<SystemInventoryCollector>();
builder.Services.AddSingleton<InteractiveUserCollector>();
builder.Services.AddSingleton<SecurityPostureCollector>();
builder.Services.AddSingleton<WindowsSecurityEventCollector>();
builder.Services.AddSingleton<SecurityEventCursorStore>();
builder.Services.AddSingleton<OutboxStore>();
builder.Services.AddSingleton<BackupReportInbox>();
builder.Services.AddSingleton<BackupClientVersionProvider>();
builder.Services.AddSingleton(serviceProvider =>
{
    AgentOptions options = serviceProvider.GetRequiredService<IOptions<AgentOptions>>().Value;
    return new HttpClient
    {
        Timeout = options.HttpTimeout
    };
});
builder.Services.AddSingleton<AgentApiClient>();
builder.Services.AddSingleton<RemotePolicyClient>();

AgentPaths enrollmentPaths = new();
AgentIdentityStore enrollmentIdentityStore = new(enrollmentPaths);
AgentOptions enrollmentOptions = builder.Configuration
    .GetSection(AgentOptions.SectionName)
    .Get<AgentOptions>()
    ?? new AgentOptions();
BackupClientVersionProvider enrollmentBackupVersionProvider = new(
    Options.Create(enrollmentOptions));

if (args.Length > 0 &&
    string.Equals(args[0], "enroll", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await AgentEnrollment.RunInteractiveAsync(
        args,
        enrollmentIdentityStore,
        enrollmentOptions,
        enrollmentBackupVersionProvider,
        CancellationToken.None);
    return;
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MIGUEL Agent";
});
builder.Services.Configure<EventLogSettings>(settings =>
{
    settings.LogName = "Application";
    settings.SourceName = "MIGUEL Agent";
    settings.Filter = (category, level) =>
        level >= (
            category.StartsWith("Miguel.Agent", StringComparison.Ordinal)
                ? LogLevel.Information
                : LogLevel.Warning);
});
builder.Services.AddHostedService<AgentWorker>();

using IHost host = builder.Build();
await host.RunAsync();
