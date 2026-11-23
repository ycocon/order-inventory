using Retail.Catalog;
using Xunit;

namespace Retail.IntegrationTests;

public sealed class SkuTests
{
    [Fact]
    public void Letter_case_and_surrounding_spaces_identify_the_same_product()
    {
        Assert.Equal(Product.NormalizeSku("LAMP-1"), Product.NormalizeSku(" lamp-1 "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-LEADING")]
    [InlineData("A B")]
    public void Rejects_invalid_identifiers(string sku)
    {
        Assert.False(Product.IsValidSku(sku));
    }
}
