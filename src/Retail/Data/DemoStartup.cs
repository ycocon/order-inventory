using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Retail.Catalog;
using Retail.Inventory;

namespace Retail.Data;

internal static class DemoStartup
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<RetailDb>();
        await db.Database.MigrateAsync();
        // Serialize seeds across hosts sharing this database. Initialization preserves staff edits.
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(713804591)");
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var config = services.GetRequiredService<IConfiguration>();
        await EnsureAccountAsync("staff", "staff@demo.local", "Demo:StaffPassword");
        await EnsureAccountAsync("customer", "customer@demo.local", "Demo:CustomerPassword");

        if (!await db.Products.AnyAsync(p => p.Sku == "FIELD-NOTES"))
        {
            db.Products.Add(new Product { Sku = "FIELD-NOTES", Name = "Field notebook", UnitPriceMinor = 1200, Active = true });
            StockBalances.Open(db, "FIELD-NOTES");
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();

        async Task EnsureAccountAsync(string role, string email, string passwordKey)
        {
            if (!await roles.RoleExistsAsync(role))
                RequireSuccess(await roles.CreateAsync(new IdentityRole(role)));
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                var password = config[passwordKey];
                if (string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException($"Set {passwordKey} to initialize the demo account.");
                user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
                RequireSuccess(await users.CreateAsync(user, password));
            }
            if (!await users.IsInRoleAsync(user, role))
                RequireSuccess(await users.AddToRoleAsync(user, role));
        }
    }

    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Code)));
    }
}
