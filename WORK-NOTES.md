# Gray Nanite Desktop 工程交接与工作笔记

记录日期：2026 年 10 月 13 日。面向继续维护、设计或制作素材的 agent。

当前工程是 Windows 原生 WPF 互动桌面，不是桌宠，也不是当前运行的 Electron 网页壁纸。下面「2026-10-13 第二次部署」一节是最新状态；其后各节写于那次部署之前，涉及立绘、姿势、开场和安装哈希的描述以新一节为准。

## 2026-10-13 第二次部署：新立绘 + 全身动作 + 新开场

「已核对」用户确认后已部署到安装目录（只替换 `StellarisUI-v12.exe`，文件名和启动路径不变）。部署记录在工作区 `work/deploy-gw-v2-20261013/`：

| 位置 | 内容 |
| --- | --- |
| `source-before/` | 改动前的 8 个 .cs 和 13 张 PNG 原件 |
| `publish2/` | 实际部署的发布产物；`publish/` 是修开场细节前的第一次构建 |
| `verify/`、`verify2/` | 诊断截图与结果 JSON；`verify2/intro` 是最终开场帧 |
| `install-backup/` | 部署前的旧 exe、config.json、chat.json、startup.log 副本 |
| `diagnostic-report.txt` | Codex 记录的第一轮完整诊断报告 |

```text
部署前 StellarisUI-v12.exe  D3DB16CA60CEE36AD8CC537B270D32129343371318B782C4A445420BB8CEA8C2
部署后 StellarisUI-v12.exe  478CA05EFCE8C3476B11828C302CBE4484384A2E3A0A2A95E1E6BE474CA539EE
```

「已核对」部署前后 `data/config.json`、`data/chat.json` 哈希不变；HKCU Run 值 `StellarisUI` 前后相同。旧进程 CloseMainWindow 返回 false，等 10 秒后被结束。新进程从安装目录启动，startup.log 记录了开场语音 7.026939 s 播放完毕并转场进桌面。发布、`--self-test`、开场渲染、表情+动作布局（16 套 × 4 工作区）、人物渲染退出码均为 0；仍只有原有 `_winEventDelegate` 未使用字段警告。

改了什么：
- 立绘：新母版（赛璐璐风，银灰长发、呆毛、军礼服短裙长靴披风，领口开大版）同时写入 `character.png` 和四个 mood 别名；8 张表情只把脸部椭圆合回母版，脸外像素与母版完全一致；7 张全身动作 `character-pose-{ahoge-guard,wave,hips,arms-crossed,stop,thinking,shy}.png`，腿部跟着动，脚底统一离底边 44px。美术工作流与候选图在工作区 `work/graywind-art-20261013/`，`final-set/old-upper-body-only/` 是被用户否掉的只动上半身版本。
- 代码：新 `CharacterPose.cs` 定义 `CharacterPose`、`CharacterLook`（表情 + 动作，动作会忽略表情）和每个动作各自的触摸几何 `CharacterHitGeometry`；`CharacterHitMap`、校验器、`--expression-render-test` 都按当前 look 的几何工作，测试输出目录名形如 `Pose.Stop`。语音映射见 `CharacterVoiceInteraction.cs`，环境情绪：happy→微笑，analyzing→思考动作，alert→停止动作。
- 开场：`StartupBootOverlay.cs` 的绘制部分重写（logo 扫描显形与锁定冲击环、PRTS 乱码解码、身份验证面板、ACCESS GRANTED 印章、WELCOME BACK、斜切刀刃退场）。时长、阶段时间、`PasswordCount`、语音和罗德岛 logo 素材都没换。宽度 < 640 时面板改为标签在上的竖排。

「待验证」实机里开场与语音的同步感、各动作的点击手感，需要用户实际使用后反馈；离屏测试覆盖不到。「个人思路」VERIFIED 印章一角会轻微压到面板下沿，是有意保留的盖章感，用户不喜欢可再挪。源码改动仍未 commit。

回滚：结束安装目录里的 `StellarisUI-v12.exe` 进程，把 `install-backup/StellarisUI-v12.exe` 复制回去即可；不要用备份的 config/chat 覆盖用户之后产生的新数据。

