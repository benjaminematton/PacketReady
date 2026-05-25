using Microsoft.Extensions.Time.Testing;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Scoring.Validators;

public sealed class LicenseStatusValidatorTests
{
    private static (LicenseStatusValidator Validator, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(Today));
        return (new LicenseStatusValidator(clock), clock);
    }

    [Fact]
    public async Task ValidLicense_EmitsEmpty()
    {
        var (v, _) = Build();
        var issues = await v.RunAsync(MakeProfile(), default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task NoLicense_ShortCircuits()
    {
        // Missing-license is owned by the aggregator (Missing-Document /
        // Extraction-Failed / Partial-Extraction Critical). This validator
        // short-circuits to avoid double-counting Criticals at the dashboard.
        var (v, _) = Build();
        var profile = MakeProfile() with { License = null };

        var issues = await v.RunAsync(profile, default);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task SuspendedStatus_EmitsCritical()
    {
        var (v, _) = Build();
        var profile = MakeProfile(license: MakeLicense(status: LicenseStatus.Suspended));

        var issues = await v.RunAsync(profile, default);

        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("Suspended", only.Message);
    }

    [Fact]
    public async Task ExpiredLicense_EmitsCritical()
    {
        var (v, _) = Build();
        // Status remains Active to isolate the expiry branch (and verify no double-count).
        var expired = MakeLicense(expiryDate: TodayDate.AddDays(-1));
        var profile = MakeProfile(license: expired);

        var issues = await v.RunAsync(profile, default);

        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("expired", only.Message);
    }

    [Fact]
    public async Task ExpiresToday_IsValid_NoExpiredIssue()
    {
        // Industry convention: valid through expiry date inclusive.
        var (v, _) = Build();
        var expiresToday = MakeLicense(expiryDate: TodayDate);
        var profile = MakeProfile(license: expiresToday);

        var issues = await v.RunAsync(profile, default);

        // The license is "expires in 0 days" — Minor (renewal window), not Critical.
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Minor, only.Severity);
    }

    [Fact]
    public async Task StateMismatch_EmitsMajor()
    {
        var (v, _) = Build();
        var profile = MakeProfile(license: MakeLicense(state: "CA"), credentialingState: "NY");

        var issues = await v.RunAsync(profile, default);

        var only = Assert.Single(issues);
        Assert.Equal(Severity.Major, only.Severity);
        Assert.Contains("CA", only.Message);
        Assert.Contains("NY", only.Message);
    }

    [Fact]
    public async Task RenewalWindow_EmitsMinor()
    {
        var (v, _) = Build();
        var profile = MakeProfile(license: MakeLicense(expiryDate: TodayDate.AddDays(15)));

        var issues = await v.RunAsync(profile, default);

        var only = Assert.Single(issues);
        Assert.Equal(Severity.Minor, only.Severity);
        Assert.Contains("15 days", only.Message);
    }

    [Fact]
    public async Task RenewalWindow_Boundary_ThirtyDays_EmitsEmpty()
    {
        // Renewal window is `< 30` days; exactly 30 days out is outside the window.
        // Locks the boundary against a `<= 30` regression.
        var (v, _) = Build();
        var profile = MakeProfile(license: MakeLicense(expiryDate: TodayDate.AddDays(30)));

        var issues = await v.RunAsync(profile, default);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task ExpiredAndStateMismatch_EmitsBoth_NoMinor()
    {
        // The doc's "yellow fixture" case: license expired AND wrong state.
        // Both Issues emitted; the renewal-window Minor is suppressed because the
        // license is already expired.
        var (v, _) = Build();
        var profile = MakeProfile(
            license: MakeLicense(state: "CA", expiryDate: TodayDate.AddDays(-30)),
            credentialingState: "NY");

        var issues = await v.RunAsync(profile, default);

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Severity == Severity.Critical && i.Message.Contains("expired"));
        Assert.Contains(issues, i => i.Severity == Severity.Major && i.Message.Contains("CA"));
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Minor);
    }

    [Fact]
    public async Task EveryIssue_CarriesValidatorName()
    {
        // Guards against the "hardcoded literal" bug — if a future refactor stamps
        // the wrong name, this test fails for every branch at once.
        var (v, _) = Build();
        var profile = MakeProfile(
            license: MakeLicense(state: "CA", expiryDate: TodayDate.AddDays(-30), status: LicenseStatus.Suspended),
            credentialingState: "NY");

        var issues = await v.RunAsync(profile, default);

        Assert.All(issues, i => Assert.Equal("license_status", i.Validator));
        Assert.All(issues, i => Assert.All(i.Citations, c => Assert.Equal("license_status", c.SourceValidator)));
    }

    [Fact]
    public async Task CitationCarriesProvenance_WhenProvenanceMapHasLicenseExpiry()
    {
        // Slice-8 contract: when the aggregator threads provenance for
        // "license.expiryDate", the validator's emitted Citation carries
        // DocumentId/Page/Bbox so the dashboard can drill into the PDF.
        var (v, _) = Build();
        var profile = MakeProfile() with
        {
            License = new LicenseInfo(
                Number: "MD-NY-00001",
                State: "NY",
                IssueDate: DateOnly.Parse("2020-01-01"),
                ExpiryDate: DateOnly.Parse("2024-01-01"),   // expired → Critical
                Status: LicenseStatus.Active),
        };

        var docId = Guid.NewGuid();
        var bbox = new BoundingBox(120, 400, 260, 422);
        var provenance = new Dictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance>
        {
            ["license.expiryDate"] = new(docId, Page: 1, Bbox: bbox, Confidence: 0.95),
        };

        var issues = await v.RunAsync(profile, provenance, default);

        var citation = issues.Single().Citations.Single();
        Assert.Equal(docId, citation.DocumentId);
        Assert.Equal(1, citation.Page);
        Assert.Equal(bbox, citation.Bbox);
    }
}
