using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets.BeatmapAccel.Compatibility;
using osu.Game.Rulesets.BeatmapAccel.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace osu.Game.Rulesets.BeatmapAccel.Features.Download;

/// <summary>
/// 优选 IP 测速与选择。
///
/// 三级筛选，顺序很关键：
///   ① TCP 连通性  —— 先把根本连不上的候选扔掉（大陆到 Cloudflare 的候选里通常只有约 20% 能连上）。
///   ② osu.ppy.sh 复测 —— 判掉 4xx（关键！必然 403 的节点以前会被当成健康候选），
///                        同时从 CF-RAY 读出 Cloudflare 落地节点代码。
///   ③ 真实吞吐测速 —— 在**同一个 IP** 上拉一小段数据量实际带宽。
///
/// 为什么不能只用延迟：实测同一条成都电信线路，
///   172.64.229.x → NRT（东京）连接 0.086s / 吞吐 6600 KB/s
///   172.64.228.x → SIN（新加坡）连接 0.077s（更快！）/ 吞吐 33 KB/s 且必然 403
/// 延迟更快的那一个恰恰是不可用的那个。下载速度由吞吐决定，所以要按吞吐选。
/// </summary>
public static class CloudflareSpeedTestManager
{
    private const string probe_host = "osu.ppy.sh";
    private const string speed_test_user_agent = "BeatmapAccel-SpeedTest";

    private const int ipv4_samples_per_range = 2;
    private const int ipv4_priority_samples_per_range = 8;
    private const int ipv6_samples_per_range = 1;

    /// <summary>进入 ② 阶段（HTTP 复测）的候选数量，按 TCP 延迟取前 N。</summary>
    private const int http_probe_count = 20;

    /// <summary>进入 ③ 阶段（吞吐测速）的候选数量。</summary>
    private const int throughput_probe_count = 6;

    /// <summary>同一个落地节点最多送几个候选去做吞吐测速，避免把机会全花在一条绕远路径上。</summary>
    private const int max_per_colo_in_throughput_stage = 2;

    /// <summary>
    /// 吞吐测速每个候选最多拉取的数据量。
    /// 512 KB 时测出的值严重虚高：同一 IP 实测 512 KB 样本 → 1526 KB/s，10 MB → 只有 383 KB/s（差 4 倍）。
    /// 原因是小样本几乎全落在 TCP 慢启动后的突发窗口里，TTFB 也被摊得极薄。
    /// 提高到 3 MB 让测量窗口至少覆盖数秒的稳定传输。
    /// </summary>
    private const int throughput_max_bytes = 3 * 1024 * 1024;

    /// <summary>少于这个字节数的吞吐测量视为无效（例如端点被限流只回了一个错误页）。</summary>
    private const long throughput_minimum_bytes = 32 * 1024;

    /// <summary>吞吐测速端点：优先 Cloudflare 官方测速端点，退化到 osu 自己的 CDN。</summary>
    private static readonly string[] default_throughput_probe_urls =
    {
        "https://speed.cloudflare.com/__down?bytes=3145728",
        "https://b.ppy.sh/preview/1.mp3",
    };

    /// <summary>启动时若缓存优选 IP 的吞吐低于这个值，说明路径已经明显退化，改为完整重测。</summary>
    private const double keep_cached_min_throughput_kbs = 128;

    /// <summary>缓存优选 IP 的有效期。</summary>
    private const int preferred_ip_cache_hours = 12;

    /// <summary>
    /// 吞吐测量的采集窗口上限。实际样本量 = min(throughput_max_bytes, 本值 × 实时吞吐)：
    /// 慢链路靠时间兜住（4 秒 ≈ 1.5 MB @ 383 KB/s），快链路靠字节数兜住，
    /// 两边都能拿到足够长的稳定传输窗口，而不是只测到一个突发尖峰。
    /// </summary>
    private static readonly TimeSpan throughput_body_timeout = TimeSpan.FromSeconds(4);


    private static readonly string[] cloudflare_priority_ipv4_ranges =
    {
        // 实测更容易落到亚洲（NRT/SIN）节点的网段，采样密度更高。
        "172.64.224.0/22",
        "172.64.228.0/22",
        "172.64.229.0/24",
    };

    private static readonly string[] cloudflare_ipv4_ranges =
    {
        "173.245.48.0/20",
        "103.21.244.0/22",
        "103.22.200.0/22",
        "103.31.4.0/22",
        "141.101.64.0/18",
        "108.162.192.0/18",
        "190.93.240.0/20",
        "188.114.96.0/20",
        "197.234.240.0/22",
        "198.41.128.0/17",
        "162.158.0.0/15",
        "104.16.0.0/12",
        "172.64.0.0/17",
        "172.64.128.0/18",
        "172.64.192.0/19",
        "172.64.224.0/22",
        "172.64.229.0/24",
        "172.64.230.0/23",
        "172.64.232.0/21",
        "172.64.240.0/21",
        "172.64.248.0/21",
        "172.65.0.0/16",
        "172.66.0.0/16",
        "172.67.0.0/16",
        "131.0.72.0/22",
    };

