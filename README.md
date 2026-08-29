# claude-scope

屏幕边上的一条横幅，用余光就能看出 Claude Code 现在在干什么。

*A always-on-top status strip for Claude Code on Windows. Chinese UI.*

---

## 它解决什么问题

你把活儿交给 Claude Code，然后切去干别的。过一会儿你想知道：它还在跑吗？跑完了吗？是不是在等我点确认？是不是报错了？

盯着终端窗口不现实，切回去看又打断手头的事。claude-scope 在屏幕顶边（或底边）挂一条 52 像素高的横幅，用**波形 + 颜色 + 文字**三个维度同时编码状态——1 米外用余光扫一眼就能分辨，不用聚焦去读字。

| 状态 | 颜色 | 波形 |
|---|---|---|
| 空闲 | 灰 | 近乎平直 |
| 思考中 | 青 | 平缓正弦 |
| 写代码 | 紫 | 短促爆发 |
| 执行命令 | 黄 | 快速锯齿 |
| 完成 | 绿 | 舒缓正弦 |
| **出错** | **红** | **削顶失真** |
| **等你确认** | **橙** | **慢速脉冲（呼吸）** |
| 疑似卡住 | 浅蓝 | 稀疏单点 |
| 会话已结束 | 灰 | 全平 |

「出错」和「等你确认」会响一声（15 秒内不重复）。

横幅右侧显示当前**模型**、**上下文 token 数**、**5 小时额度**、**周额度**；空间不够时按优先级依次隐藏。

## 装它

不需要装任何运行时，单个 exe 自包含。

1. 从 [Releases](../../releases) 下载 `claude-scope.exe`，放到你喜欢的位置（**别放临时目录**，开机自启会记住这个路径）
2. 打开 PowerShell 或 cmd，跑一次：

```
claude-scope.exe --install
```

它会做两件事：往 `~/.claude/settings.json` 写 hook（**改之前一定备份**，只动自己写的条目），以及在注册表 `HKCU\...\Run` 里登记开机自启。全程不需要管理员权限。

3. 双击 `claude-scope.exe` 启动。

卸载：`claude-scope.exe --uninstall`，然后删掉 exe。

## 用它

**横幅上点右键**（或右键托盘图标）：演示、显示（贴哪条边 / 哪块屏 / 高度 / 是否占位）、设置、关闭横幅、退出。双击托盘图标开设置窗。

命令行：

| 命令 | 作用 |
|---|---|
| `--doctor` | 自检：hook 装没装、事件收到没有、额度读到没有 |
| `--state` | 打印当前状态快照（JSON） |
| `--usage` | 打印额度 |
| `--stop` | 正常退出（会归还 AppBar 占位） |
| `--reset-workarea` | 救急：把工作区恢复成整屏 |
| `--install` / `--uninstall` | 装 / 卸 hook 和开机自启 |

> **`--reset-workarea` 什么时候用**：横幅开启「占位」后会向 Windows 注册一块保留区域，最大化的窗口会自动避开它。如果进程被任务管理器强杀、或者蓝屏断电，这块占位来不及归还，屏幕边上就会留下一条永远收不回来的空白。这个命令是独立的——需要它的时候程序往往已经死了。

## 它怎么知道 Claude 在干什么

两条通道，互相补位：

**① Hook**（权威）。往 `~/.claude/settings.json` 装 18 个 hook，全是 `http` 型，直接 POST 到 `127.0.0.1:45737`，不启动任何子进程，对 Claude 的延迟影响可以忽略。用户级 hook 是热加载的，装完不用重开会话。

**② 会话记录尾随**（补位）。跟着 `~/.claude/projects/**/<sessionId>.jsonl` 走，零配置。模型名和上下文 token 只有这里有；用户按 ESC 打断也只有这里能看出来（Claude Code 打断时不发任何 hook，连 `Stop` 都不发）。

## 已知做不到的事

这一节不是免责声明，是实测结论。

**拿不到进程号。** hook 负载里没有 `pid`。所以：

| 情况 | 能不能分辨 |
|---|---|
| 正常关闭会话 | 能，`SessionEnd` 会报 |
| 你按 ESC 打断 | 能，从会话记录里认出来 |
| 强杀进程 / 蓝屏 / 断电 | **不能**，只能按静默时长推断，10 分钟后降级成「会话已结束」 |

