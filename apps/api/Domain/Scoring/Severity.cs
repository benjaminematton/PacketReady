namespace PacketReady.Domain.Scoring;

/// <summary>
/// Issue severity. Ordinals chosen so a descending in-memory sort puts Critical first
/// (Critical > Major > Minor) — used when ordering Issues in a ReadinessScore for the
/// side-panel. Do NOT rely on the ordinal for SQL sorts; <see cref="Severity"/> is
/// not persisted as an integer column.
/// </summary>
public enum Severity
{
    Minor = 1,
    Major = 2,
    Critical = 3,
}
