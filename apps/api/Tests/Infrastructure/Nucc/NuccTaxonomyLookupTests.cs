using System.IO;
using PacketReady.Infrastructure.Nucc;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Nucc;

/// <summary>
/// CSV parsing + happy-path lookup against the committed NUCC snapshot.
/// The snapshot lives at the repo's <c>data/nucc-taxonomy-25.1.csv</c> and is
/// copied to the Infrastructure assembly's <c>Nucc/</c> directory at build
/// time. Tests resolve the file relative to <see cref="AppContext.BaseDirectory"/>
/// — the same path the DI bootstrap uses.
/// </summary>
public sealed class NuccTaxonomyLookupTests
{
    private static string ShippedCsvPath =>
        Path.Combine(AppContext.BaseDirectory, "Nucc", "nucc-taxonomy-25.1.csv");

    private static NuccTaxonomyLookup BuildShipped() => new(ShippedCsvPath);

    [Fact]
    public void ShippedSnapshot_Loads_WithPlausibleRowCount()
    {
        var lookup = BuildShipped();
        // NUCC snapshots have ~870–900 individual + group rows. Anything
        // wildly off means a corrupt file or parser regression.
        Assert.InRange(lookup.Count, 500, 2000);
    }

    [Theory]
    // Stable NUCC codes that have shipped for years; this exercises the
    // "code resolves to a non-empty Display Name" contract end-to-end.
    [InlineData("207R00000X")]   // Internal Medicine
    [InlineData("207Q00000X")]   // Family Medicine
    [InlineData("207RC0000X")]   // Cardiovascular Disease
    public void StableCodes_Resolve(string code)
    {
        var lookup = BuildShipped();

        Assert.True(lookup.TryGet(code, out var displayName));
        Assert.False(string.IsNullOrWhiteSpace(displayName));
    }

    [Fact]
    public void UnknownCode_ReturnsFalse()
    {
        var lookup = BuildShipped();

        Assert.False(lookup.TryGet("XXXNOTACODE", out var v));
        Assert.Equal("", v);
    }

    [Fact]
    public void EmptyCode_ReturnsFalse()
    {
        var lookup = BuildShipped();

        Assert.False(lookup.TryGet("", out _));
        Assert.False(lookup.TryGet("   ", out _));
    }

    [Fact]
    public void MissingFile_FailsLoud()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "nucc-does-not-exist.csv");

        Assert.Throws<InvalidOperationException>(() => new NuccTaxonomyLookup(bogus));
    }

    [Fact]
    public void HandlesQuotedFields_WithEmbeddedCommas()
    {
        // NUCC's Definition column has commas inside quoted strings; a
        // naive split-on-comma would shred those rows and either fail
        // the row count or misalign Display Name. The shipped-CSV
        // happy-path test above proves this in aggregate; this test
        // pins the parser on a hand-built row.
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp,
                "Code,Grouping,Classification,Specialization,Definition,Notes,Display Name,Section\n" +
                "TEST00000X,Group,Test,\"\",\"A practitioner, with a comma in their definition.\",[note],Test Specialty,Individual\n");
            var lookup = new NuccTaxonomyLookup(temp);

            Assert.True(lookup.TryGet("TEST00000X", out var v));
            Assert.Equal("Test Specialty", v);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
