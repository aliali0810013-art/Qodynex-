using System.Net;
using System.Net.NetworkInformation;
using Accounting.SharedKernel;

namespace Accounting.Sync.Client;

public interface INetworkAvailability
{
    Task<bool> IsNetworkAvailableAsync(CancellationToken cancellationToken = default);
}

public sealed class SystemNetworkAvailability : INetworkAvailability
{
    public Task<bool> IsNetworkAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(NetworkInterface.GetIsNetworkAvailable());
}

public sealed record SyncCycleResult(
    int PushedMessages,
    int PulledChanges,
    int Conflicts,
    long LastWatermark,
    DateTimeOffset CompletedAtUtc);

public sealed class BackgroundSyncCoordinator
{
    private readonly ISyncTransport _transport;
    private readonly LocalSyncStore _local;
    private readonly INetworkAvailability _network;
    private readonly UserContext _userContext;
    private readonly IClock _clock;

    public BackgroundSyncCoordinator(ISyncTransport transport, LocalSyncStore local, INetworkAvailability network, UserContext userContext, IClock clock)
    {
        _transport = transport;
        _local = local;
        _network = network;
        _userContext = userContext;
        _clock = clock;
    }

    public async Task<SyncCycleResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await _network.IsNetworkAvailableAsync(cancellationToken).ConfigureAwait(false))
            return new SyncCycleResult(0, 0, 0, await _local.GetLastPulledWatermarkAsync(_userContext, cancellationToken).ConfigureAwait(false), _clock.UtcNow);

        var pushed = 0;
        for (var batchNumber = 0; batchNumber < 20; batchNumber++)
        {
            var pending = await _local.GetPendingEnvelopesAsync(_userContext, SyncProtocol.MaxBatchSize, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0) break;

            var watermark = await _local.GetLastPulledWatermarkAsync(_userContext, cancellationToken).ConfigureAwait(false);
            var batch = new SyncPushBatch(SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion,
                _userContext.DeviceId, watermark, pending);
            try
            {
                var result = await _transport.PushAsync(batch, cancellationToken).ConfigureAwait(false);
                await _local.ApplyPushResultAsync(_userContext, result, cancellationToken).ConfigureAwait(false);
                pushed += pending.Count;
            }
            catch (SyncTransportException ex) when (ex.StatusCodeValue == HttpStatusCode.UpgradeRequired)
            {
                await _local.RecordUpgradeRequiredAsync(_userContext, ex.ResponseBody, cancellationToken).ConfigureAwait(false);
                throw new SyncClientUpgradeRequiredException("The server requires a newer schema/protocol. Local data was left unchanged.", ex);
            }
            catch
            {
                await _local.MarkTransportFailureAsync(_userContext, pending, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        var pulled = 0;
        var conflictsBefore = await _local.GetUnresolvedConflictCountAsync(_userContext, cancellationToken).ConfigureAwait(false);
        var after = await _local.GetLastPulledWatermarkAsync(_userContext, cancellationToken).ConfigureAwait(false);
        for (var page = 0; page < 50; page++)
        {
            var request = new SyncPullRequest(SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion,
                _userContext.DeviceId, after, SyncProtocol.MaxBatchSize);
            SyncPullResult result;
            try
            {
                result = await _transport.PullAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (SyncTransportException ex) when (ex.StatusCodeValue == HttpStatusCode.UpgradeRequired)
            {
                await _local.RecordUpgradeRequiredAsync(_userContext, ex.ResponseBody, cancellationToken).ConfigureAwait(false);
                throw new SyncClientUpgradeRequiredException("The server requires a newer schema/protocol. Local data was left unchanged.", ex);
            }
            await _local.ApplyPullResultAsync(_userContext, result, cancellationToken).ConfigureAwait(false);
            pulled += result.Changes.Count;
            after = result.NextWatermark;
            if (!result.HasMore) break;
        }

        var conflictsAfter = await _local.GetUnresolvedConflictCountAsync(_userContext, cancellationToken).ConfigureAwait(false);
        return new SyncCycleResult(pushed, pulled, Math.Max(0, conflictsAfter - conflictsBefore), after, _clock.UtcNow);
    }

    public async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                failures = 0;
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SyncClientUpgradeRequiredException)
            {
                failures = 8;
                await Task.Delay(TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                failures = Math.Min(failures + 1, 8);
                var seconds = Math.Min(300, 5 * (1 << Math.Min(failures - 1, 6)));
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
