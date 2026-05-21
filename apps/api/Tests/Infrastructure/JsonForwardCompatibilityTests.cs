using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PacketReady.Domain;
using PacketReady.Domain.Providers;
using Xunit;

namespace PacketReady.Tests.Infrastructure;

/// <summary>
/// Proves <see cref="DomainJson.Options"/> is configured for Phase 3 forward-compat.
/// Without <c>UnmappedMemberHandling.Skip</c>, the first new property added to
/// <see cref="ProviderProfile"/> in Phase 3 would crash every Phase 1 reader still
/// in flight. This test fails loudly if that flag is ever dropped.
/// </summary>
public class JsonForwardCompatibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetProfile_SkipsUnknownProperties()
    {
        // Construct a payload that's a valid ProviderProfile plus extra fields that
        // Phase 3 might add (e.g. LastExtractedAt, ProfileSchemaVersion, ExtractionRuns).
        var futureShapeJson = """
        {
            "fullName": "Dr. Future Person",
            "dateOfBirth": "1980-05-15",
            "npi": "1234567890",
            "credentialingState": "CA",
            "license": null,
            "dea": null,
            "boardCert": null,
            "sanctions": null,
            "lastExtractedAt": "2027-01-15T10:30:00Z",
            "profileSchemaVersion": 3,
            "extractionRuns": [{"id": "abc", "source": "manual"}]
        }
        """;

        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeValidProfile(), Now);

        // Swap in the future-shape JSON via reflection (mimics what'd land on disk
        // if Phase 3 had written this row).
        typeof(Provider).GetProperty(nameof(Provider.ProfileJson))!
            .SetValue(provider, futureShapeJson);

        // The critical assertion: GetProfile must NOT throw on unknown members.
        var profile = provider.GetProfile();

        Assert.Equal("Dr. Future Person", profile.FullName);
        Assert.Equal("1234567890", profile.Npi);
        Assert.Equal("CA", profile.CredentialingState);
    }

    [Fact]
    public async Task Provider_RoundTripsThroughDbWithFutureShape()
    {
        // End-to-end: a future-shape row is saved (as raw JSON), reloaded through EF,
        // and GetProfile() materializes it cleanly. Catches a regression where the
        // EF setter path bypasses DomainJson.Options.
        var futureShapeJson = """
        {
            "fullName": "Dr. Future Person",
            "dateOfBirth": "1980-05-15",
            "npi": "1234567890",
            "credentialingState": "CA",
            "license": null,
            "dea": null,
            "boardCert": null,
            "sanctions": null,
            "unmappedField": "tolerated"
        }
        """;

        var factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        var provider = Provider.Create(MakeValidProfile(), Now);
        typeof(Provider).GetProperty(nameof(Provider.ProfileJson))!
            .SetValue(provider, futureShapeJson);

        await using (var writer = factory.CreateDbContext())
        {
            writer.Providers.Add(provider);
            await writer.SaveChangesAsync();
        }

        await using var reader = factory.CreateDbContext();
        var loaded = await reader.Providers.SingleAsync(p => p.Id == provider.Id);

        var profile = loaded.GetProfile();
        Assert.Equal("Dr. Future Person", profile.FullName);
    }

    [Fact]
    public void GetProfile_PropagatesJsonExceptionOnMalformedJson()
    {
        // Forward-compat is about *unknown* properties, not arbitrary corruption.
        // Malformed JSON must still fail loudly so the bug isn't swallowed.
        var provider = Provider.Create(MakeValidProfile(), Now);
        typeof(Provider).GetProperty(nameof(Provider.ProfileJson))!
            .SetValue(provider, "{not valid");

        Assert.Throws<JsonException>(() => provider.GetProfile());
    }

    private static ProviderProfile MakeValidProfile() =>
        ProviderProfile.Create(
            fullName: "Dr. Jane Smith",
            dateOfBirth: new DateOnly(1980, 5, 15),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: Now);
}
