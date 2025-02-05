namespace OrleansTemplate.Server;

using System.Runtime.InteropServices;
#if ApplicationInsights
using Microsoft.ApplicationInsights.Extensibility;
#endif
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Statistics;
using OrleansTemplate.Abstractions.Constants;
using OrleansTemplate.Grains;
using OrleansTemplate.Server.Options;
#if Serilog
using Serilog;
using Serilog.Extensions.Hosting;
#endif

public class Program
{
    public static async Task<int> Main(string[] args)
    {
#if Serilog
        Log.Logger = CreateBootstrapLogger();
#endif
        IHost? host = null;

        try
        {
#if Serilog
            Log.Information("Initialising.");
#endif
            host = CreateHostBuilder(args).Build();

            host.LogApplicationStarted();
            await host.RunAsync().ConfigureAwait(false);
            host.LogApplicationStopped();

            return 0;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            host!.LogApplicationTerminatedUnexpectedly(exception);

            return 1;
        }
#if Serilog
        finally
        {
            Log.CloseAndFlush();
        }
#endif
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        new HostBuilder()
            .UseContentRoot(Directory.GetCurrentDirectory())
            .ConfigureHostConfiguration(
                configurationBuilder => configurationBuilder.AddCustomBootstrapConfiguration(args))
            .ConfigureAppConfiguration(
                (hostingContext, configurationBuilder) =>
                {
                    hostingContext.HostingEnvironment.ApplicationName = AssemblyInformation.Current.Product;
                    configurationBuilder.AddCustomConfiguration(hostingContext.HostingEnvironment, args);
                })
#if Serilog
            .UseSerilog(ConfigureReloadableLogger)
#endif
            .UseDefaultServiceProvider(
                (context, options) =>
                {
                    var isDevelopment = context.HostingEnvironment.IsDevelopment();
                    options.ValidateScopes = isDevelopment;
                    options.ValidateOnBuild = isDevelopment;
                })
            .UseOrleans(ConfigureSiloBuilder)
#if HealthCheck
            .ConfigureWebHost(ConfigureWebHostBuilder)
#endif
            .UseConsoleLifetime();

    private static void ConfigureSiloBuilder(
        Microsoft.Extensions.Hosting.HostBuilderContext context,
        ISiloBuilder siloBuilder) =>
        siloBuilder
            .ConfigureServices(
                (context, services) =>
                {
                    services.Configure<ApplicationOptions>(context.Configuration);
                    services.Configure<ClusterOptions>(context.Configuration.GetSection(nameof(ApplicationOptions.Cluster)));
                    services.Configure<StorageOptions>(context.Configuration.GetSection(nameof(ApplicationOptions.Storage)));
#if ApplicationInsights
                    services.Configure<ApplicationInsightsTelemetryConsumerOptions>(
                    context.Configuration.GetSection(nameof(ApplicationOptions.ApplicationInsights)));
#endif
                })
            .UseSiloUnobservedExceptionsHandler()
            .UseAzureStorageClustering(
                options => options.ConfigureTableServiceClient(GetStorageOptions(context.Configuration).ConnectionString))
            .ConfigureEndpoints(
                EndpointOptions.DEFAULT_SILO_PORT,
                EndpointOptions.DEFAULT_GATEWAY_PORT,
                listenOnAnyHostAddress: !context.HostingEnvironment.IsDevelopment())
            .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(HelloGrain).Assembly).WithReferences())
#if ApplicationInsights
            .AddApplicationInsightsTelemetryConsumer()
#endif
            .AddAzureTableGrainStorageAsDefault(
                options =>
                {
                    options.ConfigureTableServiceClient(GetStorageOptions(context.Configuration).ConnectionString);
                    options.ConfigureJsonSerializerSettings = ConfigureJsonSerializerSettings;
                    options.UseJson = true;
                })
            .UseAzureTableReminderService(
                options => options.ConfigureTableServiceClient(GetStorageOptions(context.Configuration).ConnectionString))
            .UseTransactions(withStatisticsReporter: true)
            .AddAzureTableTransactionalStateStorageAsDefault(
                options => options.ConfigureTableServiceClient(GetStorageOptions(context.Configuration).ConnectionString))
            .AddSimpleMessageStreamProvider(StreamProviderName.Default)
            .AddAzureTableGrainStorage(
                "PubSubStore",
                options =>
                {
                    options.ConfigureTableServiceClient(GetStorageOptions(context.Configuration).ConnectionString);
                    options.ConfigureJsonSerializerSettings = ConfigureJsonSerializerSettings;
                    options.UseJson = true;
                })
            .UseIf(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                x => x.UseLinuxEnvironmentStatistics())
            .UseIf(
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                x => x.UsePerfCounterEnvironmentStatistics())
            .UseDashboard();

#if HealthCheck
    private static void ConfigureWebHostBuilder(IWebHostBuilder webHostBuilder) =>
        webHostBuilder
            .UseKestrel(
                (builderContext, options) =>
                {
                    options.AddServerHeader = false;
                    options.Configure(
                        builderContext.Configuration.GetSection(nameof(ApplicationOptions.Kestrel)),
                        reloadOnChange: false);
                })
            .UseStartup<Startup>();

#endif
#if Serilog
    /// <summary>
    /// Creates a logger used during application initialisation.
    /// <see href="https://nblumhardt.com/2020/10/bootstrap-logger/"/>.
    /// </summary>
    /// <returns>A logger that can load a new configuration.</returns>
    private static ReloadableLogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.Debug()
            .CreateBootstrapLogger();

    /// <summary>
    /// Configures a logger used during the applications lifetime.
    /// <see href="https://nblumhardt.com/2020/10/bootstrap-logger/"/>.
    /// </summary>
    private static void ConfigureReloadableLogger(
        Microsoft.Extensions.Hosting.HostBuilderContext context,
        IServiceProvider services,
        LoggerConfiguration configuration) =>
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.WithProperty("Application", context.HostingEnvironment.ApplicationName)
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
#if ApplicationInsights
            .WriteTo.Conditional(
                x => context.HostingEnvironment.IsProduction(),
                x => x.ApplicationInsights(
                    services.GetRequiredService<TelemetryConfiguration>(),
                    TelemetryConverter.Traces))
#endif
            .WriteTo.Conditional(
                x => context.HostingEnvironment.IsDevelopment(),
                x => x.Console().WriteTo.Debug());

#endif

    private static void ConfigureJsonSerializerSettings(JsonSerializerSettings jsonSerializerSettings)
    {
        jsonSerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        jsonSerializerSettings.DateParseHandling = DateParseHandling.DateTimeOffset;
    }

    private static StorageOptions GetStorageOptions(IConfiguration configuration) =>
        configuration.GetSection(nameof(ApplicationOptions.Storage)).Get<StorageOptions>();
}