### 同日追加：切换过渡与语音后停留（`work/deploy-gw-v3-20261013/`）

「用户反馈」语音播完后动作不要突然切掉，切换动作和表情要更自然。「已核对」只改了 `MainWindow.cs`（原件在 `source-before/`），已部署：

```text
部署前 StellarisUI-v12.exe  478CA05EFCE8C3476B11828C302CBE4484384A2E3A0A2A95E1E6BE474CA539EE
部署后 StellarisUI-v12.exe  E4B50D43CED730583E6D408FCEAF55AF4B795AD9867016F4933095552276C3F4
```

config/chat 哈希与 Run 值前后不变；发布、`--self-test`、`--character-render-test` 退出码 0。`install-backup/` 里是上一版（v2）exe 与数据副本，回滚方法同上。

- 交叉淡化：代码在 CharacterImage 上方插入一个不可点击的图层，与它共用 RenderTransform。旧图先实不透明地压在上面，新图在下方 0→1 淡入（前 60%），旧图在 35% 后淡出。表情切换 320 ms，动作切换 620 ms 并带 -7 DIP 的上浮回落；回到环境状态时 ×1.35 更慢，上浮 -4。过渡期间跳过眨眼和信号闪烁，因为它们也会动画 CharacterImage 不透明度。
- 停留：语音结束后，动作保持 1.8 s、纯表情保持 1.2 s，再结束 touch。停留期间再次点击会取消计时，直接接新反应。

「待验证」离屏测试只看静态布局，过渡观感和停留时长需要用户实机反馈。部署脚本曾因比较进程启动时间误报"身份变化"而停在关闭进程之前；改用可执行文件完整路径核对后正常完成。

### 同日追加：开场等用户登录进桌面后再播（`work/deploy-gw-v4-20261013/`）

「用户反馈」开场要在输完密码、按回车进入系统之后才开始。「已核对」改了 `MainWindow.cs` 和 `App.cs`（原件在 `source-before/`），已部署：

```text
部署前 StellarisUI-v12.exe  E4B50D43CED730583E6D408FCEAF55AF4B795AD9867016F4933095552276C3F4
部署后 StellarisUI-v12.exe  77FFDE6048880BF0A8A9FBABE0EA5DA29B34AFF6B30043426A98A5B0CE2E5BF3
```

config/chat 哈希与 Run 值前后不变；启动日志出现 `Startup desktop gate open; inputDesktopDefault=True`，随后正常播开场。`install-backup/` 里是 v3 的 exe 与数据副本。

- 行为：在 Loaded 中、`PlayAsync` 之前等待 `IsInteractiveDesktopReady`。条件是 OpenInputDesktop 能打开且名称为 Default（输密码的界面在 Winlogon 安全桌面，打不开），并且前台窗口不属于 LockApp.exe（锁屏画面那一层）。等待期间每 500 ms 轮询一次；进入桌面后再等 900 ms 才播放。音乐、遥测等都在开场之后，所以一起延后。保险：等待期间在窗口上真实按键或点鼠标会直接放行。
- `WTSSessionInfoEx` 只记日志、不参与判断。曾因读错偏移（SessionFlags 在 +16，不是 +12）得到错误的 locked。修正后结果正确，但已用另两项验证过，保持不依赖它。
- 新诊断参数 `--desktop-gate-probe=秒数`：每秒往 startup.log 记一行判断结果，不建窗口、不改自启动。用户实测锁屏再解锁：锁屏画面阶段为 foregroundLockApp=True，输密码阶段为 inputDesktopDefault=False，解锁后 1 s 内恢复 ready=True。
- 「待验证」开机时程序在锁屏阶段就已启动的真实场景，需要下次开机确认，可以看 startup.log 里是否有 `PRTS intro waiting for sign-in` 和 `released after sign-in` 两行。

部署时 Codex 的关闭后检查再次误报，停在了覆盖 exe 之前。期间桌面程序已退出约 4 分钟，确认后从第 4 步继续完成。以后部署脚本里关闭后的检查只需确认该路径的进程已不存在。

