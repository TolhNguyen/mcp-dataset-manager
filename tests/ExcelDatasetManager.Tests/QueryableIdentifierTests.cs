using ExcelDatasetManager.Api.Services.Connectors;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class QueryableIdentifierTests
{
    [Theory]
    [InlineData("orders", '"', "\"orders\"")]
    [InlineData("public.orders", '"', "\"public\".\"orders\"")]
    [InlineData("my_dataset.orders$2024", '"', "\"my_dataset\".\"orders$2024\"")]
    [InlineData("orders", '`', "`orders`")]
    [InlineData("mydb.orders", '`', "`mydb`.`orders`")]
    public void Quotes_each_dotted_segment(string queryableName, char quoteChar, string expected)
    {
        Assert.Equal(expected, QueryableIdentifier.TryQuote(queryableName, quoteChar));
    }

    [Theory]
    [InlineData("orders; DROP TABLE users")]
    [InlineData("orders\"; DROP TABLE users --")]
    [InlineData("orders`; DROP TABLE users --")]
    [InlineData("orders WHERE 1=1")]
    [InlineData("orders/**/")]
    [InlineData("orders'")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("orders.")]
    [InlineData(".orders")]
    [InlineData("orders..name")]
    [InlineData("orders\n")]
    [InlineData("orders\r\n")]
    public void Rejects_unsafe_identifiers(string? queryableName)
    {
        Assert.Null(QueryableIdentifier.TryQuote(queryableName, '"'));
    }
}
