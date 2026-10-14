using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace osu.Game.Rulesets.BeatmapAccel.Compatibility.Windows;

internal sealed class WindowsBeatmapAccelPlatformRuntime : BeatmapAccelPlatformRuntimeBase
{
    private static readonly System.Reflection.PropertyInfo? connectCallbackProperty = typeof(SocketsHttpHandler).GetProperty("ConnectCallback");

    private static readonly Lazy<bool> hasSystemProxyLazy = new Lazy<bool>(() =>
    {
        try
        {
            // HttpClient.DefaultProxy resolves the system proxy (WinINET) exactly like osu-framework's WebRequest stack.
            // When no proxy is configured it returns a proxy instance whose GetProxy() yields null for every URI.
            return HttpClient.DefaultProxy?.GetProxy(new Uri("https://osu.ppy.sh/")) != null;
        }
        catch
        {
            return false;
        }
    });

    public override string Name => "windows";

    public override bool HasSystemProxy => hasSystemProxyLazy.Value;

    public override long NextInt64(long minInclusive, long maxExclusive)
        => Random.Shared.NextInt64(minInclusive, maxExclusive);

    public override void NextBytes(byte[] buffer)
        => Random.Shared.NextBytes(buffer);

    public override async Task<PreferredIpHttpProbeResponse?> ProbePreferredIpHttpAsync(PreferredIpHttpProbeRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.ConnectTimeout);

        // 探针必须直连候选 IP（forceDirectConnection: true）。若走系统代理，所有候选都会变成
        // "同一条代理连接"，测出来的数字彼此等价，优选就失去意义了。
        using var handler = createHandler(request.PreferredIp, allowAutoRedirect: false, DecompressionMethods.None, request.ConnectTimeout,
            forceDirectConnection: true);
        using var client = CreateHttpClient(handler);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"https://{request.Host}{normalizePath(request.PathAndQuery)}");
        SetRequestHeader(httpRequest, "User-Agent", request.UserAgent);

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using HttpResponseMessage response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            string? colo = readCloudflareColo(response);
            long bodyBytes = 0;
            TimeSpan bodyDuration = TimeSpan.Zero;

            // 只管读够需要的字节；读满就断开，避免把整个文件拖下来。
            if (request.MaxBodyBytes > 0 && (int)response.StatusCode < 400)
                (bodyBytes, bodyDuration) = await readCappedBodyAsync(response, request.MaxBodyBytes, request.BodyTimeout, cancellationToken).ConfigureAwait(false);

            return new PreferredIpHttpProbeResponse(response.StatusCode, stopwatch.Elapsed, colo, bodyBytes, bodyDuration);
        }
        catch
        {
            stopwatch.Stop();
            return null;
        }
    }

    public override async Task DownloadFileAsync(PreferredIpFileDownloadRequest request, CancellationToken cancellationToken)
    {
        using var handler = createHandler(request.PreferredIp, allowAutoRedirect: true, DecompressionMethods.None, request.ConnectTimeout,
            request.ForceDirectConnection);
        using var client = CreateHttpClient(handler);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);

        foreach (BeatmapAccelHttpHeader header in request.Headers)
            SetRequestHeader(httpRequest, header.Name, header.Value);

        using HttpResponseMessage response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await ReadHttpStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
            throw new PreferredIpDownloadHttpException(response.StatusCode, response.ReasonPhrase, body);
        }

        await CopyHttpContentToFileAsync(response.Content, request.DestinationPath, cancellationToken, request.Progress).ConfigureAwait(false);
    }

    private SocketsHttpHandler createHandler(IPAddress? preferredIp, bool allowAutoRedirect, DecompressionMethods decompressionMethods, TimeSpan connectTimeout, bool forceDirectConnection)
    {
        SocketsHttpHandler handler = CreatePreferredIpHttpHandler(new PreferredIpHttpHandlerOptions(
            preferredIp,
            allowAutoRedirect,
            decompressionMethods,
            connectTimeout,
            PooledConnectionLifetime: TimeSpan.Zero,
            PooledConnectionIdleTimeout: TimeSpan.Zero));

        // 没有优选 IP 时保持 osu!lazer 默认连接路径。
        if (preferredIp == null || connectCallbackProperty == null)
            return handler;

        // 系统代理存在且未授权直连时，直连优选 IP 会绕过代理而失败，保持默认路径（走代理）。
        if (HasSystemProxy && !forceDirectConnection)
            return handler;

        // 关键：只要决定直连优选 IP，就必须显式关掉代理。
        // SocketsHttpHandler.Proxy 留空时会回落到 HttpClient.DefaultProxy（系统代理），
        // 此时请求会先向代理发 CONNECT 隧道，ConnectCallback 起不到"连到指定 IP"的作用。
        handler.UseProxy = false;

        var callback = new Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>((context, cancellationToken) => connectPreferredIpAsync(preferredIp, context, cancellationToken));
        connectCallbackProperty.SetValue(handler, callback);
        return handler;
    }

    private async ValueTask<Stream> connectPreferredIpAsync(IPAddress preferredIp, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(preferredIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await ConnectSocketAsync(socket, preferredIp, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<(long Bytes, TimeSpan Duration)> readCappedBodyAsync(HttpResponseMessage response, long maxBytes, TimeSpan? bodyTimeout, CancellationToken cancellationToken)
    {
        long total = 0;
        var buffer = new byte[64 * 1024];

        using var bodyTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyTimeoutSource.CancelAfter(bodyTimeout ?? TimeSpan.FromSeconds(5));

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using Stream stream = await response.Content.ReadAsStreamAsync(bodyTimeoutSource.Token).ConfigureAwait(false);

            while (total < maxBytes)
            {
                int toRead = (int)Math.Min(buffer.Length, maxBytes - total);
                int read = await stream.ReadAsync(buffer, 0, toRead, bodyTimeoutSource.Token).ConfigureAwait(false);

                if (read <= 0)
                    break;

                total += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 读取超时：保留已读到的字节，用部分数据估算吞吐 —— "慢就是慢"，正是我们要的结论。
        }
        catch (IOException)
        {
        }
        catch (HttpRequestException)
        {
        }

        stopwatch.Stop();
        return (total, stopwatch.Elapsed);
    }

    private static string? readCloudflareColo(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("CF-RAY", out IEnumerable<string>? values))
            return null;

        foreach (string value in values)
        {
            string? colo = extractColo(value);
            if (colo != null)
                return colo;
        }

        return null;
    }

    /// <summary>
    /// 从 CF-RAY（形如 <c>8f2a1b3c4d5e6f70-NRT</c>）里取落地节点代码。
    /// 这是 Cloudflare 免费给出的、100% 准确的节点标识，可以在下载任何数据之前就把绕远的节点筛掉。
    /// </summary>
    private static string? extractColo(string cfRay)
    {
        int separator = cfRay.LastIndexOf('-');

        if (separator < 0 || cfRay.Length - separator - 1 != 3)
            return null;

        string colo = cfRay[(separator + 1)..];

        foreach (char c in colo)
        {
            if (c is < 'A' or > 'Z')
                return null;
        }

        return colo;
    }

    private static string normalizePath(string pathAndQuery)
        => string.IsNullOrWhiteSpace(pathAndQuery) ? "/" : pathAndQuery;
}
