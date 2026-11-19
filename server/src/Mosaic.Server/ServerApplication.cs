using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Mosaic.Server.Admin;
using Mosaic.Server.Auth;
using Mosaic.Server.Backups;
using Mosaic.Server.Data;
using Mosaic.Server.Emailing;
using Mosaic.Server.Health;
using Mosaic.Server.Http;
using Mosaic.Server.RateLimiting;
using Mosaic.Server.Security;
using Mosaic.Server.Sync;

namespace Mosaic.Server;

public static class ServerApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Contains("--healthcheck", StringComparer.Ordinal))
        {
            return await HealthProbe.RunAsync().ConfigureAwait(false);
        }

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            var options = ServerOptions.FromConfiguration(configuration);
            var app = await CreateAsync(args, options).ConfigureAwait(false);
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            // Startup errors are fatal and never include secret values.
            Console.Error.WriteLine(
                "mosaic-server failed to start: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    public static async Task<WebApplication> CreateAsync(string[] args, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);

        options.Normalize();
        options.Validate();
        Directory.CreateDirectory(options.DataDirectory);
        options.MasterKey ??= MasterKeyStore.Resolve(options);
        options.DatabasePath ??= Path.Combine(options.DataDirectory, "mosaic-server.sqlite3");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes);
        // Make minimal-API body binding failures throw so the JSON error envelope is used.
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(
            routeHandler => routeHandler.ThrowOnBadRequest = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        if (options.LogProvider is not null)
        {
            builder.Logging.AddProvider(options.LogProvider);
        }

        var database = new Database(options.DatabasePath);
        var timeProvider = options.TimeProvider ?? TimeProvider.System;

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(new SecretProtector(options.MasterKey));
        builder.Services.AddSingleton(new SecretHasher(options.MasterKey));
        builder.Services.AddSingleton<WindowLimiter>();
        builder.Services.AddSingleton<EmailSettingsStore>();
        builder.Services.AddSingleton<SessionService>();
        builder.Services.AddSingleton<EmailCodeService>();
        builder.Services.AddSingleton<DeviceFlowService>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<SyncService>();
        builder.Services.AddSingleton<IEmailSender>(services => options.EmailSenderOverride
            ?? new SmtpEmailSender(services.GetRequiredService<ILogger<SmtpEmailSender>>()));

        if (options.TrustedProxies.Length > 0)
        {
            var proxies = options.TrustedProxies.Select(IPAddress.Parse).ToArray();
            builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var proxy in proxies)
                {
                    forwarded.KnownProxies.Add(proxy);
                }
            });
        }

        var app = builder.Build();
        if (options.TrustedProxies.Length > 0)
        {
            app.UseForwardedHeaders();
        }

        await new DatabaseMigrator(database, timeProvider).ApplyAsync().ConfigureAwait(false);

        app.UseMosaicPipeline();
        app.MapAuthEndpoints();
        app.MapDeviceEndpoints();
        app.MapBackupEndpoints();
        app.MapSyncEndpoints();
        app.MapAdminEndpoints();
        app.MapHealthEndpoints();
        return app;
    }
}
