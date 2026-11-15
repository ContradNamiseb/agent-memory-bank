using AgentMemoryBank.McpServer;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// Numeric options: a value that is not a whole number is a usage error naming
/// the option, --percent must be a percentage threshold mode can reach, and
/// --limit and --id must be 1 or more.
/// </summary>
public class CommandLineTests
{
    [Theory]
    [InlineData("--percent", "abc")]
    [InlineData("--percent", "0")]
    [InlineData("--percent", "-5")]
    [InlineData("--percent", "101")]
    [InlineData("--limit", "ten")]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "-1")]
    [InlineData("--id", "1.5")]
    [InlineData("--id", "0")]
    [InlineData("--id", "-3")]
    public void A_bad_number_is_a_usage_error_naming_the_option_and_the_value(string flag, string value)
    {
        var error = Assert.Throws<ArgumentException>(() => CommandLine.Parse([flag, value]));
        Assert.Contains(flag, error.Message);
        Assert.Contains($"'{value}'", error.Message);
    }

    [Fact]
    public void Good_numbers_parse()
    {
        var options = CommandLine.Parse(["--percent", "80", "--limit", "5", "--id", "12345678901"]);
        Assert.Equal(80, options.Percent);
        Assert.Equal(5, options.LimitOption);
        Assert.Equal(12345678901L, options.Id);
    }
}
