namespace PacketReady.Application.Abstractions;

/// <summary>
/// Port for inspecting EF persistence exceptions for known database
/// invariants — typically a unique-constraint violation surfaced by the
/// underlying provider (Postgres: SqlState <c>23505</c>) that the
/// Application layer wants to translate into a typed domain exception.
///
/// <para>The seam keeps the Application layer free of the ADO.NET driver
/// package (Npgsql in our case); the Infrastructure layer's
/// implementation knows the driver-specific exception shape and pinpoints
/// the constraint by name. Application calls
/// <see cref="IsUniqueViolation"/> with the constraint name the relevant
/// table defines and translates to the right typed exception when true.</para>
/// </summary>
public interface IDbExceptionTranslator
{
    /// <summary>
    /// True when <paramref name="exception"/> chains down to a unique-
    /// constraint violation against <paramref name="constraintName"/>.
    /// The translator walks <see cref="Exception.InnerException"/> so the
    /// EF <c>DbUpdateException</c> wrapper is transparent.
    /// </summary>
    bool IsUniqueViolation(Exception exception, string constraintName);
}