### 同日追加：中途锁屏解锁后重播开场、去掉角色文字提示（`work/deploy-gw-v5-20261013/`）

「用户反馈」中途锁屏再进来也要重播开场；不需要"点击呆毛…"的文字提示。用户授权改完直接部署。「已核对」只改了 `MainWindow.cs`（原件在 `source-before/`），自检通过后已部署，新 exe SHA-256 以 `2FAF7B7D` 开头（完整值见 Codex 对话的部署回报）。config/chat 哈希与 Run 值前后不变，启动日志正常，开场完整播放。`install-backup/` 里是 v4 的 exe 与数据副本。

- 去掉了 `CharacterImage.ToolTip`，手形光标保留。
- 订阅 `SystemEvents.SessionSwitch`（关闭时退订）。只有首轮开场已结束、窗口可见、当前没有开场覆盖层时，`SessionLock` 才会新建一个 `StartupBootOverlay` 盖住桌面（t=0 是黑底），并停掉角色语音。
- 回到桌面有三条独立信号，任意一条触发即可，避免黑底卡死：`SessionUnlock` 事件；每 500 ms 轮询 `IsInteractiveDesktopReady`，先看到未就绪、之后又就绪；窗口上的真实按键或点击。触发后等 900 ms 再 `PlayAsync`，播完后 Dispose 并移除。Esc 和 F10 跳过逻辑与首轮相同。
- F10 隐藏时锁屏不会重播；首轮开场还在进行或等待登录时锁屏，也不会叠加第二个。
- 「待验证」实机锁屏再解锁时的重播效果，需要用户试一次，可以看 startup.log 里的 `Session locked; PRTS intro armed for unlock` 和 `Replaying PRTS intro after unlock`。重播时 QQ 音乐不会暂停，开场语音会和正在放的歌叠在一起。

以下用「已核对」「历史验证」「用户反馈」「困难」「个人思路」「自认为巧思」「待验证」区分事实与判断。我的审美和实现取舍只是参考：它们可能不适合下一版，也可能不被用户或接手者认可。当前素材、配色和构图不构成未来设计的美学限制。

## 工程位置与版本识别

本机工作区为 `C:/Users/30457/Documents/Codex/2026-08-12/r`，下表中的工作区路径均相对它。

| 位置 | 用途与注意事项 |
| --- | --- |
| `github/gray-nanite-desktop/` | 当前源码仓库，应从这里读代码。 |
| `work/stellaris-source/` | 旧版或参考源码，不作为当前入口；没有在此次交接中确认其完整来源。 |
| `work/deploy-*/source-before/` 和 `rollback/` | 不同任务的源码及部署前备份，恢复时要确认对应时间和内容。 |
| `work/deploy-character-expressions-20261013/` | 最近表情集成的验证记录、发布产物与备份。 |
| `output/imagegen/` | 历史生成素材、提示词、预览及部分打包结果，不等于已部署资源。 |
| `outputs/`、`backups/`、各处 `bin/obj/publish/` | 输出、历史备份和构建产物；不能仅凭名字认定“最新版”。 |
| `work/dotnet-sdk/`、`work/nuget-packages/`、`work/offline-feed/` | 本机构建工具和依赖缓存。 |

「已核对」本机安装目录：

```text
C:/Users/30457/AppData/Local/Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Local/GrayNaniteDesktop
```

运行入口是该目录中的 `StellarisUI-v12.exe`。进程号会变化，不适合作为长期标识。下面是第二次部署之前的安装文件 SHA-256（exe 已被替换，现值见上文新一节）：

```text
StellarisUI-v12.exe
D3DB16CA60CEE36AD8CC537B270D32129343371318B782C4A445420BB8CEA8C2

Assets/startup-voice.wav
3642AF4D570A72553DCD15406F7581977E3C82ECF85AF77C5BF92942040E9AB0
```

「困难」源码、Git 和可见运行版本并不一致。仓库在 `main`，HEAD 是 `2ad8604 Initial source release v11.0`，README 标题仍为 v11.0；最新改动大量未提交，运行文件名沿用了 v12。文件名不是可靠的构建版本号，检出 HEAD 也不会得到当前完成的桌面。接手前先看 `git status`，保留现有修改，不要用 reset 或 checkout 清掉它们。此次未 commit 或 push。

