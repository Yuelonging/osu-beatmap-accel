using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;

namespace osu.Game.Rulesets.BeatmapAccel.Compatibility;

internal sealed record BeatmapAccelHttpHeader(string Name, string Value);

/// <summary>
/// 一次"指定 IP + 指定 SNI/Host"的 HTTP 探测请求。
/// </summary>
/// <param name="MaxBodyBytes">
/// 大于 0 时读取响应体（最多这么多字节）并计时，用于测量真实吞吐；
/// 等于 0（默认）只读响应头，用于快速判断可达性与落地节点。
/// </param>
/// <param name="BodyTimeout">读取响应体的独立超时；超时后保留已读字节并按部分数据估算吞吐。</param>
internal sealed record PreferredIpHttpProbeRequest(
    IPAddress PreferredIp,
    string Host,
    string PathAndQuery,
    string UserAgent,
    TimeSpan ConnectTimeout,
    long MaxBodyBytes = 0,
    TimeSpan? BodyTimeout = null);

/// <summary>
/// 探测结果。
/// </summary>
/// <param name="Latency">请求发出到响应头到达（TTFB）。</param>
/// <param name="Colo">Cloudflare 落地节点代码（取自 CF-RAY 尾码），拿不到时为 null。</param>
/// <param name="BodyBytes">实际读到的响应体字节数（MaxBodyBytes 为 0 时恒为 0）。</param>
/// <param name="BodyDuration">读取响应体耗时。</param>
internal readonly record struct PreferredIpHttpProbeResponse(
    HttpStatusCode StatusCode,
    TimeSpan Latency,
    string? Colo = null,
    long BodyBytes = 0,
    TimeSpan BodyDuration = default);

internal sealed record PreferredIpFileDownloadRequest(
    Uri RequestUri,
    IPAddress? PreferredIp,
    string DestinationPath,
    TimeSpan ConnectTimeout,
    IReadOnlyList<BeatmapAccelHttpHeader> Headers,
    Action<long, long?>? Progress = null,
    int MaxRedirects = 5,
    bool ForceDirectConnection = false);

internal sealed class PreferredIpDownloadHttpException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public PreferredIpDownloadHttpException(HttpStatusCode statusCode, string? reasonPhrase, string? body)
        : base($"HTTP {(int)statusCode} {reasonPhrase} {body}".Trim())
    {
        StatusCode = statusCode;
    }
}
