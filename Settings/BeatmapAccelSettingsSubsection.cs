using System;
using osu.Framework.Bindables;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Overlays.Settings;
using osu.Game.Rulesets.BeatmapAccel.Compatibility;
using osu.Game.Rulesets.BeatmapAccel.Configuration;
using osu.Game.Rulesets.BeatmapAccel.Features.Download;

namespace osu.Game.Rulesets.BeatmapAccel.Settings;

public partial class BeatmapAccelSettingsSubsection : RulesetSettingsSubsection
{
    private SettingsButtonV2 switchButton = null!;
    private SettingsNote systemProxyNote = null!;
    private SettingsNote speedTestSummaryNote = null!;

    private BeatmapAccelRulesetConfigManager config = null!;

    [Resolved(canBeNull: true)]
    private INotificationOverlay? notifications { get; set; }

    public BeatmapAccelSettingsSubsection(BeatmapAccelRuleset ruleset)
        : base(ruleset)
    {
    }

    [BackgroundDependencyLoader]
    private void load()
    {
        config = (BeatmapAccelRulesetConfigManager)Config;

        CloudflareSpeedTestManager.ScheduleToMainThread = action => Schedule(action);
        CloudflareSpeedTestManager.NotificationPoster = notifications == null ? null : new Action<Notification>(notifications.Post);

        Children = new Drawable[]
        {
            wrapWithSettingsPadding(new GridContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                ColumnDimensions = new[]
                {
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, 180),
                },
                RowDimensions = new[]
                {
                    new Dimension(GridSizeMode.AutoSize),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        new FormTextBox
                        {
                            Caption = "当前优选 IP",
                            HintText = "支持手动填写 IPv4 或 IPv6 地址。留空时将退回默认域名解析，测速完成后也会自动覆盖这里的值。",
                            PlaceholderText = "例如 172.67.65.31 或 2606:4700::6810:1234",
                            Current = config.GetBindable<string>(BeatmapAccelSetting.PreferredIp)
                        },
                        switchButton = new SettingsButtonV2
                        {
                            Text = "测速并切换",
                            Action = runSpeedTestAndSwitch,
                            Padding = default,
                            Margin = new MarginPadding { Left = 10 }
                        }
                    }
                }
            }),
            wrapWithSettingsPadding(speedTestSummaryNote = new SettingsNote
            {
                RelativeSizeAxes = Axes.X,
            }),
            wrapWithSettingsPadding(new FormCheckBox
            {
                Caption = "谱面预览显示右上角弹窗",
                HintText = "在播放谱面预览音频后，于右上角显示 BeatmapAccel 下载弹窗。关闭后不会弹窗，但其他下载接管功能仍可单独启用。",
                Current = config.GetBindable<bool>(BeatmapAccelSetting.ShowPreviewDownloadOverlay)
            }),
            wrapWithSettingsPadding(new FormCheckBox
            {
                Caption = "接管所有谱面下载",
                HintText = "高风险兼容模式。BeatmapAccel 会尝试接管当前界面的谱面下载按钮、自动缺谱面下载与下载状态追踪，让它们尽量走优选 IP。osu! 更新后可能失效，或导致少数页面下载状态显示异常。",
                Current = config.GetBindable<bool>(BeatmapAccelSetting.InterceptAllBeatmapDownloads)
            }),
            wrapWithSettingsPadding(systemProxyNote = new SettingsNote
            {
                RelativeSizeAxes = Axes.X,
            }),
            wrapWithSettingsPadding(new FormCheckBox
            {
                Caption = "检测到系统代理时仍使用优选 IP",
                HintText = "默认开启。Clash / v2rayN 的「规则模式」只是把部分流量交给代理，osu! 通常仍走直连，"
                           + "此时直连优选 IP 既可用又更快。只有当你必须经代理才能访问 osu!（全局 / TUN 模式）时才关掉它。",
                Current = config.GetBindable<bool>(BeatmapAccelSetting.UsePreferredIpWithSystemProxy)
            }),
            wrapWithSettingsPadding(new FormCheckBox
            {
                Caption = "启动自动测速切换",
                HintText = "游戏启动后后台测速并选定优选 IP。若上次结果仍在有效期内且仍然通畅，会直接沿用，不会重复全网扫描。",
                Current = config.GetBindable<bool>(BeatmapAccelSetting.AutoSwitchOnStartup)
            }),
            wrapWithSettingsPadding(new FormCheckBox
            {
                Caption = "下载失败后自动测速切换",
                HintText = "下载请求失败时，后台重新测速并切换到新的优选 IP。手动取消下载不会触发。",
                Current = config.GetBindable<bool>(BeatmapAccelSetting.AutoSwitchOnDownloadFailure)
            }),
            wrapWithSettingsPadding(new FormCheckBox
            {
                Caption = "启用 IPv6 候选测速",
                HintText = "把 Cloudflare 的 IPv6 段也加入候选。实测大陆线路的 IPv6 往往比 IPv4 更通畅；"
                           + "网络没有可用 IPv6 时这一项只会浪费一点探测时间，不会选到更差的节点。",
                Current = config.GetBindable<bool>(BeatmapAccelSetting.EnableIpv6Candidates)
            }),
            wrapWithSettingsPadding(new FormTextBox
            {
                Caption = "吞吐测速地址（高级）",
                HintText = "用于测量各候选节点的真实下载速度，留空使用内置地址"
                           + "（默认 https://speed.cloudflare.com/__down?bytes=524288，退路为 b.ppy.sh 预览音频）。"
                           + "该主机不在默认域名解析路径上，只是用作逐 IP 的带宽探针；如需更换可填写任意支持 HTTPS 的直链。",
                PlaceholderText = "留空 = 使用内置地址",
                Current = config.GetBindable<string>(BeatmapAccelSetting.ThroughputProbeUrl)
            }),
        };

        updateSystemProxyNote();

        config.GetBindable<string>(BeatmapAccelSetting.LastSpeedTestSummary).BindValueChanged(_ => updateSpeedTestSummaryNote());
        config.GetBindable<bool>(BeatmapAccelSetting.UsePreferredIpWithSystemProxy).BindValueChanged(_ => updateSystemProxyNote());

        updateSpeedTestSummaryNote();
    }

    private void updateSpeedTestSummaryNote()
    {
        string summary = config.GetLastSpeedTestSummary();

        speedTestSummaryNote.Current.Value = new SettingsNote.Data(
            string.IsNullOrWhiteSpace(summary) ? "尚未测速。" : "最近一次测速：\n" + summary,
            SettingsNote.Type.Informational);
    }

    private void updateSystemProxyNote()
    {
        if (!BeatmapAccelCompatibility.Current.HasSystemProxy)
        {
            systemProxyNote.Current.Value = new SettingsNote.Data(
                "当前没有检测到系统代理，优选 IP 会正常生效。",
                SettingsNote.Type.Informational);
            return;
        }

        bool usePreferredIp = config.GetUsePreferredIpWithSystemProxy();

        systemProxyNote.Current.Value = new SettingsNote.Data(
            usePreferredIp
                ? "检测到系统代理。当前为「仍使用优选 IP」：谱面下载会直连你选出的 Cloudflare 节点。"
                  + "代理是规则 / PAC 模式（osu! 不在代理规则里）时保持开启即可；"
                  + "如果你必须经过代理才能访问 osu!，请关闭下面那个开关。"
                : "检测到系统代理，且选择了「系统代理优先」：谱面下载会走代理，优选 IP 不生效。",
            usePreferredIp ? SettingsNote.Type.Informational : SettingsNote.Type.Warning);
    }

    private static Drawable wrapWithSettingsPadding(Drawable drawable) => new Container
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Padding = SettingsPanel.CONTENT_PADDING,
        Child = drawable
    };

    private void runSpeedTestAndSwitch()
    {
        switchButton.Enabled.Value = false;

        Task.Run(async () =>
        {
            try
            {
                await CloudflareSpeedTestManager.SwitchToFastestIpAsync(SpeedTestTrigger.Manual, notifications).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                BeatmapAccelLogging.LogError(e, "Manual speed test failed");

                Schedule(() => notifications?.Post(new SimpleNotification
                {
                    Text = $"BeatmapAccel: 测速失败。\n{e.Message}"
                }));
            }
            finally
            {
                Schedule(() =>
                {
                    switchButton.Enabled.Value = true;
                    updateSpeedTestSummaryNote();
                });
            }
        });
    }
}
