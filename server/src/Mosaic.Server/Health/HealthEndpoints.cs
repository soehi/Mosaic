using System.Reflection;
using Mosaic.Server.Data;
using Mosaic.Server.Http;

namespace Mosaic.Server.Health;

public static class HealthEndpoints
{
    public static string Version =>
        typeof(HealthEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(HealthEndpoints).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static void MapHealthEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mosaic.Server.Health");

        app.MapGet("/health", async (Database database, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            try
            {
                await database.ReadAsync(
                    async (connection, token) => await connection.ScalarInt64Async(
                        transaction: null,
                        "SELECT 1",
                        token).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(new
                {
                    status = "ok",
                    version = Version,
                    timeUtc = TimeFormat.Iso(timeProvider.GetUtcNow().ToUnixTimeSeconds()),
                });
            }
            catch (Exception exception)
            {
                logger.LogWarning("health.unavailable errorType={ErrorType}", exception.GetType().Name);
                return Results.Json(
                    new { status = "unavailable", version = Version },
                    statusCode: 503);
            }
        });
    }
}

public static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var url = Environment.GetEnvironmentVariable("MOSAIC_HEALTHCHECK_URL")
            ?? "http://127.0.0.1:8080/health";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
