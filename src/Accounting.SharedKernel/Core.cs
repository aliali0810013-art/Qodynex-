using System.Globalization;
using System.Security.Cryptography;

namespace Accounting.SharedKernel;

public static class UuidV7
{
    public static bool IsVersion7(Guid value)
    {
        var text = value.ToString("D");
        return text.Length == 36 && text[14] == '7' && "89abAB".Contains(text[19]);
    }

    public static Guid NewGuid(DateTimeOffset? timestamp = null)
    {
        var unixMilliseconds = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        bytes[0] = (byte)(unixMilliseconds >> 40);
        bytes[1] = (byte)(unixMilliseconds >> 32);
        bytes[2] = (byte)(unixMilliseconds >> 24);
        bytes[3] = (byte)(unixMilliseconds >> 16);
        bytes[4] = (byte)(unixMilliseconds >> 8);
        bytes[5] = (byte)unixMilliseconds;
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(bytes);
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}");
        return Guid.ParseExact(canonical, "D");
    }
}

public readonly record struct Result(bool IsSuccess, string? Error)
{
    public static Result Success() => new(true, null);
    public static Result Failure(string error) => new(false, error);
}

public readonly record struct Result<T>(bool IsSuccess, T? Value, string? Error)
{
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(string error) => new(false, default, error);
}

public sealed record SyncEnvelope(
    Guid EnvelopeId,
    Guid CompanyId,
    Guid BranchId,
    Guid DeviceId,
    Guid OperationId,
    string IdempotencyKey,
    string EventType,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    long? ServerSequence = null);

public sealed record UserContext(
    Guid UserId,
    Guid CompanyId,
    Guid BranchId,
    Guid DeviceId,
    bool IsAuthenticated)
{
    public static UserContext Authenticated(Guid userId, Guid companyId, Guid branchId, Guid deviceId) =>
        new(userId, companyId, branchId, deviceId, true);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