    private static readonly string[] cloudflare_ipv6_ranges =
    {
        "2400:cb00::/32",
        "2400:cb00:2049::/48",
        "2400:cb00:f00e::/48",
        "2606:4700::/32",
        "2606:4700:10::/48",
        "2606:4700:130::/48",
        "2606:4700:3000::/48",
        "2606:4700:3001::/48",
        "2606:4700:3002::/48",
        "2606:4700:3003::/48",
        "2606:4700:3004::/48",
        "2606:4700:3005::/48",
        "2606:4700:3006::/48",
        "2606:4700:3007::/48",
        "2606:4700:3008::/48",
        "2606:4700:3009::/48",
        "2606:4700:3010::/48",
        "2606:4700:3011::/48",
        "2606:4700:3012::/48",
        "2606:4700:3013::/48",
        "2606:4700:3014::/48",
        "2606:4700:3015::/48",
        "2606:4700:3016::/48",
        "2606:4700:3017::/48",
        "2606:4700:3018::/48",
        "2606:4700:3019::/48",
        "2606:4700:3020::/48",
        "2606:4700:3021::/48",
        "2606:4700:3022::/48",
        "2606:4700:3023::/48",
        "2606:4700:3024::/48",
        "2606:4700:3025::/48",
        "2606:4700:3026::/48",
        "2606:4700:3027::/48",
        "2606:4700:3028::/48",
        "2606:4700:3029::/48",
        "2606:4700:3030::/48",
        "2606:4700:3031::/48",
        "2606:4700:3032::/48",
        "2606:4700:3033::/48",
        "2606:4700:3034::/48",
        "2606:4700:3035::/48",
        "2606:4700:3036::/48",
        "2606:4700:3037::/48",
        "2606:4700:3038::/48",
        "2606:4700:3039::/48",
        "2606:4700:a0::/48",
        "2606:4700:a1::/48",
        "2606:4700:a8::/48",
        "2606:4700:a9::/48",
        "2606:4700:a::/48",
        "2606:4700:b::/48",
        "2606:4700:c::/48",
        "2606:4700:d0::/48",
        "2606:4700:d1::/48",
        "2606:4700:d::/48",
        "2606:4700:e0::/48",
        "2606:4700:e1::/48",
        "2606:4700:e2::/48",
        "2606:4700:e3::/48",
        "2606:4700:e4::/48",
        "2606:4700:e5::/48",
        "2606:4700:e6::/48",
        "2606:4700:e7::/48",
        "2606:4700:e::/48",
        "2606:4700:f1::/48",
        "2606:4700:f2::/48",
        "2606:4700:f3::/48",
        "2606:4700:f4::/48",
        "2606:4700:f5::/48",
        "2606:4700:f::/48",
        "2803:f800::/32",
        "2803:f800:50::/48",
        "2803:f800:51::/48",
        "2405:b500::/32",
        "2405:8100::/32",
        "2a06:98c0::/29",
        "2a06:98c1:3100::/48",
        "2a06:98c1:3101::/48",
        "2a06:98c1:3102::/48",
        "2a06:98c1:3103::/48",
        "2a06:98c1:3104::/48",
        "2a06:98c1:3105::/48",
        "2a06:98c1:3106::/48",
        "2a06:98c1:3107::/48",
        "2a06:98c1:3108::/48",
        "2a06:98c1:3109::/48",
        "2a06:98c1:310a::/48",
        "2a06:98c1:310b::/48",
        "2a06:98c1:310c::/48",
        "2a06:98c1:310d::/48",
        "2a06:98c1:310e::/48",
        "2a06:98c1:310f::/48",
        "2a06:98c1:3120::/48",
        "2a06:98c1:3121::/48",
        "2a06:98c1:3122::/48",
        "2a06:98c1:3123::/48",
        "2a06:98c1:3200::/48",
        "2a06:98c1:50::/48",
        "2a06:98c1:51::/48",
        "2a06:98c1:54::/48",
        "2a06:98c1:58::/48",
        "2c0f:f248::/32",
    };

    private static readonly SemaphoreSlim switchLock = new(1, 1);
    private static readonly object failureRecoveryLock = new();
    private static readonly IBeatmapAccelPlatformRuntime runtime = BeatmapAccelCompatibility.Current;
    private static readonly TimeSpan default_tcp_probe_timeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan mobile_tcp_probe_timeout = TimeSpan.FromMilliseconds(3000);
    private static readonly TimeSpan mobile_tcp_probe_retry_timeout = TimeSpan.FromMilliseconds(5000);
    private static readonly TimeSpan default_http_probe_timeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan mobile_http_probe_timeout = TimeSpan.FromMilliseconds(5000);
    private static readonly TimeSpan default_throughput_probe_timeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan mobile_throughput_probe_timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan default_speed_test_timeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan mobile_speed_test_timeout = TimeSpan.FromSeconds(60);

    private static int startupSwitchTriggered;
    private static int failureRecoveryTriggered;
    private static int failureRecoveryKeepCurrentSkipCount;

    private const int failure_detail_limit = 3;
    private const int mobile_retry_candidate_limit = 8;
    // 同一优选 IP 连续 N 次"探测存活却仍下载失败"后，强制走完整切换，避免 keep-current 探测造成死锁。
    private const int failure_recovery_force_switch_after = 3;

    public static Action<Action>? ScheduleToMainThread { get; set; }
    public static Action<Notification>? NotificationPoster { get; set; }

    public static string GetPreferredIp()
        => BeatmapAccelRulesetConfigManager.Instance?.GetPreferredIp() ?? string.Empty;

    public static string GetLastSummary()
        => BeatmapAccelRulesetConfigManager.Instance?.GetLastSpeedTestSummary() ?? "尚未测速";

