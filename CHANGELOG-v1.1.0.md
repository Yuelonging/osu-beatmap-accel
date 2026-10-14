# Changelog — v1.2.0

## [v1.2.0]

重做「优选 IP」的筛选逻辑：从「按延迟（TTFB）挑最快」改成「按实测吞吐挑最快」，
并修掉「检测到任何系统代理就整体停用优选 IP」这一在规则模式下误伤的问题。
本版本**不包含** v1.1.0 的多人游戏代理，是 v1.0.7 的功能超集。

仍然不改动 osu! 主工程任何一个文件。

### 背景（实测复现）

同一台机器、同一条大陆电信线路，对 Cloudflare 边缘节点做逐 IP 探测，结果差异极大：

| 候选 IP | 落地节点 | HTTP 状态 | 实测吞吐 |
| --- | --- | --- | --- |
| `172.64.229.8` / `172.64.229.135` / `172.64.229.37` / `172.64.229.179` | NRT（东京） | 200 | **~1700–1780 KB/s** |
| `172.64.231.94` | LAX（洛杉矶） | 200 | ~335 KB/s |
| `172.64.228.182` | SIN（新加坡） | **403** | ~33 KB/s |
| `172.64.229.12` | NRT（东京） | **403** | — |

- 线路把 `osu.ppy.sh` 引到了 **FRA（法兰克福，234 ms）或 LAX**，而 `172.64.229.0/24` 这一个段落在 NRT；
- 旧逻辑按 TTFB 排序，于是「延迟最低」的那个恰恰是 `172.64.228.182`（SIN，0.077 s）——
  一个**必然 403、只有 33 KB/s** 的死路；
- 旧逻辑还把 4xx 当成「探测成功」，等于把坏节点当健康节点收下；
- 403 是**逐 IP**的，不是逐网段的：同属 `172.64.229.0/24` 的 `.8` 返回 200、`.12` 返回 403。
  所以必须在 HTTP 阶段逐个剔除，不能靠「这个段落整体可用」来推断。

### Changed

- **`Features/Download/CloudflareSpeedTestManager.cs` 三段式流水线重写**：
  1. TCP 可达性探测（并发 32）；
  2. 对 `osu.ppy.sh` 做真实 HTTP 复测（并发 8），**状态码 ≥ 400 一律淘汰**，
     并从 `CF-RAY` 响应头尾部解析落地节点码（`-NRT` / `-LAX` / `-FRA` / `-SIN` …）用于报告与分组；
  3. 在**同一个 IP** 上量真实吞吐（并发 6，最多 512 KB，最少 32 KB 即判可用），
     用 `https://speed.cloudflare.com/__down?bytes=524288` 作探针、`https://b.ppy.sh/preview/1.mp3` 作退路。
- **排序键由 TTFB 改为实测吞吐（KB/s）**；只有当所有吞吐测量都不可用时才退回 TTFB 排序。
- 第 3 阶段按落地节点分组轮转取样（每节点最多 2 个），让有限的名额分散在**不同路径**上，
  避免 6 个名额全被同一个节点占满而错过更快的线路。
- **候选段收敛到 `172.64.224.0/22` / `172.64.228.0/22` / `172.64.229.0/24`**（实测这三个段落有 NRT 落地），
  每段取 8 个采样点，避免把时间浪费在已知会绕远的段上。
- **新增启动快速路径**：若缓存的选择在 12 小时内、TCP 仍通、HTTP 不复现 4xx、吞吐 ≥ 128 KB/s，
  直接沿用，不做全网扫描（实测 9.8 s 的全流程降到 **1.2 s**）；缓存失效或体检不过则自动重扫。
- **系统代理判定改正**：新增设置项「检测到系统代理时仍使用优选 IP」（默认开）。
  Clash / v2rayN 的**规则模式 / PAC 模式**只是把部分流量交给代理，osu! 通常仍走直连，
  此时直连优选 IP 既可用又更快。旧版本只要 `HasSystemProxy` 为真就停用优选 IP，
  在本机（`HasSystemProxy == True`）会导致加速**完全失效**。现在只有用户显式关掉该开关才停用。
