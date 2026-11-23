using Microsoft.EntityFrameworkCore;

namespace Retail.Data;

internal static class RetailServices
{
    public static void AddRetailDb(this IServiceCollection services) =>
        services.AddDbContext<RetailDb>((provider, options) =>
        {
            var config = provider.GetRequiredService<IConfiguration>();
            var connection = config.GetConnectionString("Retail")
                ?? throw new InvalidOperationException("Configure ConnectionStrings:Retail.");
            options.UseNpgsql(connection);
        });
}
