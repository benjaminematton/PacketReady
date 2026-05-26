using System.Text.Json;
using PacketReady.Infrastructure.PrimarySources;
using Xunit;

namespace PacketReady.Tests.Infrastructure.PrimarySources;

public class MockPrimarySourceLookupTests
{
    private readonly MockPrimarySourceLookup _lookup = new();

    private static JsonElement WithNpi(string npi) =>
        JsonDocument.Parse($"{{\"npi\":\"{npi}\"}}").RootElement;

    [Fact]
    public async Task NppesLookup_GreenTierProvider_ReturnsCleanMatch()
    {
        var result = await _lookup.LookupAsync("nppes", WithNpi("1234567890"), CancellationToken.None);

        Assert.True(result.GetProperty("found").GetBoolean());
        Assert.Equal("Henry Anderson", result.GetProperty("fields").GetProperty("full_name").GetString());
        Assert.Equal(0, result.GetProperty("mismatch_fields").GetArrayLength());
    }

    [Fact]
    public async Task NppesLookup_YellowTierProvider_ReturnsNameMismatch()
    {
        var result = await _lookup.LookupAsync("nppes", WithNpi("9876543210"), CancellationToken.None);

        Assert.True(result.GetProperty("found").GetBoolean());
        var mismatches = result.GetProperty("mismatch_fields");
        Assert.Equal(1, mismatches.GetArrayLength());
        Assert.Equal("full_name", mismatches[0].GetString());
    }

    [Fact]
    public async Task OigLookup_RedTierProvider_ReturnsSanction()
    {
        var result = await _lookup.LookupAsync("oig", WithNpi("5555555555"), CancellationToken.None);

        Assert.True(result.GetProperty("found").GetBoolean());
        Assert.Equal("Mandatory", result.GetProperty("fields").GetProperty("exclusion_type").GetString());
    }

    [Fact]
    public async Task UnknownNpi_ReturnsNotFound()
    {
        var result = await _lookup.LookupAsync("nppes", WithNpi("0000000000"), CancellationToken.None);

        Assert.False(result.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task UnknownSource_ReturnsNotFound()
    {
        // A source name not in the canned table — the dispatcher falls
        // through to the not-found branch rather than throwing.
        var result = await _lookup.LookupAsync("medicare_provider_lookup", WithNpi("1234567890"), CancellationToken.None);

        Assert.False(result.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task MissingNpi_ReturnsErrorShape()
    {
        var identifiers = JsonDocument.Parse("{\"license_number\":\"X123\"}").RootElement;
        var result = await _lookup.LookupAsync("nppes", identifiers, CancellationToken.None);

        Assert.False(result.GetProperty("found").GetBoolean());
        Assert.True(result.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSource_ReturnsErrorShape(string source)
    {
        var result = await _lookup.LookupAsync(source, WithNpi("1234567890"), CancellationToken.None);

        Assert.False(result.GetProperty("found").GetBoolean());
        Assert.True(result.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task NppesLookup_CaseInsensitiveSourceName()
    {
        // "NPPES" works the same as "nppes" — defensive against the LLM
        // emitting uppercase or mixed-case source names.
        var result = await _lookup.LookupAsync("NPPES", WithNpi("1234567890"), CancellationToken.None);

        Assert.True(result.GetProperty("found").GetBoolean());
    }
}
