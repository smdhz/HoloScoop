using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HoloScoop.Search;

public sealed class MeilisearchSubtitleSearchService : ISubtitleSearchService
{
    private readonly HttpClient _httpClient;
    private readonly MeilisearchOptions _options;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public MeilisearchSubtitleSearchService(
        HttpClient httpClient,
        IOptions<MeilisearchOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress ??= new Uri(_options.Url.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !_httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async Task IndexAsync(
        IEnumerable<SubtitleSearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var batch = documents as IReadOnlyCollection<SubtitleSearchDocument> ?? documents.ToArray();
        if (batch.Count == 0)
        {
            return;
        }

        await EnsureIndexAsync(cancellationToken);
        var response = await _httpClient.PostAsJsonAsync(
            $"indexes/{IndexName}/documents?primaryKey=id",
            batch,
            cancellationToken);
        await ThrowIfFailedAsync(response, cancellationToken);
        await WaitForTaskAsync(await ReadTaskUidAsync(response, cancellationToken), cancellationToken);
    }

    public async Task ReplaceStreamAsync(
        long streamId,
        IEnumerable<SubtitleSearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (streamId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId));
        }

        await EnsureIndexAsync(cancellationToken);
        var response = await _httpClient.PostAsJsonAsync(
            $"indexes/{IndexName}/documents/delete",
            new { filter = $"streamId = {streamId}" },
            cancellationToken);
        await ThrowIfFailedAsync(response, cancellationToken);
        await WaitForTaskAsync(await ReadTaskUidAsync(response, cancellationToken), cancellationToken);
        await IndexAsync(documents, cancellationToken);
    }

    public async Task<SubtitleSearchResult> SearchAsync(
        string query,
        string? language = null,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        await EnsureIndexAsync(cancellationToken);

        var request = new Dictionary<string, object?>
        {
            ["q"] = query,
            ["offset"] = offset,
            ["limit"] = limit,
            ["attributesToRetrieve"] = new[]
            {
                "id", "streamId", "title", "channelName", "sourceUrl", "language",
                "source", "startMs", "endMs", "text"
            }
        };
        if (!string.IsNullOrWhiteSpace(language))
        {
            request["filter"] = $"language = '{EscapeFilterValue(language)}'";
        }

        var response = await _httpClient.PostAsJsonAsync(
            $"indexes/{IndexName}/search",
            request,
            cancellationToken);
        await ThrowIfFailedAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var hits = new List<SubtitleSearchHit>();
        foreach (var hit in json.RootElement.GetProperty("hits").EnumerateArray())
        {
            var sourceUrl = hit.GetProperty("sourceUrl").GetString()!;
            var startMs = hit.GetProperty("startMs").GetInt64();
            hits.Add(new SubtitleSearchHit(
                hit.GetProperty("id").GetInt64(),
                hit.GetProperty("streamId").GetInt64(),
                hit.GetProperty("title").GetString()!,
                hit.TryGetProperty("channelName", out var channel) && channel.ValueKind != JsonValueKind.Null
                    ? channel.GetString()
                    : null,
                hit.GetProperty("language").GetString()!,
                hit.GetProperty("source").GetString()!,
                startMs,
                hit.GetProperty("endMs").GetInt64(),
                hit.GetProperty("text").GetString()!,
                YouTubeTimestampUrl.Create(sourceUrl, startMs)));
        }

        long? estimated = json.RootElement.TryGetProperty("estimatedTotalHits", out var total)
            ? total.GetInt64()
            : null;
        return new SubtitleSearchResult(hits, offset, limit, estimated);
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken);
        var response = await _httpClient.DeleteAsync($"indexes/{IndexName}/documents", cancellationToken);
        await ThrowIfFailedAsync(response, cancellationToken);
        await WaitForTaskAsync(await ReadTaskUidAsync(response, cancellationToken), cancellationToken);
    }

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var response = await _httpClient.GetAsync($"indexes/{IndexName}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                response = await _httpClient.PostAsJsonAsync(
                    "indexes",
                    new { uid = _options.IndexName, primaryKey = "id" },
                    cancellationToken);
                await ThrowIfFailedAsync(response, cancellationToken);
                await WaitForTaskAsync(await ReadTaskUidAsync(response, cancellationToken), cancellationToken);
            }
            else
            {
                await ThrowIfFailedAsync(response, cancellationToken);
            }
            response.Dispose();

            var settings = await _httpClient.PatchAsJsonAsync(
                $"indexes/{IndexName}/settings",
                new
                {
                    searchableAttributes = new[] { "text", "title", "channelName" },
                    filterableAttributes = new[] { "language", "source", "streamId" },
                    sortableAttributes = new[] { "startMs" }
                },
                cancellationToken);
            await ThrowIfFailedAsync(settings, cancellationToken);
            await WaitForTaskAsync(await ReadTaskUidAsync(settings, cancellationToken), cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task WaitForTaskAsync(long taskUid, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TaskTimeout);
        while (true)
        {
            var response = await _httpClient.GetAsync($"tasks/{taskUid}", timeout.Token);
            await ThrowIfFailedAsync(response, timeout.Token);
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token),
                cancellationToken: timeout.Token);
            var status = json.RootElement.GetProperty("status").GetString();
            if (status == "succeeded")
            {
                return;
            }

            if (status is "failed" or "canceled")
            {
                var error = json.RootElement.TryGetProperty("error", out var errorNode)
                    ? errorNode.ToString()
                    : "unknown error";
                throw new HttpRequestException($"Meilisearch task {taskUid} {status}: {error}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    private static async Task<long> ReadTaskUidAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("taskUid").GetInt64();
    }

    private static async Task ThrowIfFailedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Meilisearch returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body}",
            null,
            response.StatusCode);
    }

    private string IndexName => Uri.EscapeDataString(_options.IndexName);

    private static string EscapeFilterValue(string value) => value.Replace("'", "\\'");
}