## 主要代码入口

技术栈是 `net10.0-windows`、x64、C# 14、WPF，通知区图标使用 WinForms。工程文件为 [StellarisUI.csproj](StellarisUI.csproj)。

| 文件 | 从这里了解什么 |
| --- | --- |
| [StellarisUI.MainWindow.xaml](StellarisUI.MainWindow.xaml) | 左侧模块、中央对话与投影、右侧文件、顶部信息和音乐条布局。 |
| [StellarisUI/App.cs](StellarisUI/App.cs) | 启动、诊断参数、单实例互斥及创建主窗口的顺序。 |
| [StellarisUI/MainWindow.cs](StellarisUI/MainWindow.cs) | 文件绑定、搜索、打开、聊天、天气、动画、F10、启动配置和音乐编排。职责集中，是维护难点。 |
| [StellarisUI/AppConfig.cs](StellarisUI/AppConfig.cs) | 配置模型、八个默认目录绑定及音乐和启动开关。 |
| [StellarisUI/StartupBootOverlay.cs](StellarisUI/StartupBootOverlay.cs) | PRTS 风格启动画面、语音、扫描与桌面过场。 |
| [StellarisUI/CharacterVoiceInteraction.cs](StellarisUI/CharacterVoiceInteraction.cs) | 透明度命中、三个点击区域、语音抽取、表情及音频生命周期。 |
| [StellarisUI/CharacterExpressionAssets.cs](StellarisUI/CharacterExpressionAssets.cs) | 表情资源的嵌入加载、缓存与冻结。 |
| `StellarisUI/Character*Validator.cs` | 素材、点击坐标、布局及表情状态的回归验证。 |
| `StellarisUI/QQMusicAudio.cs`、`QQMusicUiBridge.cs`、`QQMusicTray.cs` | 进程音量、QQ 客户端 UI 自动化和启动后回托盘。 |
| [Tools/QQMusicBridge.ps1](Tools/QQMusicBridge.ps1) | Windows 媒体会话标题、进度及播放控制桥接。 |
| `StellarisUI/AssistantEnvelope.cs`、`data/response.schema.json` | 聊天结构化操作格式；聊天调用本地 Codex CLI。 |
| [Build.ps1](Build.ps1) | 发布到仓库的 `publish/`；不会自动替换已安装版本。 |
| [FRIEND-SETUP-GUIDE.md](FRIEND-SETUP-GUIDE.md) | 面向非技术使用者的安装说明。 |

## 当前功能和素材状态

「已核对代码」文件入口对应绑定文件夹；有搜索、归档和聊天操作。结构化文件动作限定在绑定目录内，拒绝符号链接或 junction 越界，不覆盖同名目标，执行类内容有确认流程。聊天能力和权限仍取决于本地 Codex 配置，不能把这套界面理解成无限权限的自主文件管家。

天气使用 `wttr.in` 的 IP 推测位置，不是 GPS 精确定位，VPN、网络出口或服务状态会影响所在地名称。QQ 音乐使用已安装客户端的媒体会话，并有 UI 自动化后备；启动后自动播放已绑定的官方歌单或专辑入口，再回到托盘。登录状态、队列及客户端版本会影响实际控制能力。

F10 当前是显示或隐藏整个桌面界面，不是浏览器全屏切换；也可以从托盘恢复。单实例互斥使第二次启动退出，第二次启动本身不一定唤回已经隐藏的界面。

### 启动语音和动画

用户提供的英语录音已转为外置 `Assets/startup-voice.wav`，单声道、22050 Hz、16 位 PCM，时长约 7.027 秒；没有改语速、音高或停顿。启动画面包括罗德岛标志、慢速密码输入和扫描揭示真实桌面，约 9.35 秒完成，之后初始化 QQ 音乐。Escape 可以跳过，F10 隐藏并停止启动语音；每个进程只播一次，不随每次 F10 重播。

### 表情和点击语音

「历史验证」八张表情已应用：`smile/laugh/surprised/angry/pout/shy/focused/wry`。原始 neutral 加上八张差分共九张；五个旧名字 `character.png`、`character-neutral/happy/analyzing/alert.png` 保留为相同的 neutral 兼容别名。

