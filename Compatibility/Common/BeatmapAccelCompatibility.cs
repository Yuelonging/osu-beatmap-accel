using System;
using osu.Game.Rulesets.BeatmapAccel.Compatibility.Android;
using osu.Game.Rulesets.BeatmapAccel.Compatibility.Windows;
using osu.Game.Rulesets.BeatmapAccel.Configuration;

namespace osu.Game.Rulesets.BeatmapAccel.Compatibility;

internal static class BeatmapAccelCompatibility
{
    private static readonly IBeatmapAccelPlatformRuntime platformRuntime = createPlatformRuntime();

    public static IBeatmapAccelPlatformRuntime Current => platformRuntime;

    /// <summary>
    /// 是否允许在存在系统代理时仍然直连优选 IP。
    /// 默认 true：Clash / v2rayN 的"规则模式"只是把部分流量交给代理，osu! 通常仍走直连，
    /// 此时直连优选 IP 既可用又更快。旧版本一旦检测到系统代理就整体禁用优选 IP，
    /// 结果是既没用上代理、也没用上优选 IP，下载只能落到绕远的 Cloudflare 节点。
    /// 用户可以在设置里关掉（"系统代理优先"），行为即回到旧版本。
    /// </summary>
    public static bool UsePreferredIpWithSystemProxy
        => BeatmapAccelRulesetConfigManager.Instance?.GetUsePreferredIpWithSystemProxy() ?? true;

    /// <summary>系统代理存在且用户选择让代理优先 —— 此时不应直连优选 IP。</summary>
    public static bool PreferredIpBlockedBySystemProxy
        => Current.HasSystemProxy && !UsePreferredIpWithSystemProxy;

    private static IBeatmapAccelPlatformRuntime createPlatformRuntime()
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            return new AndroidBeatmapAccelPlatformRuntime();

        return new WindowsBeatmapAccelPlatformRuntime();
    }
}