「疑似卡住」永远是推断，不是事实。`--doctor` 会把这句话直接打出来。

**额度数字是推断的。** 数据源是 `%APPDATA%\Claude\plan-usage-history.json`，桌面版自己写的。字段含义（`fh` = 5 小时窗口、`sd` = 7 天窗口）是从行为观察出来的——取值 0-100、按 5 小时周期归零、`sd` 变化缓慢符合 7 天窗口——**没有官方确认**。而且这个文件只在桌面版运行时才更新，采样间隔中位数约 15 分钟，观察到过 11 小时的空档。

所以：**第一次用请在 Claude Code 里跑一次 `/usage`，跟横幅上的数对一次。** 数据超过 30 分钟没更新时，横幅会把它显示成灰的。格式版本号不对时直接不显示，不猜。

**上下文只显示绝对 token 数，不显示百分比。** 因为上下文窗口的上限没有可靠数据源，除不出百分比。

**桌面版的纯聊天模式监控不了。** 那种会话不写 `projects/*.jsonl`，也不触发 hook。

**有几个 hook 装了但从没见它响过**：`Notification`、`PermissionRequest`、`PermissionDenied`、`Elicitation`、`UserPromptExpansion`、`SubagentStart`。事件名都是官方文档里的有效值，但在实测的这个客户端上一条都没收到过。`--doctor` 会逐类列出每个 hook 收到了多少条——长期为零，就说明对应的状态不会出现。

## 排错

**横幅没出现** —— `--doctor` 看「运行中」那行。端口被占的话，`--install --port 45999` 换一个（会同时改配置和 hook）。

**端口查不到进程** —— 用的是 `HttpListener`（http.sys），所以 `netstat -ano | findstr :45737` 显示的是 **System (pid 4)**，不是本程序。要确认它在跑，用 `--doctor`，或看 `%APPDATA%\claude-scope\runtime.json`。

**hook 装了但一条事件都没收到** —— 用户级 hook 是热加载的，让 Claude 随便跑一步再看。项目级 `.claude/settings.json` 里的 hook **不是**热加载的，那种要重开会话。

**屏幕边上有一条空白，但横幅不在** —— AppBar 占位没归还。跑 `--reset-workarea`。还不行就 `Stop-Process -Name explorer -Force`。

**`%APPDATA%\Claude` 是 MSIX 重定向目录** —— 真实位置在 `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude`。本程序只读它里面的额度文件。hook 写的是 `~/.claude`，那个目录**不在**重定向范围内。

**日志** —— `%APPDATA%\claude-scope\logs\daemon.log`，1 MB 轮转，留 3 份，同类消息 30 秒内合并。设置窗的「诊断」页也能直接看。

## 配置

`%APPDATA%\claude-scope\config.json`，改完重启生效。常用项：

```jsonc
{
  "port": 45737,
  "strip": {
    "monitor": -1,        // -1 = 主显示器，或者 0/1/2… 指定第几块
    "edge": "top",        // top | bottom
    "height": 52,
    "reserve": true       // 是否向 Windows 注册占位
  },
  "thresholds": {
    "stallSeconds": 300,          // 多久没事件才怀疑卡住
    "deadAfterStallSeconds": 300, // 判定卡住后再静默多久转成"已结束"
    "errorStickySeconds": 10,     // 红屏至少停留多久
    "sessionTtlMinutes": 30
  },
  "sound": { "waiting": true, "error": true, "minIntervalSeconds": 15 }
}
```

环境变量 `CLAUDE_SCOPE_HOME` 可以整个换掉运行时目录（测试用）。

## 自己编译

需要 .NET 8 SDK：

```
dotnet publish src/ClaudeScope -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o dist
```

产物约 63 MB。**不能开裁剪**——WinForms 不支持（NETSDK1175）。

编译前如果有实例在跑，先 `--stop`，否则 exe 被占用会编译失败。

## 兼容性

Windows 10/11 + Claude Code（桌面版和 CLI 都行）。只在 Windows 10 Pro 19045 和 Claude for Windows 上实测过。

## 许可

MIT
