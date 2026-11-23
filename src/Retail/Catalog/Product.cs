using System.Text.RegularExpressions;

namespace Retail.Catalog;

public sealed partial class Product
{
    public required string Sku { get; init; }
    public required string Name { get; set; }
    public long UnitPriceMinor { get; set; }
    public bool Active { get; set; }

    public static string NormalizeSku(string? value) => (value ?? "").Trim().ToUpperInvariant();

    [GeneratedRegex(@"\A[A-Z0-9][A-Z0-9._-]{0,47}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();

    public static bool IsValidSku(string value) => SkuPattern().IsMatch(value);
}