九段用户提供的 MP3 位于 `Assets/character-voice/`，保留原始字节。每一区域三段随机轮换，不连续重复，播放不能重叠，结束后有 350 ms 冷却。

| 点击区域 | 01 对应表情 | 02 对应表情 | 03 对应表情 |
| --- | --- | --- | --- |
| 呆毛 `ahoge` | Surprised | Angry | Pout |
| 其他可见身体 `body` | Laugh | Smile | Wry |
| 制服胸部区域 `chest` | Shy | Angry | Wry |

表情在音频实际打开并开始播放时出现；结束、失败、隐藏或退出后恢复最新环境心情。环境心情映射为 happy → Smile、analyzing → Focused、alert → Angry，其余 → Neutral。字幕显示在现有对话标题，不写成聊天记录。启动覆盖层期间不响应人物点击。

当前动作仍是整张立绘的呼吸、漂移、轻摆、闪频等投影效果，不是 Live2D 骨骼，也不是手臂连续运动。表情差分并非只改脸的像素级编辑，局部身体和透明边缘可能有轻微重绘。

### 姿势差分仍未应用

最近两批一共八张透明姿势预览，均为 1024×1536。它们不在运行资源中，没有改点击坐标，也没有获得“应用这八张姿势”的后续确认。接手者不要把之前“应用表情”的确认延伸成对新姿势的确认。

文件均位于：

```text
C:/Users/30457/.codex/generated_images/019ff78d-7114-74d1-bb57-557800e5df7f
```

| 预览动作 | 文件名 |
| --- | --- |
| 护住呆毛并惊讶 | `exec-dc934ca7-150a-46e1-a337-8a365eea41e0.png` |
| 开心挥手 | `exec-84e2851a-ac60-42fb-bec5-e5dfea64e41c.png` |
| 抱臂警告 | `exec-2e7ff214-aa6f-4932-b98c-d39168b7d809.png` |
| 正式敬礼 | `exec-3aaf3edf-f696-4a62-8982-dda11d7f7b0a.png` |
| 托腮思考 | `exec-ce87df6f-b1c8-4f18-b455-3aa1f79290be.png` |
| 双手叉腰并调侃 | `exec-aaa09420-8da0-450e-947f-a358a2f0bac6.png` |
| 害羞侧身并在身前低位合手 | `exec-39db1ab4-8a02-4d94-a247-e32c7940a3de.png` |
| 掌心向外示意停止 | `exec-b579d211-060a-410d-a580-e11098835b07.png` |

「个人判断」预览审看没有发现明显肢体缺损或裁切，但这不等于用户喜欢，也不等于能直接塞进现有交互。抱臂更接近冷淡不满而非夸张愤怒；护呆毛与敬礼仍有动作相似处；害羞图的手实际较低。后续可以保留、重画或换方向，不必维护我的判断。

## 遇见的困难和原因边界

1. 「困难」运行路径混乱。历史上用户多次看到旧版本、启动脚本提示未安装，或认为新版没有变化。我的思路是用实际进程路径、可执行文件哈希、资源和日志识别版本，不能只看快捷方式、文件名或 README。

2. 「用户反馈与待验证」曾出现黑屏、F10 无效、关闭外部文件后桌面隐藏、中文输入失败，以及 PDF、JPG、文件夹打不开。当前代码有桌面层定位、原生输入和 Shell 打开后备，但这些历史现象不能归为一个已证实的原因。IME、焦点、文件关联、权限、显示缩放可能分别参与；此次没有重测所有输入法及文件类型。Shell 返回码或路径存在，也不能证明默认应用成功展示文件。

3. 「用户反馈与困难」HoYoPlay 进程出现不代表崩铁真正启动。启动器、游戏进程和可见游戏窗口是不同层级，不能把命令已发送或启动器已存在说成游戏已打开。今后成功提示要符合能够观察到的结果。

4. 「困难」长发、呆毛和整张投影运动容易在顶部或右边裁切。预留顶部空间、右侧间隙和自适应缩放是当前解法，但会影响大小与构图。不同外接屏、工作区和 Windows 缩放仍需要实机检查，离屏渲染不等于所有屏幕都合适。

