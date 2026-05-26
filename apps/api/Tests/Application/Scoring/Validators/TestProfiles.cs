using PacketReady.Application.Payers;
using PacketReady.Domain.Providers;

namespace PacketReady.Tests.Application.Scoring.Validators;

/// <summary>
/// Shared builders for validator unit tests. Each <c>Make*</c> defaults to a valid
/// instance (active, expires far in the future, etc.). Tests override the field
/// they're exercising and leave the rest at the happy-path default.
/// </summary>
internal static class TestProfiles
{
    public const string Today = "2026-06-01T00:00:00Z";
    public static readonly DateOnly TodayDate = new(2026, 6, 1);

    /// <summary>
    /// Happy-path profile builder. To exercise "no X" branches, call
    /// <c>MakeProfile() with { X = null }</c> — `with` bypasses shape validation
    /// (which is what we want; the validator under test treats null as "no X on
    /// file" and emits the appropriate Issue).
    /// </summary>
    public static ProviderProfile MakeProfile(
        LicenseInfo? license = null,
        DeaInfo? dea = null,
        BoardCertInfo? boardCert = null,
        MalpracticeInfo? malpractice = null,
        SanctionsResult? sanctions = null,
        string credentialingState = "NY")
    {
        return ProviderProfile.Create(
            fullName: "Dr. Test Person",
            dateOfBirth: new DateOnly(1980, 1, 1),
            npi: "1234567890",
            credentialingState: credentialingState,
            nowUtc: DateTimeOffset.Parse(Today),
            license: license ?? MakeLicense(),
            dea: dea ?? MakeDea(),
            boardCert: boardCert ?? MakeBoardCert(),
            malpractice: malpractice ?? MakeMalpractice(),
            sanctions: sanctions ?? MakeSanctions());
    }

    public static LicenseInfo MakeLicense(
        string number = "MD123456",
        string state = "NY",
        DateOnly? issueDate = null,
        DateOnly? expiryDate = null,
        LicenseStatus status = LicenseStatus.Active)
        => new(number, state, issueDate ?? TodayDate.AddYears(-5), expiryDate ?? TodayDate.AddYears(2), status);

    public static DeaInfo MakeDea(
        string number = "BD1234567",
        DateOnly? expiryDate = null,
        DeaStatus status = DeaStatus.Active,
        IReadOnlyList<DeaSchedule>? schedules = null)
        => new(number, expiryDate ?? TodayDate.AddYears(2), status,
               schedules ?? new[] { DeaSchedule.II, DeaSchedule.III, DeaSchedule.IV, DeaSchedule.V });

    public static BoardCertInfo MakeBoardCert(
        string board = "ABIM",
        string specialty = "Internal Medicine",
        DateOnly? issueDate = null,
        DateOnly? expiryDate = null,
        BoardCertStatus status = BoardCertStatus.Active)
        => new(board, specialty, issueDate ?? TodayDate.AddYears(-3), expiryDate ?? TodayDate.AddYears(5), status);

    public static MalpracticeInfo MakeMalpractice(
        string carrier = "MedProtect Mutual",
        string policyNumber = "MPM-NY-00099001",
        DateOnly? expiryDate = null,
        MalpracticeStatus status = MalpracticeStatus.Active,
        long? perOccurrence = 1_000_000,
        long? aggregate = 3_000_000)
        => new(carrier, policyNumber, expiryDate ?? TodayDate.AddYears(2), status, "",
               perOccurrence, aggregate);

    // Two-payer fixture matching the committed YAMLs. Validator unit tests
    // build against this in-memory catalog rather than YAML-on-disk so they
    // stay fast and decoupled from the loader. The loader has its own
    // integration test that round-trips the real files.
    public static IPayerCatalog MakePayers() => new PayerCatalog(MakePayerDict());

    public static IReadOnlyDictionary<string, PayerRequirement> MakePayerDict() =>
        new Dictionary<string, PayerRequirement>(StringComparer.Ordinal)
        {
            ["payer-a-national-hmo"] = new()
            {
                Id = "payer-a-national-hmo",
                Name = "Payer A — National HMO",
                Malpractice = new MalpracticeRequirement
                {
                    MinimumPerOccurrence = 1_000_000,
                    MinimumAggregate = 3_000_000,
                },
                RequiredDocuments = ["license", "dea", "boardCert", "malpractice"],
                BoardCertRequired = true,
                // Full ABMS member-board enumeration. The umbrella body "ABMS"
                // is intentionally omitted — no real cert is issued by ABMS;
                // it's issued by one of the 24 member boards below.
                AcceptedBoards = AbmsMemberBoards,
                WindowDays = new WindowDays
                {
                    MalpracticeRenewal = 30,
                    LicenseRenewal = 30,
                },
            },
            ["payer-b-state-medicaid"] = new()
            {
                Id = "payer-b-state-medicaid",
                Name = "Payer B — State Medicaid",
                Malpractice = new MalpracticeRequirement
                {
                    MinimumPerOccurrence = 500_000,
                    MinimumAggregate = 1_500_000,
                },
                RequiredDocuments = ["license", "dea", "malpractice"],
                BoardCertRequired = false,
                AcceptedBoards = [],
                WindowDays = new WindowDays
                {
                    MalpracticeRenewal = 60,
                    LicenseRenewal = 60,
                },
            },
        };

    public static SanctionsResult MakeSanctions(
        bool oigClean = true,
        bool samClean = true,
        DateTimeOffset? checkedAt = null)
        => new(oigClean, samClean, checkedAt ?? DateTimeOffset.Parse(Today).AddDays(-7));

    /// <summary>
    /// All 24 ABMS member-board acronyms. Mirrors the YAML at
    /// <c>apps/api/Infrastructure/Payers/payers/payer-a-national-hmo.yaml</c>;
    /// keep both in lockstep. Source of truth: <c>https://www.abms.org/member-boards/</c>.
    /// </summary>
    public static readonly string[] AbmsMemberBoards =
    [
        "ABAI",   // Allergy and Immunology
        "ABA",    // Anesthesiology
        "ABCRS",  // Colon and Rectal Surgery
        "ABD",    // Dermatology
        "ABEM",   // Emergency Medicine
        "ABFM",   // Family Medicine
        "ABIM",   // Internal Medicine
        "ABMGG",  // Medical Genetics and Genomics
        "ABNS",   // Neurological Surgery
        "ABNM",   // Nuclear Medicine
        "ABOG",   // Obstetrics and Gynecology
        "ABO",    // Ophthalmology
        "ABOS",   // Orthopaedic Surgery
        "ABOto",  // Otolaryngology
        "ABPath", // Pathology
        "ABP",    // Pediatrics
        "ABPMR",  // Physical Medicine and Rehabilitation
        "ABPS",   // Plastic Surgery
        "ABPM",   // Preventive Medicine
        "ABPN",   // Psychiatry and Neurology
        "ABR",    // Radiology
        "ABS",    // Surgery
        "ABTS",   // Thoracic Surgery
        "ABU",    // Urology
    ];
}
