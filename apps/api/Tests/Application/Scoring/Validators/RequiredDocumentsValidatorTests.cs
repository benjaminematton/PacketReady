using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Scoring;
using Xunit;
using static PacketReady.Tests.Application.Scoring.Validators.TestProfiles;

namespace PacketReady.Tests.Application.Scoring.Validators;

public sealed class RequiredDocumentsValidatorTests
{
    private const string PayerA = "payer-a-national-hmo";
    private const string PayerB = "payer-b-state-medicaid";

    private static RequiredDocumentsValidator Build(
        IReadOnlyDictionary<string, PayerRequirement>? payers = null) =>
        new(payers ?? MakePayers());

    private static IReadOnlyDictionary<string, FieldProvenance> EmptyProvenance() =>
        new Dictionary<string, FieldProvenance>();

    [Fact]
    public async Task PayerRequiresOnlyUniversal4_EmitsEmpty()
    {
        // Both committed payer YAMLs only require universal-4 doc types,
        // which the aggregator owns — this validator emits nothing in
        // the common case.
        var issues = await Build().RunAsync(MakeProfile(), EmptyProvenance(), PayerA, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task PayerB_AlsoEmpty_WhenOnlyUniversal4Required()
    {
        var issues = await Build().RunAsync(MakeProfile(), EmptyProvenance(), PayerB, default);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task PayerRequiresNonUniversalDoc_EmitsCritical()
    {
        // Synthetic payer requires a state-registration doc beyond universal-4.
        // The validator emits Critical because there's no signal that a
        // non-universal doc was uploaded.
        var payers = new Dictionary<string, PayerRequirement>
        {
            ["payer-x-with-state-reg"] = new()
            {
                Id = "payer-x-with-state-reg",
                Name = "Payer X — Requires State Reg",
                Malpractice = new MalpracticeRequirement
                {
                    MinimumPerOccurrence = 1_000_000,
                    MinimumAggregate = 3_000_000,
                },
                RequiredDocuments = ["license", "dea", "boardCert", "malpractice", "stateRegistration"],
                BoardCertRequired = true,
                AcceptedBoards = ["ABMS"],
                WindowDays = new WindowDays { MalpracticeRenewal = 30, LicenseRenewal = 30 },
            },
        };

        var issues = await Build(payers).RunAsync(
            MakeProfile(), EmptyProvenance(), "payer-x-with-state-reg", default);

        var only = Assert.Single(issues);
        Assert.Equal(Severity.Critical, only.Severity);
        Assert.Equal("required_documents", only.Validator);
        Assert.Contains("stateRegistration", only.Message);
        Assert.Empty(only.Citations);  // doc-less citation by design
    }

    [Fact]
    public async Task UniversalDocTypes_AlwaysSkipped_NeverEmitted()
    {
        // Pathological payer YAML asking for license/dea/boardCert/malpractice
        // shouldn't double-count with the aggregator — even if the payer
        // names them, this validator stays silent on those exact types.
        var payers = new Dictionary<string, PayerRequirement>
        {
            ["payer-y-only-universal"] = new()
            {
                Id = "payer-y-only-universal",
                Name = "Payer Y — Only Universal Docs",
                Malpractice = new MalpracticeRequirement
                {
                    MinimumPerOccurrence = 1_000_000,
                    MinimumAggregate = 3_000_000,
                },
                RequiredDocuments = ["license", "dea", "boardCert", "malpractice"],
                BoardCertRequired = true,
                AcceptedBoards = ["ABMS"],
                WindowDays = new WindowDays { MalpracticeRenewal = 30, LicenseRenewal = 30 },
            },
        };

        var issues = await Build(payers).RunAsync(
            MakeProfile(), EmptyProvenance(), "payer-y-only-universal", default);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task UnknownPayerId_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Build().RunAsync(MakeProfile(), EmptyProvenance(), "payer-c-doesnt-exist", default));
    }
}
