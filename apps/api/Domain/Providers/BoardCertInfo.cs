namespace PacketReady.Domain.Providers;

public sealed record BoardCertInfo(
    string Board,             // e.g. "ABIM"
    string Specialty,
    DateOnly IssueDate,
    DateOnly ExpiryDate,
    BoardCertStatus Status);

public enum BoardCertStatus { Unknown = 0, Active = 1, Expired = 2 }
