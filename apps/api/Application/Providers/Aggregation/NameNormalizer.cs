namespace PacketReady.Application.Providers.Aggregation;

/// <summary>
/// Shared name-comparison normalizer. Strips the credential suffixes our
/// extractors see in practice (", MD", ", DO", ", MBBS", ", PhD", ", DNP",
/// ", NP", ", PA"), case-folds, and trims, so two names that differ only
/// by formatting compare equal.
///
/// <para>Used by:
/// <list type="bullet">
///   <item><c>ProviderProfileAggregator</c>'s Levenshtein-based cross-doc
///         fullName mismatch check (Minor).</item>
///   <item><c>IdentityCoherenceValidator.PickVariantSource</c>: pin the
///         <c>Field</c> discriminator on the actual variant source by
///         comparing extracted names with formatting noise stripped. Without
///         this, "MICHAEL FOSTER" (DEA's all-caps render) reads as
///         "differs from license" before the real planted variant
///         ("Michael M. Foster-Taylor, MD") is even checked, and Field
///         gets stamped <c>dea.fullName</c> instead of
///         <c>malpractice.fullName</c> — the eval's conflict_metrics
///         predicate-3 then silently misses every name_variant catch.</item>
/// </list>
/// </para>
///
/// <para>Pure / allocation-modest. Iterates to a fixpoint so stacked
/// credentials like <c>"Henry Anderson, MD, PhD"</c> reduce all the way down;
/// a single pass would leave the inner suffix behind once the outer match
/// strips ahead of it in iteration order.</para>
/// </summary>
public static class NameNormalizer
{
    private static readonly string[] CredentialSuffixes =
    {
        ", MD", ", DO", ", MBBS", ", PhD", ", DNP", ", NP", ", PA",
    };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var s = name.TrimEnd(' ', ',', '.');
        bool stripped;
        do
        {
            stripped = false;
            foreach (var suffix in CredentialSuffixes)
            {
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[..^suffix.Length].TrimEnd(' ', ',', '.');
                    stripped = true;
                }
            }
        } while (stripped);

        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Equality on normalized form. Convenience wrapper for the common
    /// "are these the same person, formatting aside?" check.
    /// </summary>
    public static bool AreEqual(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
}