- **IPv6 候选默认开启**（原来默认关）：是否入选完全由实测吞吐决定，开着不会选到更差的节点。
- 探测**强制直连**：`ConnectCallback` 之外还显式设置 `handler.UseProxy = false`。
  仅注入 `ConnectCallback` 是不够的——`SocketsHttpHandler.Proxy` 留空时会回落到
  `HttpClient.DefaultProxy`（系统代理），请求仍会走代理，于是「优选 IP」形同虚设。
  这个 bug 修复前后表现完全不同：修复前所有探测都报「连接失败」，修复后 NRT / LAX 正常、SIN 正确返回 Forbidden。
- 新增设置项「吞吐测速地址（高级）」用于覆盖探针地址；设置页新增「最近一次测速」结果摘要；
  新增系统代理状态提示（会解释规则 / PAC 模式）。

### Added

- `BeatmapAccelSetting`：`UsePreferredIpWithSystemProxy`、`ThroughputProbeUrl`、`PreferredIpColo`、
  `PreferredIpSelectedAtUnix`（以文本存储，避免依赖配置系统的数值类型范围）、`LastSpeedTestSummary`。
- `BeatmapAccelRulesetConfigManager.SetPreferredIpSelection(ip, colo, unixSeconds)`：一次性写入 IP + 节点 + 时间。
- `tools/SpeedTestHarness`：不走游戏、直接以反射驱动 `CloudflareSpeedTestManager` 的真实联网测试台，
  支持 `probe <ip...>` / `full [manual|startup]` / `cached <ip>` 三种模式，用于在不启动 lazer 的情况下复现与验证测速行为。

### Removed

- `Features/Proxy/`（`SelectiveHttpProxy` / `HubProxyAccelerator` / `HubProxyConfigStore` / `HubProxyModule` /
  `HubProxySettingsApplier`）及其设置页的「多人游戏走代理」「代理地址」「代理目标域名」「应用并重连」「测试连通性」，
  以及 `tools/ProxyMechanismTest` / `tools/ProxyIntegrationTest` / `tools/run-proxy-tests.cmd`。
  需要多人代理请使用 v1.1.0（见 `../v1.1.0_适配net10+多人代理/`）。

### Verified

- 逐 IP 探测：`172.64.229.8` → 200 / NRT / 1731 KB/s；`172.64.229.12` → 403 / NRT；
  `172.64.228.128` → 403 / SIN；`172.64.231.94` → 200 / LAX / 335 KB/s；`172.64.229.135` → 200 / NRT / 1703 KB/s。
- 完整手动测速：177 个候选 → 115 个 TCP 可达 → 18 个 HTTP 存活 → 选中
  `172.64.229.37`（NRT，TCP 90 ms，TTFB 273 ms，**1749 KB/s**），全程 9.8 s。
- 缓存快速路径：健康的 NRT 缓存 IP 沿用耗时 **1.2 s**；缓存的 403（SIN）IP 被体检拒绝并重新选出
  `172.64.229.179`（NRT，1770 KB/s，9.5 s）。
- 加载校验：`tools/run-loadtest.cmd` 输出「可用 ✓」（枚举 148 个类型、实例化 `BeatmapAccelRuleset`、
  ruleset API `2026.818.0` 与客户端一致）。
- 编译：0 错误 0 警告。

### 产物

- `dist/osu.Game.Rulesets.BeatmapAccel.dll`
- md5 `e2d820170706f6123e10745878ec561d`，195 584 字节

---

## 基线 [v1.0.7]

本版本以 v1.0.7 为基线，其内容为：目标框架 `net8.0` → `net10.0`、
新增「客户端程序集模式」（引用已安装 lazer 自带的 `osu.Game.dll` / `osu.Framework.dll`，无需源码）、
保留「源码模式」兼容上游 CI、编译前校验引用路径。

详见 [`../v1.0.7_适配net10/CHANGELOG-v1.0.7.md`](../v1.0.7_适配net10/CHANGELOG-v1.0.7.md)。
