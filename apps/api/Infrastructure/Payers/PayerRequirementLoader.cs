using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PacketReady.Infrastructure.Payers;

/// <summary>
/// Loads every <c>*.yaml</c> in a payer directory into a frozen
/// <c>{id → PayerRequirement}</c> dictionary. Called once at DI bootstrap
/// (see <see cref="DependencyInjection.AddPersistence"/>); failure throws
/// before <c>App.Build()</c> returns so a schema-broken YAML can't ship.
///
/// <para>Failure modes (all <see cref="InvalidOperationException"/>, all
/// naming the offending file):
/// <list type="bullet">
///   <item>Directory missing or empty.</item>
///   <item>YAML parse error.</item>
///   <item>File stem doesn't match the file's <c>id:</c> field — catches
///   rename mistakes where the dictionary key would silently shadow.</item>
///   <item>Required key missing or unusable (empty <c>name</c>, empty
///   <c>requiredDocuments</c>, non-positive malpractice minimums, etc.).</item>
///   <item><c>boardCertRequired</c> and <c>acceptedBoards</c> out of sync in
///   either direction — true+empty (validator can't distinguish "any board"
///   from "no board accepted") or false+non-empty (data the validator will
///   silently ignore). Refused at load time, not at runtime.</item>
///   <item><c>malpractice.minimumAggregate</c> below <c>minimumPerOccurrence</c>
///   — an incoherent policy shape that would still parse cleanly.</item>
/// </list>
/// </para>
///
/// <para>Unresolved <c>Provider.PayerId</c> at validator-run time is a
/// runtime <see cref="KeyNotFoundException"/>, not a startup failure —
/// the validator names which provider id caused the miss. A startup-time
/// cross-check against the providers table would require a DB query
/// during DI bootstrap; not worth the coupling.</para>
/// </summary>
public static class PayerRequirementLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()  // Tolerate forward-compat YAML keys; loader pins required ones.
        .Build();

    public static IReadOnlyDictionary<string, PayerRequirement> LoadAll(string directory)
    {
        if (!Directory.Exists(directory))
            throw new InvalidOperationException(
                $"Payer directory '{directory}' does not exist. Expected to contain at least one payer YAML.");

        var files = Directory.EnumerateFiles(directory, "*.yaml").OrderBy(p => p).ToList();
        if (files.Count == 0)
            throw new InvalidOperationException(
                $"Payer directory '{directory}' contains no *.yaml files; at least one payer is required.");

        var result = new Dictionary<string, PayerRequirement>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var requirement = ParseOne(path);
            // Unreachable while the stem-↔-id check below holds (filesystem
            // entries have unique paths → unique stems → unique ids), but
            // kept as a safety net in case that invariant is ever loosened.
            if (!result.TryAdd(requirement.Id, requirement))
                throw new InvalidOperationException(
                    $"Duplicate payer id '{requirement.Id}' in {Path.GetFileName(path)}; ids must be unique across the directory.");
        }
        return result;
    }

    private static PayerRequirement ParseOne(string path)
    {
        var file = Path.GetFileName(path);
        PayerRequirement parsed;
        try
        {
            parsed = Deserializer.Deserialize<PayerRequirement>(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"Payer YAML {file} deserialized to null.");
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException(
                $"Payer YAML {file} failed to parse: {ex.Message}", ex);
        }

        ValidateShape(parsed, path);
        return parsed;
    }

    private static void ValidateShape(PayerRequirement r, string path)
    {
        var file = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);

        // File stem ↔ id pinning. Catches: file copied + only one of (name, id)
        // updated; an id that doesn't match its filename would route lookups by
        // the wrong key, and the dictionary would silently shadow.
        if (r.Id != stem)
            throw new InvalidOperationException(
                $"Payer YAML {file}: file stem '{stem}' must equal id '{r.Id}'. Rename the file or fix the id.");

        if (string.IsNullOrWhiteSpace(r.Name))
            throw new InvalidOperationException(
                $"Payer YAML {file}: 'name' is required.");

        if (r.RequiredDocuments.Length == 0)
            throw new InvalidOperationException(
                $"Payer YAML {file}: 'requiredDocuments' must be non-empty.");

        if (r.BoardCertRequired && r.AcceptedBoards.Length == 0)
            throw new InvalidOperationException(
                $"Payer YAML {file}: boardCertRequired=true but acceptedBoards is empty. " +
                "Use boardCertRequired=false to opt out, or list at least one accepted board.");

        if (!r.BoardCertRequired && r.AcceptedBoards.Length > 0)
            throw new InvalidOperationException(
                $"Payer YAML {file}: boardCertRequired=false but acceptedBoards is non-empty. " +
                "Remove acceptedBoards or set boardCertRequired=true — the validator only consults " +
                "the list when board cert is required, so a populated list with required=false is " +
                "silent dead data.");

        if (r.Malpractice.MinimumPerOccurrence <= 0 || r.Malpractice.MinimumAggregate <= 0)
            throw new InvalidOperationException(
                $"Payer YAML {file}: malpractice.minimumPerOccurrence and minimumAggregate must be positive.");

        if (r.Malpractice.MinimumAggregate < r.Malpractice.MinimumPerOccurrence)
            throw new InvalidOperationException(
                $"Payer YAML {file}: malpractice.minimumAggregate ({r.Malpractice.MinimumAggregate}) " +
                $"must be >= minimumPerOccurrence ({r.Malpractice.MinimumPerOccurrence}); " +
                "aggregate is the policy-wide cap and can't sit below a single occurrence cap.");

        if (r.WindowDays.MalpracticeRenewal <= 0 || r.WindowDays.LicenseRenewal <= 0)
            throw new InvalidOperationException(
                $"Payer YAML {file}: windowDays.malpracticeRenewal and licenseRenewal must be positive.");
    }
}
