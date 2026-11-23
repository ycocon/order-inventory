using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Retail.IntegrationTests;

public sealed class RetailFixture : IAsyncLifetime
{
    private PostgreSqlContainer? database;
    public WebApplicationFactory<Program> App { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;
    public string StaffPassword { get; } = $"Staff!7{Guid.NewGuid():N}";
    public string CustomerPassword { get; } = $"Customer!7{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        // CI uses Testcontainers. A local runner may supply a new, empty test database.
        var external = Environment.GetEnvironmentVariable("RETAIL_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(external))
        {
            database = new PostgreSqlBuilder("postgres:18").Build();
            await database.StartAsync();
            ConnectionString = database.GetConnectionString();
        }
        else
        {
            var settings = new NpgsqlConnectionStringBuilder(external);
            if (settings.Database?.StartsWith("retail_test_", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("RETAIL_TEST_POSTGRES needs a fresh database named retail_test_*.");

            await using var connection = new NpgsqlConnection(external);
            await connection.OpenAsync();
            await using var check = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public')", connection);
            if ((bool)(await check.ExecuteScalarAsync())!)
                throw new InvalidOperationException("The supplied retail_test_* database must be empty.");

            ConnectionString = external;
        }

        App = new TestApplication(ConnectionString, StaffPassword, CustomerPassword);
    }

    public HttpClient NewClient() => App.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    public async Task DisposeAsync()
    {
        if (App is not null)
            await App.DisposeAsync();
        if (database is not null)
            await database.DisposeAsync();
    }

    private sealed class TestApplication(string connection, string staffPassword, string customerPassword)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Retail", connection);
            builder.UseSetting("Demo:StaffPassword", staffPassword);
            builder.UseSetting("Demo:CustomerPassword", customerPassword);
        }
    }
}
