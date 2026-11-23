using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Retail.Data;
using Retail.Inventory;

namespace Retail.Catalog;

public sealed record ProductInput(string? Sku, string? Name, long? UnitPriceMinor, bool? Active);
public sealed record ProductView
{
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public long UnitPriceMinor { get; init; }
    public bool Active { get; init; }
    public long Available { get; init; }
}

public static class CatalogEndpoints
{
    public static void MapCatalog(this RouteGroupBuilder api)
    {
        var catalog = api.MapGroup("/products").RequireAuthorization();
        catalog.MapGet("", async (RetailDb db, ClaimsPrincipal user, CancellationToken ct) =>
            await VisibleProducts(db, user).OrderBy(p => p.Sku).ToListAsync(ct));
        catalog.MapGet("/{sku}", async (string sku, RetailDb db, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var key = Product.NormalizeSku(sku);
            var product = await VisibleProducts(db, user).FirstOrDefaultAsync(p => p.Sku == key, ct);
            return product is null ? Results.NotFound() : Results.Ok(product);
        });
        catalog.MapPost("", CreateAsync).RequireAuthorization(policy => policy.RequireRole("staff"));
        catalog.MapPut("/{sku}", UpdateAsync).RequireAuthorization(policy => policy.RequireRole("staff"));
    }

    private static IQueryable<ProductView> VisibleProducts(RetailDb db, ClaimsPrincipal user)
    {
        var includeInactive = user.IsInRole("staff");
        return from p in db.Products.AsNoTracking()
               join stock in StockBalances.Available(db) on p.Sku equals stock.Sku
               where includeInactive || p.Active
               select new ProductView
               {
                   Sku = p.Sku, Name = p.Name, UnitPriceMinor = p.UnitPriceMinor,
                   Active = p.Active, Available = stock.Units
               };
    }

    private static async Task<IResult> CreateAsync(ProductInput input, RetailDb db, CancellationToken ct)
    {
        var sku = Product.NormalizeSku(input.Sku);
        var error = Validate(input, sku);
        if (error is not null) return Results.BadRequest(new { error });
        var product = new Product
        {
            Sku = sku, Name = input.Name!.Trim(), UnitPriceMinor = input.UnitPriceMinor!.Value, Active = input.Active!.Value
        };
        db.Products.Add(product);
        StockBalances.Open(db, sku);
        try
        {
            // One SaveChanges transaction creates both the SKU and its zero balance.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Conflict(new { error = "This SKU already exists." });
        }
        return Results.Created($"/api/products/{sku}", new ProductView { Sku = sku, Name = product.Name, UnitPriceMinor = product.UnitPriceMinor, Active = product.Active });
    }

    private static async Task<IResult> UpdateAsync(string sku, ProductInput input, RetailDb db, ClaimsPrincipal user, CancellationToken ct)
    {
        sku = Product.NormalizeSku(sku);
        if (sku != Product.NormalizeSku(input.Sku))
            return Results.BadRequest(new { error = "The SKU cannot change." });
        var error = Validate(input, sku);
        if (error is not null) return Results.BadRequest(new { error });
        var product = await db.Products.FindAsync([sku], ct);
        if (product is null) return Results.NotFound();
        product.Name = input.Name!.Trim();
        product.UnitPriceMinor = input.UnitPriceMinor!.Value;
        product.Active = input.Active!.Value;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await VisibleProducts(db, user).SingleAsync(p => p.Sku == sku, ct));
    }

    private static string? Validate(ProductInput input, string sku)
    {
        if (!Product.IsValidSku(sku)) return "Use a 1–48 character SKU with letters, numbers, dots, underscores, or dashes.";
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 160) return "Enter a product name up to 160 characters.";
        if (input.UnitPriceMinor is null or < 0) return "The price must be a nonnegative whole number of minor currency units.";
        if (input.Active is null) return "Choose whether the product is active.";
        return null;
    }
}
