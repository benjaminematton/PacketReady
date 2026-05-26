using Npgsql;
using PacketReady.Application.Abstractions;

namespace PacketReady.Infrastructure.Persistence;

/// <summary>
/// Postgres-flavored <see cref="IDbExceptionTranslator"/>. Walks the
/// inner-exception chain looking for a <see cref="PostgresException"/>
/// with SqlState <c>23505</c> (unique_violation) against a specific
/// constraint name.
///
/// <para>Pinned to the constraint name so a future UNIQUE added to a
/// table (e.g. on a derived column) does NOT silently get treated as the
/// caller's expected race. Mirrors the
/// <c>ExtractionPersister.IsIdempotencyRaceLost</c> shape.</para>
/// </summary>
public sealed class PostgresExceptionTranslator : IDbExceptionTranslator
{
    private const string UniqueViolationSqlState = "23505";

    public bool IsUniqueViolation(Exception exception, string constraintName)
    {
        if (string.IsNullOrWhiteSpace(constraintName))
            throw new ArgumentException("constraintName is required.", nameof(constraintName));

        var inner = exception.InnerException;
        while (inner is not null)
        {
            if (inner is PostgresException pg
                && pg.SqlState == UniqueViolationSqlState
                && pg.ConstraintName == constraintName)
                return true;
            inner = inner.InnerException;
        }
        return false;
    }
}
