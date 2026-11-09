namespace Accounting.SharedKernel;

public sealed record DeviceRegistrationRequest(
    Guid DeviceId,
    string DeviceName,
    string Platform,
    string ClientVersion,
    int ClientSchemaVersion,
    string ProtocolVersion);

public sealed record DeviceRegistrationResult(
    Guid DeviceId,
    string DeviceName,
    string Platform,
    string ClientVersion,
    int ClientSchemaVersion,
    string ProtocolVersion,
    bool IsActive,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastSeenAtUtc);
