using Microsoft.Extensions.Time.Testing;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Scoring.Validators;

public sealed class MalpracticeCurrencyValidatorTests
{
    private const string PayerA = "payer-a-national-hmo";
    private const string PayerB = "payer-b-state-medicaid";

    private static MalpracticeCurrencyValidator Build() =>
        new(new FakeTimeProvider(DateTimeOffset.Parse(Today)), MakePayers());

    [Fact]
    public async Task ActivePolicyAtMinimums_EmitsEmpty()
    {
        var issues = await Build().RunAsync(MakeProfile(), EmptyProvenance(), PayerA, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task NoMalpractice_ShortCircuits()
    {
        // Missing-malpractice is owned by the aggregator; this validator stays
        // silent to avoid double-counting Criticals.
        var profile = MakeProfile() with { Malpractice = null };
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task LapsedStatus_EmitsCritical()
    {
        var profile = MakeProfile(malpractice: MakeMalpractice(status: MalpracticeStatus.Lapsed));
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("Lapsed", only.Message);
    }

    [Fact]
    public async Task ExpiredDate_EmitsCritical()
    {
        // Status stays Active to isolate the expiry branch.
        var profile = MakeProfile(malpractice: MakeMalpractice(expiryDate: TodayDate.AddDays(-1)));
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Contains("expired", only.Message);
    }

    [Fact]
    public async Task PerOccurrenceBelowMinimum_EmitsMajor()
    {
        // $500k against payer-a's $1M floor.
        var profile = MakeProfile(malpractice: MakeMalpractice(perOccurrence: 500_000));
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Major, only.Severity);
        Assert.Contains("per-occurrence", only.Message);
        Assert.Contains("$500,000", only.Message);
    }

    [Fact]
    public async Task AggregateBelowMinimum_EmitsMajor()
    {
        var profile = MakeProfile(malpractice: MakeMalpractice(aggregate: 1_000_000));
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Major, only.Severity);
        Assert.Contains("aggregate", only.Message);
    }

    [Fact]
    public async Task NullCoverage_DoesNotEmitCoverageIssue()
    {
        // Extractor failed to read the coverage line — aggregator's
        // Partial-Extraction lane handles that, not us. Null means "no data",
        // not "policy has $0 coverage".
        var profile = MakeProfile(
            malpractice: MakeMalpractice(perOccurrence: null, aggregate: null));
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task PayerB_LowerMinimums_AcceptsPayerAUnacceptableCoverage()
    {
        // A $500k/$1.5M policy fails payer-a but exactly meets payer-b's floor.
        var profile = MakeProfile(
            malpractice: MakeMalpractice(perOccurrence: 500_000, aggregate: 1_500_000));
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerB, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task RenewalWindow_UsesPayerWindowDays()
    {
        // 45 days out: inside payer-b's 60-day window, outside payer-a's 30-day window.
        var profile = MakeProfile(malpractice: MakeMalpractice(expiryDate: TodayDate.AddDays(45)));

        var payerAIssues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        Assert.Empty(payerAIssues);

        var payerBIssues = await Build().RunAsync(profile, EmptyProvenance(), PayerB, default);
        var only = Assert.Single(payerBIssues);
        Assert.Equal(Severity.Minor, only.Severity);
        Assert.Contains("45 days", only.Message);
    }

    [Fact]
    public async Task ExpiresToday_IsValid_NoIssue()
    {
        // Industry convention: valid through the expiry date inclusive. With
        // a 30-day payer window the 0-day delta is NOT inside `< 30 days`,
        // so today-expiry produces no Minor either.
        var profile = MakeProfile(malpractice: MakeMalpractice(expiryDate: TodayDate));
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        // No Critical (not yet expired), no Minor (0 < 30 holds, so renewal Minor fires).
        // Verify the only Issue is the renewal Minor, not a Critical.
        var only = Assert.Single(issues);
        Assert.Equal(Severity.Minor, only.Severity);
    }

    [Fact]
    public async Task UnknownPayerId_Throws()
    {
        // Fail-loud: a payer id not backed by a YAML is an operator bug,
        // not a default-to-payer-a moment.
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Build().RunAsync(MakeProfile(), EmptyProvenance(), "payer-c-doesnt-exist", default));
    }

    [Fact]
    public async Task EveryIssue_CarriesValidatorName()
    {
        // Stack a Critical and a Major on the same provider.
        var profile = MakeProfile(malpractice: MakeMalpractice(
            status: MalpracticeStatus.Lapsed,
            perOccurrence: 250_000));
        var issues = await Build().RunAsync(profile, EmptyProvenance(), PayerA, default);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal("malpractice_currency", i.Validator));
    }

    private static IReadOnlyDictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance> EmptyProvenance() =>
        new Dictionary<string, PacketReady.Application.Providers.Aggregation.FieldProvenance>();
}
