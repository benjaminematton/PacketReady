namespace PacketReady.Domain.Documents;

/// <summary>
/// Provenance of a single extraction row. P3 writes only <see cref="Llm"/> rows;
/// <see cref="ProviderEdit"/> and <see cref="AdminEdit"/> are reserved for P5's
/// confirmation flow (design §7.9) — entity carries them now so the schema doesn't
/// need a second migration when P5 lands.
///
/// <para>Stored as snake_case lowercase TEXT (<c>'llm' | 'provider_edit' | 'admin_edit'</c>)
/// via an explicit value converter in <c>DocumentExtractionConfiguration</c>. Check
/// constraint pins the value set.</para>
/// </summary>
public enum ExtractionSource
{
    Llm,
    ProviderEdit,
    AdminEdit,
}
