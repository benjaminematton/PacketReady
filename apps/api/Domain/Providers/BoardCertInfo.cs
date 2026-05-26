namespace PacketReady.Domain.Providers;

public sealed record BoardCertInfo(
    string Board,             // e.g. "ABIM"
    string Specialty,
    DateOnly IssueDate,
    DateOnly ExpiryDate,
    BoardCertStatus Status,
    // Per-doc full name extracted off the board-cert PDF; consumed by the P4
    // identity-coherence validator. Default "" lets pre-P4 callers stay as-is.
    string FullName = "");

public enum BoardCertStatus { Unknown = 0, Active = 1, Expired = 2 }
