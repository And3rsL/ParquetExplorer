using ParquetExplorer.Services;

namespace ParquetExplorer.Tests;

public class SqlTextTests
{
    [Fact]
    public void SplitStatements_IgnoresSemicolonsInStringsAndComments()
    {
        var sql = "SELECT 'a;b' AS x; -- comment; here\nSELECT \"c;d\" FROM t /* ; */;  ;\n";
        var parts = SqlText.SplitStatements(sql);
        Assert.Equal(2, parts.Count);
        Assert.Equal("SELECT 'a;b' AS x", parts[0]);
        Assert.StartsWith("-- comment; here\nSELECT \"c;d\" FROM t /* ; */", parts[1]);
    }

    [Fact]
    public void SplitStatements_SkipsCommentOnlyStatements()
    {
        Assert.Empty(SqlText.SplitStatements("-- nothing here\n/* nor here */;"));
    }

    [Theory]
    [InlineData("sales/orders", "sales_orders")]
    [InlineData("2024 data", "t_2024_data")]
    [InlineData("my-folder.v2", "my_folder_v2")]
    [InlineData("///", "t")]
    public void ToIdentifier_ProducesFriendlyNames(string raw, string expected) =>
        Assert.Equal(expected, SqlText.ToIdentifier(raw));

    [Fact]
    public void LiteralAndIdent_EscapeQuotes()
    {
        Assert.Equal("'it''s'", SqlText.Literal("it's"));
        Assert.Equal("\"a\"\"b\"", SqlText.Ident("a\"b"));
    }

    [Theory]
    [InlineData("  -- c\n /* x */ (select 1)", "SELECT")]
    [InlineData("with x as (select 1) select * from x", "WITH")]
    [InlineData("", "")]
    public void FirstKeyword_SkipsCommentsAndParentheses(string sql, string expected) =>
        Assert.Equal(expected, SqlText.FirstKeyword(sql));
}