    public static void BeginStartupSpeedTest()
    {
        if (Interlocked.Exchange(ref startupSwitchTriggered, 1) != 0)
            return;

        var config = BeatmapAccelRulesetConfigManager.Instance;

        if (config == null || !config.GetAutoSwitchOnStartup())
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                BeatmapAccelLogging.Log($"Startup speed test running on {runtime.Name} runtime strategy.");
                SpeedTestSelectionResult result = await SwitchToFastestIpAsync(SpeedTestTrigger.Startup).ConfigureAwait(false);

                if (!result.Success)
                    BeatmapAccelLogging.Log($"Startup preferred-IP speed test failed: {result.Message}");
                else
                    BeatmapAccelLogging.Log($"Startup preferred-IP speed test selected {result.SelectedIp}. {result.Message}");
            }
            catch (Exception e)
            {
                BeatmapAccelLogging.LogError(e, "Startup speed test threw an unexpected exception.");
            }
        });
    }

    public static void BeginFailureRecoverySpeedTest(Action<Notification>? postNotification = null)
    {
        if (Interlocked.Exchange(ref failureRecoveryTriggered, 1) != 0)
            return;

        string preferredIp;

        try
        {
            var config = BeatmapAccelRulesetConfigManager.Instance;

            if (config == null || !config.GetAutoSwitchOnDownloadFailure())
            {
                Interlocked.Exchange(ref failureRecoveryTriggered, 0);
                return;
            }

            preferredIp = config.GetPreferredIp();
        }
        catch (Exception e)
        {
            // 前言阶段抛异常必须复位单飞标志，否则 failureRecoveryTriggered 永久卡在 1，后续恢复全部失效。
            Interlocked.Exchange(ref failureRecoveryTriggered, 0);
            BeatmapAccelLogging.LogError(e, "Failed to begin download failure recovery.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                int skipCount;
                bool shouldForceSwitch;

                lock (failureRecoveryLock)
                {
                    skipCount = failureRecoveryKeepCurrentSkipCount;
                    shouldForceSwitch = skipCount + 1 >= failure_recovery_force_switch_after;
                }

                // 小探测通过不代表大文件持续传输也能成功（移动端常见"传一半被掐"）。
                // 仅在尚未触及强制切换阈值时探测当前 IP：探测存活则跳过切换并累计跳过次数；
                // 达到阈值后不再做无谓探测，直接走完整切换，避免死循环。
                bool probeSucceeded;

                if (!shouldForceSwitch)
                {
                    using var probeTimeout = new CancellationTokenSource();
                    probeTimeout.CancelAfter(getTcpProbeTimeout() + getHttpProbeTimeout() + TimeSpan.FromSeconds(1));
                    probeSucceeded = await tryKeepCurrentPreferredIpAsync(preferredIp, probeTimeout.Token).ConfigureAwait(false);
                }
                else
                {
                    BeatmapAccelLogging.Log($"Current preferred IP {preferredIp} survived {skipCount} consecutive recovery probes while downloads keep failing; forcing a full switch.");
                    probeSucceeded = false;
                }

                if (probeSucceeded)
                {
                    int newSkipCount;
                    lock (failureRecoveryLock)
                        newSkipCount = ++failureRecoveryKeepCurrentSkipCount;

                    string message = $"当前优选 IP {preferredIp} 探测可用，已跳过切换（第 {newSkipCount} 次，连续 {failure_recovery_force_switch_after} 次后将强制切换）。";
                    BeatmapAccelLogging.Log($"Download failure recovery skipped. {message}");
                    postNotificationOrFallback(new SimpleNotification
                    {
                        Text = $"BeatmapAccel: {message}"
                    }, postNotification);
                    return;
                }

                // 探测失败（"探测存活"连续被打断）或已达阈值强制切换：重置跳过计数，让切换后从新 IP 重新起算。
                lock (failureRecoveryLock)
                    failureRecoveryKeepCurrentSkipCount = 0;

                BeatmapAccelLogging.Log($"Failure recovery speed test running on {runtime.Name} runtime strategy.");
                SpeedTestSelectionResult result = await SwitchToFastestIpAsync(SpeedTestTrigger.DownloadFailure).ConfigureAwait(false);

                if (result.Success)
                {
                    BeatmapAccelLogging.Log($"Download failure recovery speed test selected {result.SelectedIp}. {result.Message}");
                    postNotificationOrFallback(new SimpleNotification
                    {
                        Text = $"BeatmapAccel: 下载失败后已切换为 {result.SelectedIp}。\n{result.Message}"
                    }, postNotification);
                }
                else
                {
                    BeatmapAccelLogging.Log($"Download failure recovery speed test failed: {result.Message}");
                    postNotificationOrFallback(new SimpleNotification
                    {
                        Text = $"BeatmapAccel: 下载失败后重测速失败。{result.Message}"
                    }, postNotification);
                }
            }
            catch (Exception e)
            {
                // finally 会复位单飞标志；此处吞掉异常避免 fire-and-forget 的未观察任务异常。
                BeatmapAccelLogging.LogError(e, "Download failure recovery speed test threw an unexpected exception.");
            }
            finally
            {
                Interlocked.Exchange(ref failureRecoveryTriggered, 0);
            }
        });
    }

    /// <summary>
    /// 一次下载成功说明当前优选 IP 可用，重置"探测存活却下载失败"的连续计数。
    /// </summary>
    public static void OnDownloadSucceeded()
    {
        lock (failureRecoveryLock)
        {
            if (failureRecoveryKeepCurrentSkipCount != 0)
                failureRecoveryKeepCurrentSkipCount = 0;
        }
    }

    public static async Task<SpeedTestSelectionResult> SwitchToFastestIpAsync(SpeedTestTrigger trigger, INotificationOverlay? notifications = null, CancellationToken cancellationToken = default)
    {
        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(getOverallSpeedTestTimeout());

        await switchLock.WaitAsync(overallTimeout.Token).ConfigureAwait(false);

        try
        {
            var config = BeatmapAccelRulesetConfigManager.Instance;

            if (config == null)
                return new SpeedTestSelectionResult(false, string.Empty, "测速配置尚未初始化。");

            Stopwatch stopwatch = Stopwatch.StartNew();

            // 启动时若缓存仍然健康就直接沿用：频繁重选本身就是"不稳定"的来源之一，
            // 而且每次启动都全网扫一遍会白白占用带宽。
            if (trigger == SpeedTestTrigger.Startup)
            {
                SpeedTestSelectionResult? cached = await tryReuseCachedSelectionAsync(config, overallTimeout.Token).ConfigureAwait(false);

                if (cached is { } reuse)
                {
                    BeatmapAccelLogging.Log($"Startup speed test reused cached selection. {reuse.Message}");
                    return await finishAsync(config, notifications, trigger, reuse).ConfigureAwait(false);
                }
            }

            List<ProbeCandidate> candidates = buildCandidates(config.GetPreferredIp());
            BeatmapAccelLogging.Log($"Speed test built {candidates.Count} candidates on {runtime.Name} runtime strategy.");
            List<TcpProbeResult> tcpResults = await probeTcpCandidatesAsync(candidates, overallTimeout.Token).ConfigureAwait(false);

            if (tcpResults.Count == 0 && runtime.PreferConservativeNetworking)
            {
                BeatmapAccelLogging.Log("No TCP speed test candidates succeeded on conservative runtime. Retrying with reduced concurrency and longer timeout.");
                tcpResults = await probeTcpCandidatesAsync(candidates.Take(mobile_retry_candidate_limit), overallTimeout.Token, 1, mobile_tcp_probe_retry_timeout).ConfigureAwait(false);
            }

            if (tcpResults.Count == 0)
            {
                stopwatch.Stop();
                return await finishAsync(config, notifications, trigger, new SpeedTestSelectionResult(false, string.Empty, buildFailureSummary(trigger, candidates.Count, stopwatch.Elapsed, candidates))).ConfigureAwait(false);
            }

            List<TcpProbeResult> finalists = tcpResults.OrderBy(result => result.Latency).Take(http_probe_count).ToList();
            List<HttpProbeResult> httpResults = await probeHttpCandidatesAsync(finalists, overallTimeout.Token).ConfigureAwait(false);

            if (httpResults.Count == 0)
            {
                stopwatch.Stop();
                return await finishAsync(config, notifications, trigger,
                    new SpeedTestSelectionResult(false, string.Empty, buildHttpFailureSummary(trigger, candidates.Count, tcpResults.Count, stopwatch.Elapsed, finalists))).ConfigureAwait(false);
            }

            List<HttpProbeResult> throughputCandidates = pickThroughputCandidates(httpResults);
            List<ThroughputProbeResult> throughputResults = await probeThroughputAsync(throughputCandidates, overallTimeout.Token).ConfigureAwait(false);

            stopwatch.Stop();

            ThroughputProbeResult? winnerThroughput = throughputResults
                                                     .Where(result => result.Bytes >= throughput_minimum_bytes)
                                                     .OrderByDescending(result => result.Kbps)
                                                     .ThenBy(result => result.Candidate.TcpLatency)
                                                     .FirstOrDefault();

            // 吞吐端点整体不可用（例如被限流）时退化为按 TTFB 排序 —— 至少 4xx 与绕远节点已经在 ② 阶段被剔除。
            HttpProbeResult winnerHttp = winnerThroughput?.Candidate
                                         ?? httpResults.OrderBy(result => result.HttpLatency).ThenBy(result => result.TcpLatency).First();

            string winnerColo = winnerThroughput?.Candidate.Colo ?? winnerHttp.Colo ?? string.Empty;

            await runOnMainThreadAsync(() => config.SetPreferredIpSelection(winnerHttp.Ip, winnerColo, DateTimeOffset.UtcNow.ToUnixTimeSeconds())).ConfigureAwait(false);
            resetFailureRecoverySkipCount();

            return await finishAsync(config, notifications, trigger,
                new SpeedTestSelectionResult(true, winnerHttp.Ip,
                    buildSuccessSummary(trigger, candidates.Count, tcpResults.Count, httpResults.Count, throughputResults.Count, stopwatch.Elapsed, winnerHttp, winnerThroughput))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (overallTimeout.IsCancellationRequested)
        {
            return new SpeedTestSelectionResult(false, string.Empty, $"测速超时：在 {getOverallSpeedTestTimeout().TotalSeconds:F0}s 内未能完成。");
        }
        finally
        {
            switchLock.Release();
        }
    }

    /// <summary>
    /// 启动时的"沿用缓存"快路径：缓存未过期，且当前优选 IP 仍然 TCP 可连、
    /// osu.ppy.sh 不再返回 4xx（例如历史版本可能存下的 403 节点）、吞吐没有明显退化时直接沿用。
    /// </summary>
    private static async Task<SpeedTestSelectionResult?> tryReuseCachedSelectionAsync(BeatmapAccelRulesetConfigManager config, CancellationToken cancellationToken)
    {
        string preferredIp = config.GetPreferredIp();
        long selectedAtUnix = config.GetPreferredIpSelectedAtUnix();

        if (!IPAddress.TryParse(preferredIp, out _) || selectedAtUnix <= 0)
            return null;

        TimeSpan age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(selectedAtUnix);

        if (age < TimeSpan.Zero || age > TimeSpan.FromHours(preferred_ip_cache_hours))
            return null;

        var candidate = new ProbeCandidate("cached", preferredIp, true);
        TcpProbeResult tcpResult = await probeTcpAsync(candidate, getTcpProbeTimeout(), cancellationToken).ConfigureAwait(false);

        if (!tcpResult.Success)
            return null;

        HttpProbeResult? httpResult = await probeHttpAsync(tcpResult, cancellationToken).ConfigureAwait(false);

        if (httpResult == null)
            return null;

        ThroughputProbeResult? throughput = await probeThroughputForCandidateAsync(httpResult, cancellationToken).ConfigureAwait(false);

        if (throughput != null && throughput.Kbps < keep_cached_min_throughput_kbs)
        {
            BeatmapAccelLogging.Log($"Cached preferred IP {preferredIp} measured only {throughput.Kbps:F0} KB/s; running a full speed test instead.");
            return null;
        }

        await runOnMainThreadAsync(() => config.SetPreferredIpSelection(preferredIp, httpResult.Colo ?? config.GetPreferredIpColo(), DateTimeOffset.UtcNow.ToUnixTimeSeconds())).ConfigureAwait(false);
        resetFailureRecoverySkipCount();

        string throughputText = throughput == null
            ? "未测得吞吐"
            : $"{throughput.Kbps:F0} KB/s";

        return new SpeedTestSelectionResult(true, preferredIp,
            $"启动沿用上次优选 IP {preferredIp}（节点 {describeColo(httpResult.Colo)}，TCP {tcpResult.Latency.TotalMilliseconds:F0} ms，"
            + $"TTFB {httpResult.HttpLatency.TotalMilliseconds:F0} ms，吞吐 {throughputText}，缓存有效期内）。");
    }

    private static async Task<SpeedTestSelectionResult> finishAsync(BeatmapAccelRulesetConfigManager config, INotificationOverlay? notifications, SpeedTestTrigger trigger, SpeedTestSelectionResult result)
    {
        await runOnMainThreadAsync(() => config.SetLastSpeedTestSummary(result.Message)).ConfigureAwait(false);

        if (trigger == SpeedTestTrigger.Manual)
        {
            var notification = new SimpleNotification
            {
                Text = result.Success
                    ? $"BeatmapAccel: 当前已切换为 {result.SelectedIp}。\n{result.Message}"
                    : $"BeatmapAccel: 测速失败。\n{result.Message}"
            };

            if (notifications != null)
                await runOnMainThreadAsync(() => postNotificationOrFallback(notification, notifications.Post)).ConfigureAwait(false);
            else
                await runOnMainThreadAsync(() => postNotificationOrFallback(notification)).ConfigureAwait(false);
        }

        return result;
    }

    private static void resetFailureRecoverySkipCount()
    {
        lock (failureRecoveryLock)
            failureRecoveryKeepCurrentSkipCount = 0;
    }

    private static async Task<bool> tryKeepCurrentPreferredIpAsync(string preferredIp, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(preferredIp, out _))
            return false;

        var candidate = new ProbeCandidate("current", preferredIp, true);
        TcpProbeResult tcpResult = await probeTcpAsync(candidate, getTcpProbeTimeout(), cancellationToken).ConfigureAwait(false);

        if (!tcpResult.Success)
        {
            BeatmapAccelLogging.Log($"Current preferred IP {preferredIp} TCP probe failed before failure recovery: {tcpResult.FailureReason}");
            return false;
        }

        HttpProbeResult? httpResult = await probeHttpAsync(tcpResult, cancellationToken).ConfigureAwait(false);

        if (httpResult == null)
        {
            BeatmapAccelLogging.Log($"Current preferred IP {preferredIp} HTTP probe failed before failure recovery.");
            return false;
        }

        BeatmapAccelLogging.Log($"Current preferred IP {preferredIp} probe succeeded before failure recovery: TCP {tcpResult.Latency.TotalMilliseconds:F0} ms, HTTP {httpResult.HttpLatency.TotalMilliseconds:F0} ms, status {(int)httpResult.StatusCode}.");
        return true;
    }

    private static Task runOnMainThreadAsync(Action action)
    {
        if (ScheduleToMainThread == null)
        {
            action();
            return Task.CompletedTask;
        }

        var completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        ScheduleToMainThread(() =>
        {
            try
            {
                action();
                completionSource.SetResult(null);
            }
            catch (Exception e)
            {
                completionSource.SetException(e);
            }
        });

        return completionSource.Task;
    }

    private static void postNotificationOrFallback(Notification notification, Action<Notification>? preferredPoster = null)
    {
        (preferredPoster ?? NotificationPoster)?.Invoke(notification);
    }

    private static List<ProbeCandidate> buildCandidates(string preferredIp)
    {
        var candidates = new Dictionary<string, ProbeCandidate>(StringComparer.Ordinal);

        if (IPAddress.TryParse(preferredIp, out IPAddress? parsedPreferredIp))
            candidates[preferredIp] = new ProbeCandidate("current", preferredIp, true);

        // 对"命中率高"的网段加权采样：候选池有限，如果每个段只抽 2 个，
        // 唯一能落到东京的那一小段很容易被漏掉。这只是提高命中概率，最终选谁仍由实测吞吐决定。
        foreach (string cidr in cloudflare_priority_ipv4_ranges)
        {
            foreach (string ip in sampleIpv4Range(cidr, ipv4_priority_samples_per_range))
                candidates.TryAdd(ip, new ProbeCandidate(cidr, ip, false));
        }

        foreach (string cidr in cloudflare_ipv4_ranges)
        {
            foreach (string ip in sampleIpv4Range(cidr, ipv4_samples_per_range))
                candidates.TryAdd(ip, new ProbeCandidate(cidr, ip, false));
        }

        // Instance 为 null（配置尚未初始化）时按默认值"开启"处理。
        if (BeatmapAccelRulesetConfigManager.Instance?.GetEnableIpv6Candidates() != false)
        {
            foreach (string cidr in cloudflare_ipv6_ranges)
            {
                foreach (string ip in sampleIpv6Range(cidr, ipv6_samples_per_range))
                    candidates.TryAdd(ip, new ProbeCandidate(cidr, ip, false));
            }
        }

        return candidates.Values.ToList();
    }

    private static IEnumerable<string> sampleIpv4Range(string cidr, int sampleCount)
    {
        if (!tryParseCidr(cidr, out uint network, out int prefixLength))
            yield break;

        ulong hostCount = 1UL << (32 - prefixLength);
        ulong minOffset = hostCount > 2 ? 1UL : 0UL;
        ulong maxExclusive = hostCount > 2 ? hostCount - 1UL : hostCount;

        if (maxExclusive <= minOffset)
        {
            yield return formatIpv4(network);
            yield break;
        }

        var offsets = new HashSet<ulong>();

        while (offsets.Count < sampleCount && offsets.Count < (int)(maxExclusive - minOffset))
        {
            long nextOffset = runtime.NextInt64((long)minOffset, (long)maxExclusive);
            if (!offsets.Add((ulong)nextOffset))
                continue;

            yield return formatIpv4(network + (uint)nextOffset);
        }
    }

    private static IEnumerable<string> sampleIpv6Range(string cidr, int sampleCount)
    {
        if (!tryParseIpv6Cidr(cidr, out BigInteger network, out int prefixLength))
            yield break;

        int hostBits = 128 - prefixLength;
        BigInteger maxOffsetExclusive = BigInteger.One << hostBits;
        var yielded = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < sampleCount; i++)
        {
            BigInteger offset = hostBits == 0 ? BigInteger.Zero : randomBigInteger(maxOffsetExclusive);
            string ip = formatIpv6(network + offset);

            if (yielded.Add(ip))
                yield return ip;
        }
    }

    private static async Task<List<TcpProbeResult>> probeTcpCandidatesAsync(IEnumerable<ProbeCandidate> candidates, CancellationToken cancellationToken)
        => await probeTcpCandidatesAsync(candidates, cancellationToken, getTcpProbeConcurrency(), getTcpProbeTimeout()).ConfigureAwait(false);

    private static async Task<List<TcpProbeResult>> probeTcpCandidatesAsync(IEnumerable<ProbeCandidate> candidates, CancellationToken cancellationToken, int maxConcurrency, TimeSpan timeout)
    {
        var results = new ConcurrentBag<TcpProbeResult>();

        await runtime.ForEachAsync(candidates, maxConcurrency, cancellationToken, async (candidate, token) =>
        {
            TcpProbeResult result = await probeTcpAsync(candidate, timeout, token).ConfigureAwait(false);
            if (result.Success)
                results.Add(result);
        }).ConfigureAwait(false);

        return results.ToList();
    }

    private static async Task<TcpProbeResult> probeTcpAsync(ProbeCandidate candidate, TimeSpan timeoutDuration, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(candidate.Ip, out IPAddress? address))
            return new TcpProbeResult(candidate, false, TimeSpan.MaxValue, "invalid ip");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration);

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            await runtime.ConnectSocketAsync(socket, address, 443, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new TcpProbeResult(candidate, true, stopwatch.Elapsed, null);
        }
        catch (Exception e)
        {
            stopwatch.Stop();
            return new TcpProbeResult(candidate, false, TimeSpan.MaxValue, describeProbeError(e));
        }
    }

    private static async Task<List<HttpProbeResult>> probeHttpCandidatesAsync(IEnumerable<TcpProbeResult> finalists, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<HttpProbeResult>();

        await runtime.ForEachAsync(finalists, getHttpProbeConcurrency(), cancellationToken, async (candidate, token) =>
        {
            HttpProbeResult? result = await probeHttpAsync(candidate, token).ConfigureAwait(false);
            if (result != null)
                results.Add(result);
        }).ConfigureAwait(false);

        return results.ToList();
    }

    private static async Task<HttpProbeResult?> probeHttpAsync(TcpProbeResult candidate, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(candidate.Candidate.Ip, out IPAddress? address))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(getHttpProbeTimeout());

        try
        {
            PreferredIpHttpProbeResponse? response = await runtime.ProbePreferredIpHttpAsync(new PreferredIpHttpProbeRequest(
                address,
                probe_host,
                "/",
                speed_test_user_agent,
                getTcpProbeTimeout()), timeout.Token).ConfigureAwait(false);

            if (response == null)
                return null;

            // 4xx 一律视为不可用候选。
            // 这是本次修复的核心之一：旧逻辑只把 >=500 当失败，于是"必然返回 403"的节点会被当成健康候选，
            // 只要它的 TTFB 略快就会被选中 → 下载全挂 → 触发重测 → 又选回来，表现为时好时坏。
            if ((int)response.Value.StatusCode >= 400)
            {
                string failure = $"HTTP {(int)response.Value.StatusCode}（节点 {describeColo(response.Value.Colo)}）";
                candidate.Candidate.LastHttpFailure = failure;
                BeatmapAccelLogging.Log($"HTTP probe rejected {candidate.Candidate.Ip}: {failure}.");
                return null;
            }

            return new HttpProbeResult(candidate.Candidate, candidate.Latency, response.Value.Latency, response.Value.StatusCode, response.Value.Colo, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            candidate.Candidate.LastHttpFailure = "timeout";
            return null;
        }
        catch (Exception e)
        {
            string failure = describeProbeError(e);
            candidate.Candidate.LastHttpFailure = failure;
            BeatmapAccelLogging.Log($"HTTP probe failed for {candidate.Candidate.Ip}: {failure}");
            return null;
        }
    }

    /// <summary>
    /// 从 ② 阶段的幸存者里挑出 ③ 阶段的测速对象：按 Cloudflare 落地节点分组后轮转取样，
    /// 让有限的测速次数摊在"多条不同路径"上，而不是把 6 次机会全花在同一个绕远节点。
    /// </summary>
    private static List<HttpProbeResult> pickThroughputCandidates(List<HttpProbeResult> survivors)
    {
        List<List<HttpProbeResult>> groups = survivors
                                            .GroupBy(result => string.IsNullOrEmpty(result.Colo) ? "?" : result.Colo!)
                                            .OrderBy(group => group.Min(result => result.HttpLatency))
                                            .Select(group => group.OrderBy(result => result.HttpLatency).ToList())
                                            .ToList();

        var picked = new List<HttpProbeResult>();

        for (int round = 0; round < max_per_colo_in_throughput_stage && picked.Count < throughput_probe_count; round++)
        {
            foreach (List<HttpProbeResult> group in groups)
            {
                if (picked.Count >= throughput_probe_count)
                    break;

                if (round < group.Count)
                    picked.Add(group[round]);
            }
        }

        return picked;
    }

    private static async Task<List<ThroughputProbeResult>> probeThroughputAsync(List<HttpProbeResult> candidates, CancellationToken cancellationToken)
    {
        var results = new List<ThroughputProbeResult>();

        if (candidates.Count == 0)
            return results;

        foreach (ThroughputProbeTarget target in buildThroughputProbeTargets())
        {
            var stageResults = new ConcurrentBag<ThroughputProbeResult>();

            await runtime.ForEachAsync(candidates, getThroughputProbeConcurrency(), cancellationToken, async (candidate, token) =>
            {
                ThroughputProbeResult? result = await probeThroughputAsync(candidate, target, token).ConfigureAwait(false);
                if (result != null)
                    stageResults.Add(result);
            }).ConfigureAwait(false);

            results = stageResults.ToList();

            int usable = results.Count(result => result.Bytes >= throughput_minimum_bytes);
            BeatmapAccelLogging.Log($"Throughput probe via {target.Url}: {usable}/{candidates.Count} usable measurement(s).");

            if (usable > 0)
                return results;
        }

        return results;
    }

    private static async Task<ThroughputProbeResult?> probeThroughputForCandidateAsync(HttpProbeResult candidate, CancellationToken cancellationToken)
    {
        foreach (ThroughputProbeTarget target in buildThroughputProbeTargets())
        {
            ThroughputProbeResult? result = await probeThroughputAsync(candidate, target, cancellationToken).ConfigureAwait(false);

            if (result != null && result.Bytes >= throughput_minimum_bytes)
                return result;
        }

        return null;
    }

    private static async Task<ThroughputProbeResult?> probeThroughputAsync(HttpProbeResult candidate, ThroughputProbeTarget target, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(candidate.Ip, out IPAddress? address))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(getThroughputProbeTimeout());

        try
        {
            PreferredIpHttpProbeResponse? response = await runtime.ProbePreferredIpHttpAsync(new PreferredIpHttpProbeRequest(
                address,
                target.Host,
                target.PathAndQuery,
                speed_test_user_agent,
                getTcpProbeTimeout(),
                MaxBodyBytes: throughput_max_bytes,
                BodyTimeout: throughput_body_timeout), timeout.Token).ConfigureAwait(false);

            if (response == null || (int)response.Value.StatusCode >= 400)
                return null;

            if (response.Value.BodyBytes <= 0 || response.Value.BodyDuration <= TimeSpan.Zero)
                return null;

            return new ThroughputProbeResult(candidate, target.Url, response.Value.BodyBytes, response.Value.BodyDuration);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            BeatmapAccelLogging.Log($"Throughput probe failed for {candidate.Ip} via {target.Url}: {describeProbeError(e)}");
            return null;
        }
    }

    private static List<ThroughputProbeTarget> buildThroughputProbeTargets()
    {
        var targets = new List<ThroughputProbeTarget>();
        string configured = BeatmapAccelRulesetConfigManager.Instance?.GetThroughputProbeUrl() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(configured) && tryBuildThroughputProbeTarget(configured, out ThroughputProbeTarget custom))
            targets.Add(custom);

        foreach (string url in default_throughput_probe_urls)
        {
            if (tryBuildThroughputProbeTarget(url, out ThroughputProbeTarget target) && !targets.Any(existing => existing.Url == target.Url))
                targets.Add(target);
        }

        return targets;
    }

    private static bool tryBuildThroughputProbeTarget(string url, out ThroughputProbeTarget target)
    {
        target = new ThroughputProbeTarget(string.Empty, "/", string.Empty);

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || IPAddress.TryParse(uri.Host, out _))
        {
            return false;
        }

        target = new ThroughputProbeTarget(uri.Host, string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery, uri.ToString());
        return true;
    }

    private static bool tryParseCidr(string cidr, out uint network, out int prefixLength)
    {
        network = 0;
        prefixLength = 0;

        string[] parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out IPAddress? ipAddress) || ipAddress.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (!int.TryParse(parts[1], out prefixLength) || prefixLength is < 0 or > 32)
            return false;

        uint value = parseIpv4(ipAddress);
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        network = value & mask;
        return true;
    }

    private static bool tryParseIpv6Cidr(string cidr, out BigInteger network, out int prefixLength)
    {
        network = BigInteger.Zero;
        prefixLength = 0;

        string[] parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out IPAddress? ipAddress) || ipAddress.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        if (!int.TryParse(parts[1], out prefixLength) || prefixLength is < 0 or > 128)
            return false;

        network = parseIpv6(ipAddress);

        if (prefixLength < 128)
        {
            BigInteger hostMask = (BigInteger.One << (128 - prefixLength)) - 1;
            network &= ~hostMask;
        }

        return true;
    }

    private static uint parseIpv4(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();

        return ((uint)bytes[0] << 24)
             | ((uint)bytes[1] << 16)
             | ((uint)bytes[2] << 8)
             | bytes[3];
    }

    private static BigInteger parseIpv6(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        Array.Reverse(bytes);

        byte[] unsignedLittleEndian = new byte[bytes.Length + 1];
        Array.Copy(bytes, unsignedLittleEndian, bytes.Length);
        return new BigInteger(unsignedLittleEndian);
    }

    private static string formatIpv4(uint value)
        => runtime.FormatIpv4(value);

    private static string formatIpv6(BigInteger value)
        => runtime.FormatIpv6(value);

    private static BigInteger randomBigInteger(BigInteger exclusiveMax)
    {
        byte[] bytes = exclusiveMax.ToByteArray();
        BigInteger result;

        do
        {
            runtime.NextBytes(bytes);
            bytes[^1] &= 0x7F;
            result = new BigInteger(bytes);
        }
        while (result >= exclusiveMax);

        return result;
    }

    private static int getTcpProbeConcurrency()
        => runtime.PreferConservativeNetworking ? 4 : 32;

    private static int getHttpProbeConcurrency()
        => runtime.PreferConservativeNetworking ? 2 : 8;

    private static int getThroughputProbeConcurrency()
        => runtime.PreferConservativeNetworking ? 2 : 6;

    private static TimeSpan getTcpProbeTimeout()
        => runtime.PreferConservativeNetworking ? mobile_tcp_probe_timeout : default_tcp_probe_timeout;

    private static TimeSpan getHttpProbeTimeout()
        => runtime.PreferConservativeNetworking ? mobile_http_probe_timeout : default_http_probe_timeout;

    private static TimeSpan getThroughputProbeTimeout()
        => runtime.PreferConservativeNetworking ? mobile_throughput_probe_timeout : default_throughput_probe_timeout;

    private static TimeSpan getOverallSpeedTestTimeout()
        => runtime.PreferConservativeNetworking ? mobile_speed_test_timeout : default_speed_test_timeout;

    private static string buildFailureSummary(SpeedTestTrigger trigger, int candidateCount, TimeSpan elapsed, IEnumerable<ProbeCandidate> candidates)
    {
        string detail = string.Join("；", candidates
                                         .Select(candidate => candidate.LastTcpFailure)
                                         .Where(message => !string.IsNullOrWhiteSpace(message))
                                         .Distinct()
                                         .Take(failure_detail_limit)!);

        if (!string.IsNullOrWhiteSpace(detail))
            BeatmapAccelLogging.Log($"Speed test TCP failures: {detail}");

        return string.IsNullOrWhiteSpace(detail)
            ? $"{describeTrigger(trigger)}：{candidateCount} 个候选 IP 全部 TCP 连接失败，总耗时 {elapsed.TotalSeconds:F1}s"
            : $"{describeTrigger(trigger)}：{candidateCount} 个候选 IP 全部 TCP 连接失败，总耗时 {elapsed.TotalSeconds:F1}s。原因：{detail}";
    }

    private static string buildHttpFailureSummary(SpeedTestTrigger trigger, int candidateCount, int tcpSuccessCount, TimeSpan elapsed, IEnumerable<TcpProbeResult> finalists)
    {
        string detail = string.Join("；", finalists
                                         .Select(result => result.Candidate.LastHttpFailure ?? result.FailureReason)
                                         .Where(message => !string.IsNullOrWhiteSpace(message))
                                         .Distinct()
                                         .Take(failure_detail_limit)!);

        return string.IsNullOrWhiteSpace(detail)
            ? $"{describeTrigger(trigger)}：{candidateCount} 个候选 IP 中 {tcpSuccessCount} 个 TCP 可达，但 HTTP 复测全部失败，总耗时 {elapsed.TotalSeconds:F1}s"
            : $"{describeTrigger(trigger)}：{candidateCount} 个候选 IP 中 {tcpSuccessCount} 个 TCP 可达，但 HTTP 复测全部失败（已剔除返回 4xx 的节点），总耗时 {elapsed.TotalSeconds:F1}s。细节：{detail}";
    }

    private static string buildSuccessSummary(
        SpeedTestTrigger trigger,
        int candidateCount,
        int tcpSuccessCount,
        int httpSuccessCount,
        int throughputCount,
        TimeSpan elapsed,
        HttpProbeResult winner,
        ThroughputProbeResult? throughput)
    {
        string throughputText = throughput != null && throughput.Bytes >= throughput_minimum_bytes
            ? $"吞吐 {throughput.Kbps:F0} KB/s（{throughput.Bytes / 1024} KB / {throughput.Duration.TotalSeconds:F2}s）"
            : "未测得吞吐，已退化为按响应延迟排序";

        return $"{describeTrigger(trigger)}：{candidateCount} 个候选中 {tcpSuccessCount} 个 TCP 可达，"
             + $"{httpSuccessCount} 个 HTTP 复测通过，{throughputCount} 个完成吞吐测速，"
             + $"已选 {winner.Ip}（{winner.Candidate.Cidr}，节点 {describeColo(winner.Colo)}，"
             + $"TCP {winner.TcpLatency.TotalMilliseconds:F0} ms，TTFB {winner.HttpLatency.TotalMilliseconds:F0} ms，"
             + $"{throughputText}），总耗时 {elapsed.TotalSeconds:F1}s";
    }

    private static string describeColo(string? colo)
        => string.IsNullOrWhiteSpace(colo) ? "未知" : colo!;

    private static string describeTrigger(SpeedTestTrigger trigger)
        => trigger switch
        {
            SpeedTestTrigger.Startup => "启动自动测速",
            SpeedTestTrigger.DownloadFailure => "下载失败后自动测速",
            _ => "手动测速"
        };

    private static string describeProbeError(Exception error)
    {
        string message = error switch
        {
            OperationCanceledException => "timeout",
            SocketException socketException => $"socket {socketException.SocketErrorCode}: {socketException.Message}",
            _ => $"{error.GetType().Name}: {error.Message}"
        };

        BeatmapAccelLogging.Log($"Speed test probe error: {message}");
        return message;
    }

    private sealed record ThroughputProbeTarget(string Host, string PathAndQuery, string Url);

    private sealed record ProbeCandidate(string Cidr, string Ip, bool IsCurrent)
    {
        public string? LastTcpFailure { get; set; }

        public string? LastHttpFailure { get; set; }
    }

    private sealed class TcpProbeResult
    {
        public ProbeCandidate Candidate { get; }

        public bool Success { get; }

        public TimeSpan Latency { get; }

        public string? FailureReason { get; }

        public TcpProbeResult(ProbeCandidate candidate, bool success, TimeSpan latency, string? failureReason)
        {
            Candidate = candidate;
            Success = success;
            Latency = latency;
            FailureReason = failureReason;

            if (!Success && !string.IsNullOrWhiteSpace(FailureReason))
                Candidate.LastTcpFailure = FailureReason;
        }
    }

    private sealed record HttpProbeResult(ProbeCandidate Candidate, TimeSpan TcpLatency, TimeSpan HttpLatency, HttpStatusCode StatusCode, string? Colo, string? FailureReason)
    {
        public string Ip => Candidate.Ip;
    }

    /// <summary>某个候选在指定吞吐探针上实测到的带宽。</summary>
    private sealed record ThroughputProbeResult(HttpProbeResult Candidate, string ProbeUrl, long Bytes, TimeSpan Duration)
    {
        public double Kbps => Duration.TotalSeconds > 0 ? Bytes / Duration.TotalSeconds / 1024.0 : 0;
    }
}

public readonly record struct SpeedTestSelectionResult(bool Success, string SelectedIp, string Message);

public enum SpeedTestTrigger
{
    Startup,
    Manual,
    DownloadFailure,
}