5. 「困难」音频加载、停止和环境心情会交错。若点击立即改表情，可能出现还没声音就反应；若音频结束一律归零，又会打断正在分析的状态。需要明确状态优先级及失败清理，不能仅靠视觉延时猜测。

6. 「困难」部署时曾第一次复制提示文件占用，之后重试成功。当时没有查明占用者，不能断言是权限、管理员模式或某个已知进程导致。只操作已确认的本项目目标，不以此为理由批量结束别人的进程。

7. 「困难」外部客户端行为不是本工程完全可控的。QQ 音乐的媒体会话和 UI 控件会变，天气依赖外部服务，Codex 聊天依赖本机环境。后备实现降低部分失败概率，不能据此承诺永久兼容。

## 我的思路与自认为的巧思

以下是我自认为有价值、但可以被替换的实现判断，不是“最佳实践认证”，更不是美学规则。

- 「自认为巧思」把环境心情 Ambient 与点击反应 Touch 分开。语音期间记住新的任务心情，结束时回到最新状态，而不是固定 neutral。它解决状态互相覆盖；我认为较自然，但接手者可以采用更丰富的优先级或其他叙事逻辑。

- 「自认为巧思」每条语音明确绑定表情，并在 MediaOpened 后开始表情。这样语义匹配可检查，加载失败不会留下无声反应；代价是随机性较少。未来也可以做语境选择，不必固守一对一。

- 「自认为巧思」缓存并冻结位图，只替换前景和全息残影的图源，不重建舞台、不重置原有变换。目的是减少加载停顿和位置跳动，也避免调用会额外脉冲缩放的 SetMood；它不能抹平生成图本身的轮廓差异。

- 「自认为巧思」以图片本地坐标、Uniform 留白和 alpha 共同判断点击，让命中跟着缩放、漂移、旋转走。呆毛额外给本区内 16 个源像素容差，照顾细目标。这个数值只是可用性折中，未经正式用户研究，不是普适标准。

- 「个人思路」为了保留头发而移动整个投影舞台、扩大右侧裁切空间，并留顶部余量。当前工作区会产生约 1.5% 的尺寸调整。我的偏好是先保护人物轮廓和两侧功能区；用户也可能更喜欢更大的角色、重排对话或完全不同的构图。

- 「自认为巧思」过场跟随真实语音时长，而不是沿用旧固定计时强行截断新录音。这样替换录音更稳定；目前约 0.123 秒的结束间隔和扫描节奏来自此前试看的取舍，不代表唯一正确节奏。

- 「自认为巧思」QQ 启动回托盘带有取消标记，手动打开客户端会取消仍待执行的隐藏动作，避免稍后又把用户主动打开的窗口藏走。这处理的是竞态，不保证每一版 QQ 客户端都采用相同窗口行为。

- 「自认为巧思」诊断分支在创建 MainWindow 前运行，能离屏验证而不改真实自启动。它将几何和生命周期问题变得可复查；但测试覆盖技术假设，不会替用户判断“好看”。

## 技术假设和美学方向要分开

当前角色方案是银发、呆毛、黑色军官装、金红点缀和仪式剑；整体界面经历了偏暗科幻、游戏大厅和 PRTS 启动画面的尝试。这些是用户此前确认的版本历史，不是要求未来 agent 只能往这个方向想。

我此前倾向暗底、克制发光、可读性优先，这种审美可能过于保守，也未必足够“游戏感”。接手者可以质疑配色、比例、界面语言、人物呈现甚至整套方向；尊重用户当前任务和已有确认，提出新的预览或方案即可，不必服从我的品味。

当前代码的点击区域则是技术假设：1024×1536 图上固定呆毛 ROI 和胸部多边形，余下可见像素为身体。缓存随图源更新只会重建 alpha 图，不会自动识别新的头部或胸部。若换姿势，身体部位移动，应该增加每姿势命中元数据或另选识别方案，并跟随改验证；这不意味着只能使用原姿势。

