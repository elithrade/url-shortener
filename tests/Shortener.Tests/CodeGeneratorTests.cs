namespace Shortener.Tests;

public class CodeGeneratorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20)]
    public void Generated_code_matches_the_length(int length)
    {
        var code = CodeGenerator.GenerateCode(length);
        Assert.Equal(length, code.Length);
    }

    [Fact]
    public void All_generated_codes_are_in_the_character_set()
    {
        List<string> codes = NewCodes(1000);
        Assert.All(codes, code => Assert.Matches("^[a-zA-Z0-9]{7}$", code));
    }

    [Fact]
    public void All_generated_codes_are_distinct()
    {
        List<string> codes = NewCodes(1000);
        Assert.Equal(1000, codes.Distinct().Count());
    }

    [Fact]
    public void Zero_length_gives_an_empty_code() =>
        Assert.Equal("", CodeGenerator.GenerateCode(0));

    [Fact]
    public void Characters_are_uniformly_distributed()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var counts = new int[alphabet.Length];
        var totalChars = 0;

        for (var n = 0; n < 143_000; n++)
        {
            foreach (var c in CodeGenerator.GenerateCode(7))
            {
                var index = alphabet.IndexOf(c);
                Assert.True(index >= 0, $"Unexpected character '{c}' in generated code");
                counts[index]++;
                totalChars++;
            }
        }

        double expected = totalChars / (double)alphabet.Length;
        // sum (observed - expected)^2 / expected over all 62 counts
        double chiSquared = ChiSquared(counts, expected);

        Assert.True(chiSquared < 150, $"Chi-squared was {chiSquared:F1}");
    }

    [Fact]
    public void Chi_squared_example()
    {
        var counts = new[] { 90, 100, 110 };
        var expected = 100d;

        Assert.Equal(2.0, ChiSquared(counts, expected));
    }

    [Fact]
    public void Negative_length_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeGenerator.GenerateCode(-1));

    private static double ChiSquared(int[] observations, double expected)
    {
        double chiSquared = 0;
        foreach (var observed in observations)
        {
            var diff = observed - expected;
            chiSquared += diff * diff / expected;
        }

        return chiSquared;
    }

    private static List<string> NewCodes(int count)
    {
        var codes = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var code = CodeGenerator.GenerateCode(7);
            codes.Add(code);
        }

        return codes;
    }
}
