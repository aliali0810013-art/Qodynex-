using System.Net;
using System.Net.Http.Json;
using Accounting.SharedKernel;

namespace Accounting.Sync.Client;

public interface ISyncTransport
{
    Task<SyncPushResult> PushAsync(SyncPushBatch batch, CancellationToken cancellationToken = default);
    Task<SyncPullResult> PullAsync(SyncPullRequest request, CancellationToken cancellationToken = default);
}

public sealed class SyncTransportException : HttpRequestException
{
    public HttpStatusCode StatusCodeValue { get; }
    public string ResponseBody { get; }

    public SyncTransportException(HttpStatusCode statusCode, string responseBody)
        : base($"Sync server returned {(int)statusCode} {statusCode}. {responseBody}", null, statusCode)
    {
        StatusCodeValue = statusCode;
        ResponseBody = responseBody;
    }
}

public sealed class HttpSyncTransport : ISyncTransport
{
    private readonly HttpClient _httpClient;
    public HttpSyncTransport(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<SyncPushResult> PushAsync(SyncPushBatch batch, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("/api/v1/sync/push", batch, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<SyncPushResult>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncPullResult> PullAsync(SyncPullRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("/api/v1/sync/pull", request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<SyncPullResult>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new SyncTransportException(response.StatusCode, body);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Sync server returned an empty response.");
    }
}