现有表情验证要求可见边界漂移不超过 3 个源像素，历史实测最大 2 个；这是为同姿势切表情设的检查，不适合原样拿来拒绝新姿势。布局或美术方向改变后，验证假设也应有理由地更新。

## 构建和诊断注意事项

`Build.ps1` 使用 PATH 中的 dotnet，输出仓库 `publish/`。本机另外有 .NET SDK 10.0.400：`work/dotnet-sdk/dotnet.exe`。从源码目录执行发布示例：

```powershell
& '../../work/dotnet-sdk/dotnet.exe' publish './StellarisUI.csproj' `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o '../../work/handoff-check-20261013/publish' `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -v minimal
```

这里只是示例，没有在此次整理时执行；后续应使用新的任务输出目录。本机已有恢复的依赖，新机器可能需要先 restore，不能保证 `--no-restore` 到处可用。

下列诊断在创建主窗口前运行，不改真实自启动、目录绑定或 QQ 播放状态：

- `--self-test`
- `--intro-render-test=绝对输出目录`
- `--character-render-test=绝对输出目录 --character-layout-source=绝对XAML路径`
- `--expression-render-test=绝对输出目录 --character-layout-source=绝对XAML路径`

它们仍会写日志或指定输出。自测会读取音频峰值、创建并清理 GUID 临时测试目录和 DOCX；无权限时部分链接检查可能跳过。不能把它们描述成零文件系统接触。`--qq-seek-test` 和 `--qq-play-collection-test` 会实际操作 QQ 客户端，不属于同一类无播放干扰诊断。

「重要困难」直接普通运行新发布文件并不是无副作用预览：MainWindow 构造时加载配置并调用 SetStartup，会根据开关创建、改写或删除真实 HKCU Run 自启动项；启用时记录当前可执行文件路径。`--skip-intro` 只跳过开场，不阻止这件事。优先用诊断参数检验，真正替换运行版本时再明确部署路径。

PNG 在工程根 `Assets/` 中以 Resource 嵌入。新表情走嵌入加载，需要重新构建，不能以为往安装目录丢 PNG 就一定生效；通用 LoadImageAsset 部分路径会优先外置图，二者行为不同。启动 WAV、点击 MP3、桥接脚本和 response schema 是随发布复制的外置文件。

部署时保护安装目录里的 `data/config.json` 和 `chat.json`，不要用测试产物覆盖用户目录绑定、歌单、启动开关或聊天。仅替换任务需要的文件，先留对应备份；确认启动路径、哈希、日志、F10 和实际行为。回滚也要区分代码、语音、配置的版本，不能盲目恢复旧配置。

## 验证证据与仍需验证的部分

「历史验证」最近部署报告在工作区 `work/deploy-character-expressions-20261013/verification.json`：发布、自测和表情渲染退出码为 0；仍有原有 CS0169 未使用字段警告。九张图、四种工作区、三种动画位置共 108 帧，通过 432 个变换点击采样，没有对话框或文件栏重叠。工作区为 2560×1440、2560×1392、2048×1152、1920×1080。

报告包含嵌入图与已确认素材字节一致、语音到表情映射及状态恢复测试、配置与音频未变、启动路径未变等结果。实机播放日志只记录了 Body/Laugh 和 Chest/Shy 两组，不能说九段都已人工点击试听。布局验证对组合极限动作允许并报告轻微边缘裁切，闲置位置裁切会失败；不能说每帧都零裁切。

此次只重新核对源码结构、Git 状态、运行进程和安装文件哈希，没有重跑上述全套测试。离屏测试用 XAML 派生布局，不覆盖所有 Windows DPI、多屏切换、GPU、IME 或 QQ 客户端状态，也不能证明审美成功。

「待验证或后续选择」姿势差分的用户取舍、每姿势点击区域、回到环境姿势的策略、语音匹配及不同工作区的重叠裁切，都还没接入验证。连续动作、骨骼动画或帧间过渡也没有完成。若接下来的任务只是讨论或预览，不要自动扩大成部署。

本次文档不复制历史 API 密钥、账号会话或私人配置。以后分享源码或日志前仍应核对敏感内容；历史其他工作流不是本桌面工程的接手目标。
