using System.Globalization;
using osu.Game.Configuration;
using osu.Game.Rulesets.Configuration;

namespace osu.Game.Rulesets.BeatmapAccel.Configuration;

public class BeatmapAccelRulesetConfigManager : RulesetConfigManager<BeatmapAccelSetting>
{
    public static BeatmapAccelRulesetConfigManager? Instance { get; private set; }

    public BeatmapAccelRulesetConfigManager(SettingsStore? settings, RulesetInfo ruleset)
        : base(settings, ruleset)
    {
        Instance = this;
    }

    protected override void InitialiseDefaults()
    {
        base.InitialiseDefaults();
        SetDefault(BeatmapAccelSetting.ShowPreviewDownloadOverlay, true);
        SetDefault(BeatmapAccelSetting.InterceptAllBeatmapDownloads, false);
        SetDefault(BeatmapAccelSetting.AutoSwitchOnStartup, true);
        SetDefault(BeatmapAccelSetting.AutoSwitchOnDownloadFailure, true);
        // IPv6 候选默认开启：实测大陆电信线路的 IPv6 常常比 IPv4 更通畅，
        // 而候选是否被采用完全由实测吞吐决定，开着不会选到更差的。
        SetDefault(BeatmapAccelSetting.EnableIpv6Candidates, true);
        // 系统代理（Clash / v2rayN 的"规则模式"）默认不阻止优选 IP。
        // 这类代理只把部分流量交给代理，osu! 往往走的是直连；旧版本检测到系统代理就整体禁用优选 IP，
        // 结果是"既没用上代理、也没用上优选 IP"，下载只能落到绕远的 Cloudflare 节点。
        SetDefault(BeatmapAccelSetting.UsePreferredIpWithSystemProxy, true);
        SetDefault(BeatmapAccelSetting.ThroughputProbeUrl, string.Empty);
        SetDefault(BeatmapAccelSetting.PreferredIp, string.Empty);
        SetDefault(BeatmapAccelSetting.PreferredIpColo, string.Empty);
        SetDefault(BeatmapAccelSetting.PreferredIpSelectedAtUnix, string.Empty);
        SetDefault(BeatmapAccelSetting.LastSpeedTestSummary, "尚未测速");
    }

    public bool GetShowPreviewDownloadOverlay()
        => Get<bool>(BeatmapAccelSetting.ShowPreviewDownloadOverlay);

    public bool GetInterceptAllBeatmapDownloads()
        => Get<bool>(BeatmapAccelSetting.InterceptAllBeatmapDownloads);

    public bool GetAutoSwitchOnStartup()
        => Get<bool>(BeatmapAccelSetting.AutoSwitchOnStartup);

    public bool GetAutoSwitchOnDownloadFailure()
        => Get<bool>(BeatmapAccelSetting.AutoSwitchOnDownloadFailure);

    public bool GetEnableIpv6Candidates()
        => Get<bool>(BeatmapAccelSetting.EnableIpv6Candidates);

    public bool GetUsePreferredIpWithSystemProxy()
        => Get<bool>(BeatmapAccelSetting.UsePreferredIpWithSystemProxy);

    public string GetThroughputProbeUrl()
        => Get<string>(BeatmapAccelSetting.ThroughputProbeUrl).Trim();

    public string GetPreferredIp()
        => Get<string>(BeatmapAccelSetting.PreferredIp).Trim();

    public string GetPreferredIpColo()
        => Get<string>(BeatmapAccelSetting.PreferredIpColo).Trim();

    public void SetPreferredIp(string value)
        => SetValue(BeatmapAccelSetting.PreferredIp, value.Trim());

    /// <summary>
    /// 一次性写入"优选 IP + 落地节点 + 选择时间"。选择时间用于判断缓存是否仍然可信，
    /// 以文本存储是为了不依赖配置系统对数值类型的支持范围。
    /// </summary>
    public void SetPreferredIpSelection(string ip, string colo, long unixSeconds)
    {
        SetValue(BeatmapAccelSetting.PreferredIp, ip.Trim());
        SetValue(BeatmapAccelSetting.PreferredIpColo, colo.Trim());
        SetValue(BeatmapAccelSetting.PreferredIpSelectedAtUnix, unixSeconds.ToString(CultureInfo.InvariantCulture));
    }

    public long GetPreferredIpSelectedAtUnix()
        => long.TryParse(Get<string>(BeatmapAccelSetting.PreferredIpSelectedAtUnix), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    public string GetLastSpeedTestSummary()
        => Get<string>(BeatmapAccelSetting.LastSpeedTestSummary).Trim();

    public void SetLastSpeedTestSummary(string value)
        => SetValue(BeatmapAccelSetting.LastSpeedTestSummary, value.Trim());
}

public enum BeatmapAccelSetting
{
    ShowPreviewDownloadOverlay,
    InterceptAllBeatmapDownloads,
    AutoSwitchOnStartup,
    AutoSwitchOnDownloadFailure,
    EnableIpv6Candidates,
    UsePreferredIpWithSystemProxy,
    ThroughputProbeUrl,
    PreferredIp,
    PreferredIpColo,
    PreferredIpSelectedAtUnix,
    LastSpeedTestSummary,
}
