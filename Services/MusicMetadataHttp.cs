using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MusicPlayer.Services;

/// <summary>Shared host limits for identity searches, photo lookups and retries.</summary>
internal static class MusicMetadataHttp
{
    internal static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private sealed class HostLimit(int milliseconds)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly TimeSpan Interval = TimeSpan.FromMilliseconds(milliseconds);
        public DateTimeOffset NextRequest;
    }
    private static readonly HostLimit MusicBrainz = new(1100);
    private static readonly HostLimit Wikimedia = new(400);
    private static readonly HostLimit Fanart = new(250);
    private static readonly HostLimit AudioDb = new(2100); // Free API: at most 30 requests per minute.
    private static readonly HostLimit AudioDbImages = new(250);
    private static readonly HostLimit Deezer = new(500); // Conservative client limit, not a promised provider quota.
    private static readonly HostLimit DeezerImages = new(250);

    internal static async Task<JsonDocument> JsonAsync(HttpClient client, Uri uri, CancellationToken token, string? apiKey = null) =>
        JsonDocument.Parse(await BytesAsync(client, uri, 4 * 1024 * 1024, token, apiKey).ConfigureAwait(false));

    internal static async Task<byte[]> BytesAsync(HttpClient client, Uri uri, int limit, CancellationToken token, string? apiKey = null)
    {
        var host = uri.Host == "musicbrainz.org" ? MusicBrainz
            : uri.Host == "api.deezer.com" ? Deezer
            : uri.Host is "cdn-images.dzcdn.net" or "e-cdns-images.dzcdn.net" ? DeezerImages
            : uri.Host is "www.theaudiodb.com" or "r2.theaudiodb.com"
                ? uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) ? AudioDb : AudioDbImages
            : uri.Host.EndsWith("fanart.tv", StringComparison.Ordinal) ? Fanart : Wikimedia;
        for (var attempt = 0; ; attempt++)
        {
            await host.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var delay = host.NextRequest - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                host.NextRequest = DateTimeOffset.UtcNow + host.Interval;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("MusicPlayer/1.0 (https://github.com/DaRealTurtyWurty/MusicPlayer)");
                if (apiKey is not null) request.Headers.Add("api-key", apiKey);
                // ResponseHeadersRead ends HttpClient's own timeout at the headers. Bound body reads too.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var requestToken = timeout.Token;
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var retry = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow +
                        (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(ReferenceEquals(host, AudioDb) ? 60 : 5));
                    if (retry > host.NextRequest) host.NextRequest = retry;
                    if (attempt == 0) continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Image service response is too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(chunk, requestToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > limit) throw new InvalidDataException("Image service response is too large.");
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
            finally { host.Gate.Release(); }
        }
    }
}
