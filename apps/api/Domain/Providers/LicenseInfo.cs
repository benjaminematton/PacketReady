namespace PacketReady.Domain.Providers;

public sealed record LicenseInfo(
    string Number,
    string State,
    DateOnly IssueDate,
    DateOnly ExpiryDate,
    LicenseStatus Status);

public enum LicenseStatus { Unknown = 0, Active = 1, Suspended = 2, Expired = 3 }
