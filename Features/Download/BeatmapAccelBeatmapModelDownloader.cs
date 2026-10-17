using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Extensions;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets.BeatmapAccel.Compatibility;

namespace osu.Game.Rulesets.BeatmapAccel.Features.Download;

public partial class BeatmapAccelBeatmapModelDownloader
{
    private static readonly IBeatmapAccelPlatformRuntime runtime = BeatmapAccelCompatibility.Current;

    public Action<Notification>? PostNotification { private get; set; }

    public event Action<ArchiveDownloadRequest<IBeatmapSetInfo>>? DownloadBegan;

    public event Action<ArchiveDownloadRequest<IBeatmapSetInfo>>? DownloadFailed;

    private readonly object downloadsLock = new();

    private readonly List<PreferredIpDownloadBeatmapSetRequest> currentDownloads = new();

    private readonly IModelImporter<BeatmapSetInfo> beatmapImporter;
    private readonly BeatmapManager? beatmapManager;
    private readonly IAPIProvider api;

    public BeatmapAccelBeatmapModelDownloader(IModelImporter<BeatmapSetInfo> beatmapImporter, IAPIProvider api)
    {
        this.beatmapImporter = beatmapImporter;
        beatmapManager = beatmapImporter as BeatmapManager;
        this.api = api;
    }

    public void SetPostNotification(Action<Notification>? postNotification)
        => PostNotification = postNotification;

    public ArchiveDownloadRequest<IBeatmapSetInfo>? GetExistingDownload(IBeatmapSetInfo model)
    {
        lock (downloadsLock)
            return currentDownloads.Find(request => request.Model.OnlineID == model.OnlineID);
    }

    public bool Download(IBeatmapSetInfo model, bool minimiseDownloadSize = false)
        => startDownload(model, minimiseDownloadSize, null);

    public bool DownloadAsUpdate(BeatmapSetInfo originalModel, bool minimiseDownloadSize = false)
        => startDownload(originalModel, minimiseDownloadSize, originalModel);

