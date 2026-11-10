using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Accounting.Infrastructure.Server;

/// <summary>
/// Retries a complete PostgreSQL transaction after SQLSTATE 40001 or 40P01.
/// The supplied delegate MUST open and commit its own transaction on every
/// invocation. Retrying individual SQL statements within an aborted transaction
/// is not safe and can create gaps or non-idempotent financial side effects.
/// </summary>
public static class PostgresTransactionalRetry
{
    public const int MaxAttempts = 30;

    public static async Task<T> ExecuteAsync<T>(
        ServerAccountingDbContext db,
        Func<CancellationToken, Task<T>> transaction,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await transaction(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (attempt < MaxAttempts && IsTransientConcurrency(error))
            {
                // The failed transaction must have been rolled back/disposed by
                // the delegate. Forget all entities and generated values from
                // the aborted attempt before re-running the entire transaction.
                db.ChangeTracker.Clear();
                var capMs = Math.Min(800, 15 * (1 << Math.Min(attempt - 1, 5)));
                var delayMs = Random.Shared.Next(capMs / 2, capMs + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientConcurrency(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres &&
                (postgres.SqlState == PostgresErrorCodes.SerializationFailure ||
                 postgres.SqlState == PostgresErrorCodes.DeadlockDetected))
                return true;
        }
        return false;
    }
}
