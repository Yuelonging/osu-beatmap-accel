# BeatmapAccel

`BeatmapAccel` 是一个面向 `osu!lazer` 的谱面下载加速 ruleset。（Vibe Coding）

目标：

- 为 `osu!lazer direct download` 选择当前更快的 Cloudflare IP
- 让下载尽量走优选 IP，而不是默认解析结果
- 在不修改 `osu!` 主工程的前提下，尽可能覆盖更多下载入口

当前提供两种模式：

- 预览弹窗模式：播放谱面预览后，在右上角弹出下载按钮
- 全局接管模式：尝试接管当前界面的谱面下载按钮、自动缺谱面下载和部分下载状态追踪

![右上角弹窗](imgs/image.png)
![设置](imgs/image-1.png)

## 安装

- 在 GitHub 右侧 `Releases` 下载最新发布文件
- 方式一：将文件中的osu.Game.Rulesets.BeatmapAccel.dll文件直接放在osu文件夹的rulset文件内
- 方式二：本地构建，请下载最新的osu源码(https://github.com/ppy/osu)然后进行编译，编译命令参见本地构建章节，最后将生成的.dll文件放在osu文件夹的rulset文件内

## 模式说明

### 预览弹窗模式

- 默认开启
- 播放谱面预览后显示右上角下载按钮
- 点击后使用当前优选 IP 下载谱面

### 全局接管模式

- 默认关闭
- 会尝试接管更多下载入口
- 兼容性风险更高

## 已知限制

- 本项目不修改 `osu!` 主工程，而是通过 ruleset 注入实现
- 全局接管模式依赖反射和运行时 patch，兼容性天然弱于主工程改造
- 全局接管模式仍可能掉帧、异常或失效
- 部分页面的下载状态显示可能和原生不完全一致
- 个别自动下载链路可能会因为 `osu!lazer` 更新而失效
- 如果你的网络对某个 Cloudflare IP 更差，手动填写错误 IP 可能导致下载更慢或失败

## 本地构建
请打开osu.Game.Rulesets.BeatmapAccel.csproj调整osu源码路径或者已安装lazer的路径，不会调整请咨询chatgpt或者大肥鱼
```powershell
dotnet build .\osu.Game.Rulesets.BeatmapAccel\osu.Game.Rulesets.BeatmapAccel.csproj -c Release
```

## 输出文件

- `osu.Game.Rulesets.BeatmapAccel\bin\Release\net8.0\osu.Game.Rulesets.BeatmapAccel.dll`

## 实现说明

项目当前主要由几部分组成：

- `CloudflareSpeedTestManager`
  - 负责测速、候选筛选和优选 IP 切换
- `PreviewTrackHandler`
  - 负责预览音频触发后的右上角弹窗
- `BeatmapAccelBeatmapModelDownloader`
  - 负责通过当前优选 IP 发起下载、导入谱面和失败恢复
- `GlobalBeatmapDownloadInterceptor`
  - 负责在全局接管模式下接管按钮、自动下载链路和部分状态桥接

## 备注

谱面预览，弹出下载按钮参照了[LLin](https://github.com/MATRIX-feather/LLin)。