    private bool startDownload(IBeatmapSetInfo model, bool minimiseDownloadSize, BeatmapSetInfo? originalModel)
    {
        if (GetExistingDownload(model) != null)
            return false;

        int[] onlineBeatmapIds = model.Beatmaps.Select(beatmap => beatmap.OnlineID).Where(id => id > 0).Distinct().ToArray();
        var request = new PreferredIpDownloadBeatmapSetRequest(model, minimiseDownloadSize, api, CloudflareSpeedTestManager.GetPreferredIp());
        var notification = new DownloadNotification
        {
            Text = $"Downloading {request.Model.GetDisplayString()}",
        };

        request.DownloadProgressed += progress =>
        {
            notification.State = ProgressNotificationState.Active;
            notification.Progress = progress;
        };

        request.Success += filename =>
        {
            // 下载传输成功说明当前优选 IP 可用，重置失败恢复的连续跳过计数。
            CloudflareSpeedTestManager.OnDownloadSucceeded();

            _ = Task.Run(async () =>
            {
                bool importSuccessful = false;

                try
                {
                    if (originalModel != null)
                    {
                        importSuccessful = await beatmapImporter.ImportAsUpdate(notification, new ImportTask(filename), originalModel).ConfigureAwait(false) != null;
                        await waitForLocalAvailability(request.Model.OnlineID, onlineBeatmapIds, request.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var imported = (await beatmapImporter.Import(notification, new[] { new ImportTask(filename) }).ConfigureAwait(false)).ToList();
                        importSuccessful = imported.Any();

                        if (importSuccessful)
                        {
                            repairImportedSetOnlineIds(imported, request.Model.OnlineID);
                            await waitForLocalAvailability(request.Model.OnlineID, onlineBeatmapIds, request.CancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    if (!importSuccessful)
                        DownloadFailed?.Invoke(request);

                    removeCurrentDownload(request);
                }
            });
        };

        request.Failure += error =>
        {
            removeCurrentDownload(request);
            DownloadFailed?.Invoke(request);
            notification.State = ProgressNotificationState.Cancelled;

            if (error is OperationCanceledException)
                return;

            BeatmapAccelLogging.LogError(error, $"Beatmap download failed for set {request.Model.OnlineID}");

            // 超时以前和"用户取消"共用 OperationCanceledException，被上面那行直接吞掉：
            // 既不写日志、也不触发恢复测速。现在拆成独立类型，能走到这里。
            if (error is PreferredIpDownloadTimeoutException)
            {
                PostNotification?.Invoke(new SimpleNotification
                {
                    Text = $"BeatmapAccel: 谱面 {request.Model.OnlineID} 下载超时（{error.Message}），正在重新测速选择线路。"
                });
            }

            if (isNonRecoverableDownloadFailure(error))
            {
                BeatmapAccelLogging.Log($"Skipping preferred-IP recovery for non-recoverable beatmap download failure on set {request.Model.OnlineID}: {error.Message}");
                return;
            }

            CloudflareSpeedTestManager.BeginFailureRecoverySpeedTest(PostNotification);
        };

        notification.CancelRequested += () =>
        {
            request.CancelDownload();
            return true;
        };

        lock (downloadsLock)
            currentDownloads.Add(request);

        PostNotification?.Invoke(notification);
        DownloadBegan?.Invoke(request);
        request.Start();
        return true;
    }

    private void repairImportedSetOnlineIds(IReadOnlyList<Live<BeatmapSetInfo>> importedBeatmaps, int expectedOnlineId)
    {
        if (expectedOnlineId <= 0)
            return;

        foreach (Live<BeatmapSetInfo> imported in importedBeatmaps)
        {
            imported.PerformWrite(set =>
            {
                if (set.OnlineID > 0)
                    return;

                set.OnlineID = expectedOnlineId;
                BeatmapAccelLogging.Log($"Repaired imported beatmap set online ID to {expectedOnlineId} for {set.GetDisplayString()}.");
            });
        }
    }

    private async Task waitForLocalAvailability(int beatmapSetId, IReadOnlyList<int> onlineBeatmapIds, CancellationToken cancellationToken)
    {
        if (beatmapManager == null || onlineBeatmapIds.Count == 0)
            return;

        bool isAvailableLocally()
            => onlineBeatmapIds.Any(id => beatmapManager.IsAvailableLocally(new APIBeatmap { OnlineID = id }));

        for (int i = 0; i < 40; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (isAvailableLocally())
                return;

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        if (!isAvailableLocally())
            BeatmapAccelLogging.Log($"Imported beatmap set {beatmapSetId} did not become locally visible before the wait timeout expired.");
    }

    private static bool isNonRecoverableDownloadFailure(Exception error)
    {
        if (error is not PreferredIpDownloadHttpException httpError)
            return false;

        return httpError.StatusCode is HttpStatusCode.Unauthorized
                                   or HttpStatusCode.NotFound
                                   or HttpStatusCode.TooManyRequests;
    }

    public void CancelAllDownloads()
    {
        PreferredIpDownloadBeatmapSetRequest[] activeDownloads;

        lock (downloadsLock)
            activeDownloads = currentDownloads.ToArray();

        foreach (PreferredIpDownloadBeatmapSetRequest request in activeDownloads)
            request.CancelDownload();
    }

    private void removeCurrentDownload(PreferredIpDownloadBeatmapSetRequest request)
    {
        lock (downloadsLock)
            currentDownloads.Remove(request);
    }

    private partial class DownloadNotification : ProgressNotification
    {
        protected override Notification CreateCompletionNotification() => new ProgressCompletionNotification
        {
            Activated = CompletionClickAction,
            IsImportant = false,
            Text = CompletionText
        };
    }

    private sealed class PreferredIpDownloadBeatmapSetRequest : ArchiveDownloadRequest<IBeatmapSetInfo>
    {
        private const string download_host = "osu.ppy.sh";

        /// <summary>
        /// 空闲超时：连续这么久没有收到任何新字节，才判定链路已经死掉。
        /// 以前这里是一个 60 秒的"总时长"超时，会把"慢但仍在稳定传输"的大谱面直接砍断——
        /// 实测完成的下载耗时上限正好卡在 58.6 秒，越过 60 秒的全部消失。
        /// </summary>
        private static readonly TimeSpan download_stall_timeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 绝对上限：防止极端情况下（每秒都有几个字节进账）单个下载无限挂住。
        /// </summary>
        private static readonly TimeSpan download_hard_timeout = TimeSpan.FromMinutes(15);

        private readonly bool noVideo;
        private readonly IAPIProvider api;
        private readonly string preferredIp;
        private readonly CancellationTokenSource cancellationSource = new();
        private readonly string targetFilePath;
        private readonly Stopwatch stopwatch = new();

        private int firstProgressLogged;
        // [临时诊断] 跟踪最后一次进度回调的已写入字节数与 Content-Length，用于诊断移动端"传到一半被终止"。
        private long lastProgressBytes;
        private long? lastProgressTotal;

        public CancellationToken CancellationToken => cancellationSource.Token;

        public PreferredIpDownloadBeatmapSetRequest(IBeatmapSetInfo set, bool noVideo, IAPIProvider api, string preferredIp)
            : base(set)
        {
            this.noVideo = noVideo;
            this.api = api;
            this.preferredIp = preferredIp;

            AttachAPI(api);

            string tempFile = Path.GetTempFileName();
            targetFilePath = Path.ChangeExtension(tempFile, ".osz");
            runtime.MoveFileOverwrite(tempFile, targetFilePath);
        }

        public void Start()
        {
            stopwatch.Start();
            BeatmapAccelLogging.Log($"Starting preferred-IP download for beatmap set {Model.OnlineID} via {(string.IsNullOrWhiteSpace(preferredIp) ? download_host : preferredIp)}.");
            _ = Task.Run(downloadAsync);
        }

        public void CancelDownload()
        {
            cancellationSource.Cancel();
            Fail(new OperationCanceledException("Request cancelled"));
            cleanupTargetFile();
        }

        protected override string Target => $@"beatmapsets/{Model.OnlineID}/download{(noVideo ? "?noVideo=1" : string.Empty)}";

        protected override string Uri => noVideo
            ? $"https://{download_host}/api/v2/beatmapsets/{Model.OnlineID}/download?noVideo=1"
            : $"https://{download_host}/api/v2/beatmapsets/{Model.OnlineID}/download";

        private async Task downloadAsync()
        {
            // 三层计时器：
            //   stallTimeout —— 空闲超时，每收到一批新字节就重置（真正的"链路死了"判据）
            //   hardTimeout  —— 绝对上限，防止每秒挤几个字节的僵尸连接永久占位
            //   requestTimeout —— 两者与用户取消的并集，交给 HTTP 层
            using var stallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationSource.Token);
            using var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationSource.Token);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stallTimeout.Token, hardTimeout.Token);

            try
            {
                // 下载并发不给任何上限：用户想一次拖 20 个就拖 20 个。
                // 带宽被摊薄只会让每个下载变慢，而"慢"本身在空闲超时机制下是安全的，
                // 只要还在进字节就不会被判死。
                stallTimeout.CancelAfter(download_stall_timeout);
                hardTimeout.CancelAfter(download_hard_timeout);

                await runtime.DownloadFileAsync(new PreferredIpFileDownloadRequest(
                    new System.Uri(this.Uri),
                    parsePreferredIp(),
                    targetFilePath,
                    TimeSpan.FromSeconds(15),
                    createHeaders(),
                    (currentBytes, totalBytes) =>
                    {
                        if (Interlocked.Exchange(ref firstProgressLogged, 1) == 0)
                            BeatmapAccelLogging.Log($"Preferred-IP download received first progress for beatmap set {Model.OnlineID} after {stopwatch.ElapsedMilliseconds} ms.");

                        // 只要还有新字节进来，就说明链路活着 —— 重置空闲计时器。
                        // 这是本次修复的核心：慢但持续的下载不再被误杀。
                        resetStallTimeout(stallTimeout);

                        lastProgressBytes = currentBytes;
                        lastProgressTotal = totalBytes;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                            SetProgress((float)currentBytes / totalBytes.Value);
                    },
                    // 系统代理存在时是否仍然直连优选 IP。默认是"直连"：
                    // 规则模式代理通常不管 osu! 流量，走代理反而更慢。
                    ForceDirectConnection: BeatmapAccelCompatibility.UsePreferredIpWithSystemProxy), requestTimeout.Token).ConfigureAwait(false);

                SetProgress(1);
                BeatmapAccelLogging.Log($"Preferred-IP download finished transfer for beatmap set {Model.OnlineID} in {stopwatch.ElapsedMilliseconds} ms.");
                TriggerSuccess(targetFilePath);
            }
            catch (Exception e)
            {
                bool userCancelled = cancellationSource.IsCancellationRequested;
                bool stalled = !userCancelled && stallTimeout.IsCancellationRequested;
                bool hardExpired = !userCancelled && hardTimeout.IsCancellationRequested;
                bool timedOut = stalled || hardExpired;

                // 用户主动取消才是预期行为，不刷日志；
                // 停滞/超时和真实异常都必须留下痕迹——以前超时被当成取消静默吞掉，
                // 导致"下载进度条突然消失但日志里什么都没有"。
                if (!userCancelled)
                {
                    string reason = timedOut
                        ? (stalled
                            ? $"stalled for {download_stall_timeout.TotalSeconds:F0}s without receiving new bytes"
                            : $"exceeded hard limit of {download_hard_timeout.TotalMinutes:F0} min")
                        : $"Type={BeatmapAccelLogging.BuildExceptionTypeChain(e)}, Message={e.Message}";

                    BeatmapAccelLogging.Log($"[DIAG] Download aborted for set {Model.OnlineID} after {stopwatch.ElapsedMilliseconds} ms. " +
                                            $"Reason={reason}, lastBytes={lastProgressBytes}, lastTotal={(lastProgressTotal?.ToString() ?? "<null>")}, " +
                                            $"preferredIp={(string.IsNullOrWhiteSpace(preferredIp) ? "<empty>" : preferredIp)}.", LogLevel.Important);
                }

                cleanupTargetFile();

                if (timedOut)
                {
                    // 用专门类型而不是 OperationCanceledException：
                    // 上层 Failure 处理器会把 OperationCanceledException 当作"用户取消"直接 return，
                    // 那样就永远不会触发失败恢复测速。
                    Fail(new PreferredIpDownloadTimeoutException(
                        $"Preferred-IP download for beatmap set {Model.OnlineID} " +
                        $"{(stalled ? "stalled" : "exceeded hard time limit")} after {stopwatch.ElapsedMilliseconds} ms " +
                        $"(lastBytes={lastProgressBytes}, lastTotal={lastProgressTotal?.ToString() ?? "unknown"})."));
                    return;
                }

                Fail(e);
            }
        }

        /// <summary>
        /// 重置空闲超时。进度回调可能来自网络线程，也可能在下载主体已经结束时再被触发一次，
        /// 此时 CTS 可能已释放；吞掉 ObjectDisposedException 即可，不影响已经结束的下载。
        /// </summary>
        private static void resetStallTimeout(CancellationTokenSource stallTimeout)
        {
            try
            {
                stallTimeout.CancelAfter(download_stall_timeout);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private IPAddress? parsePreferredIp()
        {
            if (string.IsNullOrWhiteSpace(preferredIp))
                return null;

            return IPAddress.TryParse(preferredIp, out IPAddress? ipAddress) ? ipAddress : null;
        }

        private BeatmapAccelHttpHeader[] createHeaders()
        {
            return new[]
            {
                new BeatmapAccelHttpHeader("Authorization", $"Bearer {api.AccessToken}"),
                new BeatmapAccelHttpHeader("User-Agent", "osu!"),
                new BeatmapAccelHttpHeader("Accept-Language", api.Language.ToCultureCode()),
                new BeatmapAccelHttpHeader("x-api-version", api.APIVersion.ToString())
            };
        }

        private void cleanupTargetFile()
        {
            try
            {
                if (File.Exists(targetFilePath))
                    File.Delete(targetFilePath);
            }
            catch
            {
            }
        }
    }
}

/// <summary>
/// 优选 IP 下载超时（空闲超时或绝对上限）。
/// 刻意不继承 <see cref="OperationCanceledException"/>：上层需要把"传输停滞"和"用户主动取消"
/// 区分开，前者要写日志并触发重测速，后者才是静默预期行为。
/// </summary>
public sealed class PreferredIpDownloadTimeoutException : Exception
{
    public PreferredIpDownloadTimeoutException(string message)
        : base(message)
    {
    }

    public PreferredIpDownloadTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
