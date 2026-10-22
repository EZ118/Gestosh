# Gestosh

Windows 精密触摸板（Windows Precision Touchpad）手势扩展。  
**不使用任何第三方库**，只依赖 Windows 自带的 .NET Framework 4.8 和 Win32 API。

作者：**EZ118** · 版本：**1.4.0.0**

已实现：

- **三指移动 = 鼠标左键拖拽**（任意方向，不是模拟滚轮，也不是切换窗口）。
- **四指向下一滑 = App Expose 窗口预览**；在 App Expose 中向上滑 = 随手势倒放并关闭，窗口回到原位。
- **不在 App Expose、也不在 Gestosh 显示桌面状态时，四指向上滑 = Windows 自带 Task View（Win+Tab）**。
- **四指向左/向右滑 = 跟着手指渐进切换上一个/下一个虚拟桌面**：滑过约 30% 屏幕宽度后松手才切换，不够就弹回。
- **双指从触摸板右侧边缘向左滑 = Win+A，打开 Windows 操作/通知中心**。
- **五指由收缩至张开 = 当前桌面“贴边显示桌面”**：所有符合条件的窗口移到各自最近的屏幕边缘，只露出一点点。
- **五指由张开至收缩 = 恢复**：窗口回到原来的位置；原来最大化的窗口重新最大化。
- **左键单击任意已贴边窗口露出的部分 = 恢复**；这个点击会被吞掉，不会传给窗口本身，避免误点关闭按钮。
- **全程不改变窗口大小**；最大化窗口先以“相同外框大小”的普通窗口参与移动，恢复时再还原最大化状态。
- 贴边/恢复使用平滑过渡动画（默认 340 ms，60 FPS）。
- 只处理**当前虚拟桌面**的窗口（`IVirtualDesktopManager`）；支持多显示器；自动避开任务栏所在边；同边窗口沿边排开而不是扇形堆叠。
- **App Expose**：四指向下滑动**随手势进度**展开；打开、点击空白、悬停都不抢原窗口焦点，只有选中缩略图才切换前台。缩略图按**候选行数 + 加权评分**布局，尽量保留窗口在桌面上的相对方位；背景固定使用当前壁纸，**不做模糊、不截屏、不在每帧改变亮度**，避免闪烁；悬停显示白色边框和标题 tooltip；支持 Tab/Shift+Tab、方向键、Enter/Space、Esc；鼠标在 App Expose 中仍可移动。**不显示标题栏，也不在窗口下方显示窗口标题**。
- **Win+D 交互保护**：贴边状态下按 Win+D，会先把 Gestosh 的窗口放回真实位置，再交给 Windows，避免窗口永久卡在贴边状态。
- 三指/四指/五指手势不影响单指移动和双指滚动（唯一的例外是双指右侧边缘手势）。

![icon](assets/Gestosh-preview.png)

---

## 快速开始

### 直接运行

```text
dist\Gestosh.exe
```

程序没有主窗口，只驻留在托盘。托盘菜单和全局快捷键都可以触发贴边/恢复。托盘菜单里的 **“手势设置...”** 用来把各个手势改成别的功能（见 [手势设置对话框](#手势设置对话框)）。

如果双击时被 SmartScreen 拦住（从浏览器/聊天窗口下载的文件会带上 Mark-of-the-Web），看下面的 [程序元数据与数字签名](#程序元数据与数字签名)；最快的办法是 `Unblock-File .\Gestosh.exe`，或右键 → 属性 → 勾选“解除锁定”。

- **Ctrl+Alt+Shift+D**：贴边显示桌面
- **Ctrl+Alt+Shift+R**：恢复窗口
- **Ctrl+Alt+Shift+T**：切换贴边/恢复

### 命令行控制

```text
Gestosh.exe --dock
Gestosh.exe --restore
Gestosh.exe --toggle
Gestosh.exe --expose
Gestosh.exe --reload
Gestosh.exe --exit
```

这些命令只向**已经运行**的实例发送消息；不会另起一个实例，也不会在未运行时乱动窗口。

### 首次运行检查

1. 从 **Windows 文件资源管理器**（按 Win+E）打开 `E:\Gestosh\dist`，双击 `Gestosh.exe`。不要从聊天窗口的文件卡片、沙箱资源管理器或受限终端启动；这些方式会把程序放在低完整性沙箱里，托盘/触摸板/注册表都会被系统拒绝。
2. 把光标放在一个普通窗口的标题栏上，用三根手指在触摸板上整体向左/右/上/下移动；窗口应跟随光标拖动。三指向下仍然是拖拽，不会打开 App Expose。
3. **四根手指从上往下一滑**应打开 App Expose；在 App Expose 中四指向上滑应让动画随手势倒放，回到桌面后关闭；不在 App Expose 且未处于 Gestosh 显示桌面时，四指向上滑应打开 Windows Task View。**双指从触摸板右侧边缘向左滑**应打开 Windows 操作/通知中心。**四指向左/向右滑**应让桌面跟着手指滑动，滑够了松手就切换虚拟桌面，没滑够就弹回。在 App Expose 中点击任意缩略图可切换到该窗口；拖拽缩略图后松手，缩略图会弹回原位。按 Esc 或点击缩略图之外的空白区域也可以关闭。
4. 五指先靠拢再张开：当前桌面的普通窗口应滑到屏幕边缘，只露出细条；窗口大小不变。任务栏在底部时，窗口只会贴上/左/右，不会贴到底部任务栏；同一侧的窗口会沿边排开，不再叠成一堆。
5. 五指先张开再靠拢：窗口滑回原位置；原来最大化的窗口重新最大化。
6. 也可以直接左键单击任意贴边窗口露出的细条；所有窗口应立即滑回原位。
7. 如果三指移动仍然触发 Windows 的“切换应用/任务视图”，说明系统三指滑动没有被关闭；检查日志，或手动在“设置 → 蓝牙和其他设备 → 触摸板 → 三指手势”中关闭后重试。
8. 如果托盘没有图标或完全没有手势，运行 `dist\Gestosh.Debug.exe --diagnose 30`，确认能看到触摸板设备和实时报告。

### 构建

Windows 10/11 自带 `csc.exe`，无需安装 Visual Studio / .NET SDK：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

输出：

```text
dist\Gestosh.exe         GUI 版本，双击运行
dist\Gestosh.Debug.exe   控制台版本，用于 --diagnose / --selftest
dist\Gestosh.ini         默认配置文件
obj\                     中间产物（PatchVersion.exe、打补丁用的临时副本）
```

`build.ps1` 在链接完成后会用 `tools\PatchVersion.cs` 重写 Win32 版本资源，写入公司/作者、产品名、版本号，以及 6 种语言的“文件说明”和简介；详见 [程序元数据与数字签名](#程序元数据与数字签名)。签名是可选的，用 `sign.ps1`。

### 安装 / 开机启动（可选）

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

默认复制到 `%LOCALAPPDATA%\Programs\Gestosh`，并在当前用户的“启动”文件夹创建快捷方式。不希望开机启动：

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -NoStartup
```

卸载：

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
# 同时删除配置和日志：
powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Purge
```

---

## 程序元数据与数字签名

### 文件里写了什么

`Gestosh.exe` 的 Win32 版本资源（资源管理器 → 右键 → 属性 → 详细信息）由 `build.ps1` 写入：

| 字段 | 值 |
| --- | --- |
| 公司名称 / 作者 | `EZ118` |
| 产品名称 | `Gestosh` |
| 文件说明 | 跟随系统 UI 语言：`Gestosh - 精密触摸板手势工具`（中文）、`Gestosh - precision touchpad gestures`（英）、`Gestosh - gestes du pavé tactile de précision`（法）、`Gestosh - жесты точной сенсорной панели`（俄）、`Gestosh - gestos del panel táctil de precisión`（西）、`Gestosh - Gesten für Präzisionstouchpads`（德） |
| 备注 | 对应语言的一句话简介（本 README 的摘要） |
| 文件版本 / 产品版本 | `1.4.0.0` / `1.4.0` |
| 版权 | `Copyright © 2026 EZ118. All rights reserved.` |
| 原始文件名 | `Gestosh.exe` |

in-box 的 `csc.exe` 只能生成一个语言中性的版本块（`FileDescription` 取自 `AssemblyTitle`），而且不允许 `/win32res` 与 `/win32icon`、`/win32manifest` 同时出现。所以 `tools\PatchVersion.cs` 在链接之后用 `BeginUpdateResource` / `UpdateResource` 重写 `RT_VERSION`，写入 6 个 `StringTable`（0409 / 0804 / 040C / 0419 / 0C0A / 0407）和一个 `Translation` 表；资源管理器、任务管理器会按系统语言自动选中对应块。

`src\Gestosh.manifest` 同时声明 `asInvoker`（不请求管理员权限）、PerMonitorV2 DPI 感知、`longPathAware`，并标记支持 Windows 7–11。

### 为什么还会被 SmartScreen 拦截

SmartScreen 判断的是**签名 + 信誉**，不是元数据：

- 文件带 **Mark-of-the-Web**（浏览器/聊天窗口下载、或从 zip 解压）→ 触发“Windows 已保护你的电脑”；
- **没有签名**，或签名证书太新 → 发布者未知、信誉不足；
- 如果开启了 **智能应用控制**（Windows 11），未签名程序会被直接阻止，连“仍要运行”都不给。

### 最快的解决方式（本机）

1. 右键 exe → 属性 → 常规 → 勾选“解除锁定” → 确定；或者：

   ```powershell
   Unblock-File .\Gestosh.exe
   ```

2. 仍然弹出 SmartScreen 时：点“更多信息”→“仍要运行”。
3. 提示来自“智能应用控制”时，只能在 设置 → 隐私和安全性 → Windows 安全中心 → 应用和浏览器控制 → 智能应用控制 中关闭它，或改用已签名版本。

`build.ps1` 和 `install.ps1` 已经对生成/安装的 exe 执行 `Unblock-File`；但**每次重新下载都会重新打上标记**，需要再解除一次。

### 让其他人也不被拦截：代码签名

需要一张**公共 CA 签发的代码签名证书**（EV 立即有信誉，OV 需要积累）。自签名证书只能让本机信任，不能消除别人的 SmartScreen。

```powershell
# 使用证书存储里的证书（推荐：证书已导入“个人”）
powershell -ExecutionPolicy Bypass -File sign.ps1 -Thumbprint <指纹>

# 直接使用 pfx
powershell -ExecutionPolicy Bypass -File sign.ps1 -Pfx .\EZ118.pfx -PfxPassword <密码>

# 只在本机验证签名流程（会提示证书不受信任）
powershell -ExecutionPolicy Bypass -File sign.ps1 -SelfSigned
```

`sign.ps1` 用 `Set-AuthenticodeSignature`，SHA-256 + RFC3161 时间戳（默认 `http://timestamp.digicert.com`，可用 `-TimestampUrl` 换），签完立刻用 `Get-AuthenticodeSignature` 回读状态；`-SelfSigned` 会额外导出 `dist\Gestosh-selfsigned.cer`，可用 `Import-Certificate` 导入“受信任的根证书颁发机构”和“受信任的发布者”，让本机接受。

注意：

- 先 `build.ps1` 再 `sign.ps1`；签名之后再改文件会让签名失效，重新构建后要重新签。
- 同一张证书长期使用才会积累 SmartScreen 信誉；换证书等于从零开始。
- 仍然被误报时，可以向微软提交：<https://www.microsoft.com/en-us/wdsi/filesubmission>。
- 不想自己买证书时，也可以用 Azure Trusted Signing（按次计费，需要完成身份验证）。

---

## 手势是怎么工作的

### 三指拖拽

精确触摸板会把每个接触点的 `Contact ID / X / Y / Tip / Confidence` 作为 HID 报告上报。Gestosh 通过 **Raw Input + HID** 读取这些报告（`UsagePage=0x0D`、`Usage=0x05`，用 `HidP_GetValueCaps` / `HidP_GetUsageValue` / `HidP_GetUsages` 解析，自动处理报告 ID 和报告长度），把坐标归一化到触摸板表面的 `0..1`。

三指拖拽的判定：

- 恰好 3 个接触点；
- 30 ms 之后整体移动超过 `dragStartThreshold`；
- 三指基本是一起平移（离散度变化不超过移动量的 1.8 倍 + 0.003），否则视为非拖拽并放弃本次手势；
- 用 `SendInput` 绝对坐标移动光标并按下/抬起左键；像素换算 = 当前显示器工作区宽度的 90% × `dragSensitivity`。

### App Expose（四指向下 / 向上）

- 四指向下：进度 = `dy / exposeSwipeDistance`（默认 0.16），必须垂直占优（`exposeSwipeVerticalRatio` = 1.5）并且在 `exposeSwipeMaxMs` 内，动画跟着手指；
- 四指向上（App Expose 已打开）：进度 = `-dy / exposeSwipeDistance`，随手势倒放，松手时进度过半就关闭；
- 打开动画 650 ms，提交 600 ms，关闭 500 ms，缩略图回弹 160 ms，全部走 `ease-in-out`；
- overlay 是 `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`：**打开、点击空白、悬停、键盘选择都不会改变前台窗口**；只有点击缩略图或按 Enter/Space 才清除 `WS_EX_NOACTIVATE` 并把目标窗口像 Alt-Tab 一样置前（不设为置顶）；
- 缩略图是 DWM 缩略图，源窗口不移动、不改大小、不改 Z 序；
- 鼠标钩子只拦截左右键，**`WM_MOUSEMOVE` 永远放行**，所以 App Expose 打开时鼠标仍然能动；
- 键盘钩子处理 Tab/Shift+Tab、方向键、Enter/Space、Esc；按 Win、Win+Tab、Alt+Tab 等不相关快捷键会关闭 App Expose 并吞掉该按键，避免两个界面叠在一起。

过滤规则：

- 包含：普通顶层窗口、`WS_EX_TOOLWINDOW` 工具窗口、对话框（`#32770`）以及其他不在任务栏显示但有标题的窗口；
- 排除：桌面（`Progman`/`WorkerW`）、任务栏/副任务栏、开始菜单/搜索/小组件/Action Center 等 shell 窗口、Alt+Tab 视图、`#32768` 右键菜单、tooltip、阴影、NVIDIA GeForce Overlay/游戏栏等覆盖层；
- 只显示当前虚拟桌面、可见、未 cloaked 的窗口；最小化窗口也会列出，点击可还原并切换。

### App Expose 布局算法

- 视口取当前显示器工作区（`SPI_GETWORKAREA`），四周留 `padding = 32`，缩略图之间 `gap = 16`；
- 依次尝试 1..N 行，每行窗口数上限为 `ceil(N / rows)`，生成候选排列；
- 每个候选按 `fillRatio + 0.8*uniformity + 1.2*spatialFidelity + 0.3*stability - scalePenalty - 0.5*orphanPenalty` 评分：
  - `layoutScale` 在扣除 padding 和所有 gap 后，取能同时适配宽/高的最大缩放（上限为窗口原始大小的 60%）；
  - `spatialFidelity` 用窗口对原始中心向量与缩略图中心向量的夹角余弦平均值，保留桌面相对方位；
  - `orphanPenalty` 惩罚最后一行明显少于平均行数，避免“沙堆”；
- 选择最高分；`N = 1` 居中，`N = 2` 强制单行并排。

### Task View / 操作中心

- 不在 App Expose、也不在 Gestosh 显示桌面状态时，四指向上滑 = `Win+Tab`；
- 双指在触摸板右侧边缘（两指都从 x ≥ 0.82 区域开始）向左滑 = `Win+A`；
- 双指边缘滑动必须水平向左、超过约 0.045 归一化距离，并有 700 ms 时间窗和手势冷却；
- 如果 App Expose 正打开，双指边缘滑动会先关闭 App Expose，再打开操作中心。

### 四指左右滑动（切换虚拟桌面）

四指向左/向右滑会**跟着手指渐进地**切换到上一个/下一个虚拟桌面，接近系统自带的三指切换动画：

- 一开始就抓住当前桌面的**真实截图**，整张桌面随手指平移；
- 新桌面从另一侧推入，滑动中显示的是**共享壁纸 + 任务栏**（任务栏不跟着平移，和系统行为一致）；
- 实际切换发生在动画结束：滑过屏幕宽度约 **30%** 后松手才算切换，没滑够就滑回原位，当前桌面完全不动；
- 快速轻扫（≤ 220 ms 且滑过约 18% 屏宽）也算切换，避免短促滑动总要滑回；
- 只有 4 指、水平方向明显占优（水平位移 ≥ 垂直位移 × 1.5）并且超过 `desktopSwipeStartDistance` 才启动，所以四指上/下仍然分别是 Task View / App Expose，斜向滑动两个手势都不触发；
- App Expose 打开、贴边状态下不启动；
- 切换本身使用 `Win+Ctrl+Left/Right`，切换结果会用 `IVirtualDesktopManager` 回读验证，失败时写日志（例如只有 1 个桌面、前台窗口是管理员权限、按键注入被拦截）。

> 为什么动画里的新桌面只有壁纸和任务栏：Windows 不提供“渲染另一个虚拟桌面的实时内容”的接口，属于别的桌面的窗口也会被 DWM cloak。滑动过程中的桌面截图是真实的，**新桌面上的窗口会在切换完成的那一帧出现**——这是目前能拿到的最接近原生渐进动画的效果，且切换瞬间被遮住，不会看到硬切。

### 五指贴边 / 恢复

识别器在五个接触点稳定后记录一个基准离散度（RMS 到质心的距离）：

- 当前离散度 / 基准 ≥ `1.35`，且绝对变化 ≥ `0.045`：**贴边**
- 当前离散度 / 基准 ≤ `0.74`，且绝对变化 ≥ `0.045`：**恢复**
- 一次五指触摸只会触发一次；抬起后才能再次触发。

贴边动作会：

1. 枚举当前虚拟桌面上的可见顶层窗口（默认排除任务栏、桌面、开始菜单、工具窗口、全屏窗口等）；
2. 为每个窗口选择最近的**可用**屏幕边（工作区，避开任务栏；任务栏所在边不参与选择），并给已经用过该边的窗口加一点“负载惩罚”，让窗口尽量分散到四条边；
3. 同一条边上的多个窗口沿屏幕边依次排开，窗口之间保留 `stackStepPx` 的间隔；只有排不下时才允许部分重叠：每个窗口只露出 `exposePx`（上限 `maxExposePx`）；
4. 所有窗口同时做 `ease-out` 动画，只改位置、不改大小；
5. 恢复时反向动画，回到保存的矩形；原来是最大化的窗口最后重新最大化。

> 注意：贴边状态保存在内存中。正常从托盘退出会先恢复窗口；如果进程被任务管理器强制结束，窗口会停留在贴边位置，下次启动不会自动恢复（重新做一次贴边/恢复即可）。

窗口的 Z 序、焦点、激活状态都不会被改变（`SWP_NOZORDER | SWP_NOACTIVATE`）。

### 当前虚拟桌面

使用公开的 `IVirtualDesktopManager`：

```csharp
IsWindowOnCurrentVirtualDesktop(hwnd, &onCurrent)
```

贴边只作用于枚举时位于当前虚拟桌面的窗口。  
如果切换虚拟桌面后又做贴边，程序会把旧会话的窗口瞬间恢复到原位（在另一个桌面上，不可见），再对当前桌面重新贴边。恢复手势会恢复当前会话中的所有窗口，即使你已经切到别的桌面。

### 最大化窗口

Windows 不允许直接移动一个最大化的顶层窗口（`SetWindowPos` 只改恢复位置）。Gestosh 的做法是：

1. 用 `SetWindowPlacement(showCmd = SW_SHOWNORMAL, rcNormalPosition = 当前最大化外框)` 把窗口变成**外框大小完全相同**的普通窗口；
2. 参与贴边动画；
3. 恢复时先移动回原外框位置，再 `SetWindowPlacement(原 showCmd, 原 rcNormalPosition)` 重新最大化。

因此窗口内容大小在贴边过程中保持不变；只是在最大化/普通状态切换时可能出现一帧左右的非客户区边框差异，这是 Win32 的限制。

---

## 手势设置对话框

托盘菜单 → **“手势设置...”** 会打开一个设置窗口，用下拉框把每个手势映射到功能，不需要手改 ini：

| 手势 | 可选功能 |
| --- | --- |
| 三指 | 无 / 拖拽 |
| 四指向下滑动 | 无 / App Exposé / Windows 任务视图 |
| 四指向上滑动 | 无 / App Exposé / Windows 任务视图 |
| 四指左右滑动 | 无 / 切换桌面 |
| 双指由右侧向内滑动 | 无 / Windows 操作中心 |

- 点“确定”后写入 `Gestosh.ini` 的 `[gestures]` 段（保留注释和其他键），并立即重新加载生效；点“取消”不写入。
- 默认值就是现在的行为：三指拖拽、四指下 App Exposé、四指上任务视图、四指左右切换桌面、双指右边缘操作中心。
- 设为“无”时该手势完全不响应。注意系统自带的三/四指滑动仍由 Gestosh 接管（`manageThreeFingerGesture` / `manageFourFingerGesture`），不会自动退回给 Windows。
- 五个映射分别对应 `threeFingerAction` / `fourFingerDownAction` / `fourFingerUpAction` / `fourFingerHorizontalAction` / `edgeSwipeAction`；旧配置里的 `threeFingerDrag`、`desktopSwipe` 在没有新键时仍然有效（新键优先）。
- 五指贴边/恢复不在这个对话框里（它们不是“手势→功能”的二选一）。

---

## 配置

程序优先读取 **exe 同目录的 `Gestosh.ini`**；没有时读取 `%APPDATA%\Gestosh\Gestosh.ini` 并自动生成默认配置。  
日志默认写入 `%LOCALAPPDATA%\Gestosh\gestosh.log`，可用 `--log <file>` 覆盖。  
修改后可从托盘菜单选择“重新加载配置”，无需重启。

```ini
[general]
enabled=true
tray=true
manageThreeFingerGesture=true
manageFourFingerGesture=true
hotkeys=true

[gestures]
threeFingerDrag=true
dragSensitivity=1.0
dragStartThreshold=0.012
pinchFingers=5
pinchOutRatio=1.35
pinchInRatio=0.74
pinchMinTravel=0.045
pinchSettleMs=60
gestureCooldownMs=350
exposeSwipeDistance=0.16
exposeSwipeVerticalRatio=1.5
exposeSwipeMaxMs=900
desktopSwipe=true
desktopSwipeGain=1.0
desktopSwipeStartDistance=0.012
desktopSwipeCommitRatio=0.30
threeFingerAction=drag
fourFingerDownAction=expose
fourFingerUpAction=taskview
fourFingerHorizontalAction=desktop
edgeSwipeAction=actioncenter

[animation]
durationMs=340
fps=60
desktopSwipeSettleMs=170

[dock]
exposePx=42
stackStepPx=10
maxExposePx=220
includeToolWindows=false
includeMinimized=false
onlyProcesses=
excludeProcesses=
```

- `manageThreeFingerGesture` / `manageFourFingerGesture`：启动时把 Windows 自带的三指/四指滑动临时关闭，退出时恢复原值。
- `threeFingerAction` / `fourFingerDownAction` / `fourFingerUpAction` / `fourFingerHorizontalAction` / `edgeSwipeAction`：每个手势的功能映射，见 [手势设置对话框](#手势设置对话框)。
- `dragSensitivity`：三指拖拽的像素倍率；`dragStartThreshold`：开始拖拽前的归一化移动量。
- `pinchFingers`：贴边手势需要几个接触点（4 或 5；只有 5 点触摸板才支持五指手势）。
- `exposeSwipeDistance`：驱动 App Expose 的垂直距离；`exposeSwipeVerticalRatio`：垂直/水平比例要求。
- `desktopSwipe`：四指左右滑动切换桌面；`desktopSwipeGain`：1.0 = 划过一个触摸板宽度正好一屏；`desktopSwipeCommitRatio`：松手时切换所需的屏幕比例；`desktopSwipeSettleMs`：松手后的收尾动画时长。
- `exposePx`：贴边后每个窗口露出的像素（96 DPI 单位，按显示器 DPI 缩放）；`stackStepPx`：同边窗口间隔；`maxExposePx`：露出深度上限。
- `onlyProcesses` / `excludeProcesses`：逗号分隔的进程名（不带 `.exe`），用于只贴边/排除某些程序。

---

## 诊断与验证

### 控制台诊断

```text
dist\Gestosh.Debug.exe --diagnose 30
```

会打印操作系统、进程位数、`IVirtualDesktopManager` 是否可用、触摸板设备路径、preparsed data（报告 ID、输入报告长度、接触点数量、每个 Link Collection 的 X/Y 范围）、实时报告数量，以及识别到的手势事件（拖拽、App Expose、Task View、操作中心、桌面滑动、贴边/恢复）。诊断默认运行 30 秒。

### 内部自测

```text
dist\Gestosh.Debug.exe --selftest
```

**45 项全部通过**（结果同时写入 `tests/selftest.txt`），包括：真实设备的 preparsed data 合成 5 点 HID 报告解析（报告 ID 有无、报告长度消歧、空闲报告）、三指向下仍然拖拽、四指 App Expose 打开/提交/取消/上滑关闭、四指上滑打开 Task View、双指右侧边缘左滑打开操作中心、四指左右滑动切换桌面（方向、跟手更新、松手总位移、斜向不触发、overlay 不可用不重复尝试、App Expose 中不触发）、手势映射（配置解析、写回不丢其他键、映射为“无”时不响应、四指下开任务视图、四指上开 App Exposé），以及“任务栏边不参与贴边”的布局测试。

### 窗口集成测试

```powershell
# 需要先关闭正在运行的 Gestosh
tests\DockTest.exe
tests\ExposeTest.exe
tests\DesktopSwipeTest.exe
tests\SettingsDialogTest.exe
```

- `DockTest.exe`：窗口尺寸不变、动画有多个中间位置、恢复误差 ≤ 2px、最大化恢复、任务栏在底部时不贴底、同边窗口分开、点击贴边窗口可恢复、点击关闭按钮不会关窗。
- `ExposeTest.exe`：全屏覆盖窗口可打开、DWM 缩略图可见、背景壁纸加载、过渡中间帧、Esc 可关闭；截图在 `tests/artifacts/expose-mid.png` 和 `tests/artifacts/expose-test.png`。
- `DesktopSwipeTest.exe`：桌面滑动 overlay 覆盖整个虚拟屏幕、跟随手指更新偏移、偏移限制在一屏内、提交后只切换一次且方向正确、切换后 overlay 销毁、短距离滑动取消且不切换；截图在 `tests/artifacts/desktop-slide-*.png`。测试用空实现替代真实切换，不会真的切走桌面。
- `SettingsDialogTest.exe`：打开设置对话框、截图、点“确定”，验证回传的五个映射和窗口销毁；截图在 `tests/artifacts/settings-dialog.png`。

---

## 已知限制

1. **只支持 Windows Precision Touchpad**。老式 Synaptics/ELAN 私有驱动、把触摸板模拟成鼠标的设备、以及非 PTP 的触摸屏不适用。
2. **五指硬件要求**。PTP 规范允许 3–5 个接触点；只有 5 点触摸板才能做五指手势。4 点硬件无法做五指手势；四指 App Expose 不依赖五指。
3. **提升权限的窗口**：普通权限的 Gestosh 无法移动管理员权限窗口（UIPI）。需要覆盖这类窗口时，以管理员身份运行 Gestosh。App Expose 中管理员窗口的 DWM 缩略图也可能无法注册，显示为占位块。
4. **全屏窗口**（无标题栏、覆盖整个显示器）在贴边时会被跳过，避免破坏游戏/视频；App Expose 会列出这些窗口，但使用独占全屏/硬件覆盖层的程序可能显示黑块。
5. **最大化窗口**在贴边期间是“同尺寸普通窗口”，非客户区边框可能有一帧差异；恢复后会重新最大化。
6. **系统手势冲突**：`manageThreeFingerGesture` / `manageFourFingerGesture` 会在运行时把 `HKCU\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\ThreeFingerSlideEnabled` / `FourFingerSlideEnabled` 设为 0，并记录原值；正常退出时恢复。若程序被强制结束，下次启动会继续接管；也可以从托盘菜单“恢复 Windows 触摸板手势”立即恢复。
7. **虚拟桌面**：贴边按枚举时的当前桌面过滤；恢复跟随贴边会话，即使随后切换到其他桌面。
8. **动画**：窗口由其他进程绘制，个别程序可能在 `SetWindowPos` 时重绘/闪烁；恢复时只对尺寸确实变化的窗口做最终 resize，最后一帧同步落位，减少闪烁。
9. **App Expose** 使用 DWM 缩略图，个别使用硬件覆盖层/受保护表面的窗口可能显示黑块；拖拽只移动缩略图，不会移动源窗口；最小化窗口会列出，但缩略图可能为空，点击仍可还原并切换。桌面壁纸在启动时缓存；运行中更换壁纸或改变分辨率后需要重启 Gestosh 才能更新。
10. **Win+D**：贴边时按 Win+D，Gestosh 会先瞬间恢复窗口真实位置，再把按键交给 Windows；这会牺牲一次贴边动画，换取不会卡死。
11. **托盘图标**需要 Explorer 正常运行；没有托盘时仍可用热键和命令行。
12. 某些安全软件/沙箱会阻止 `Shell_NotifyIcon`、HKCU 写入或原始输入；请在正常用户桌面运行。
13. **桌面切换动画**只对当前桌面使用真实截图；新桌面的窗口无法在切换前渲染（DWM 会 cloak 其他桌面的窗口），所以滑动中显示的是壁纸 + 任务栏，窗口在切换完成时出现。切换使用 `Win+Ctrl+Left/Right`，前台窗口是管理员权限时按键注入可能被 UIPI 拦截；只有 1 个虚拟桌面时不会切换，日志里会有 `Desktop switch did not take effect`。

---

## 实现说明（为什么库很少）

- 单一 C# 代码库，不引用 NuGet，不使用 WinForms/WPF；只调用 `user32.dll`、`kernel32.dll`、`hid.dll`、`dwmapi.dll`、`shell32.dll`、`winmm.dll`、`gdi32.dll`、`gdiplus.dll`、`msimg32.dll`。
- 用 in-box 的 `csc.exe` 编译；运行时只需要 Windows 自带的 .NET Framework 4.8。
- 原始输入：`RegisterRawInputDevices(UsagePage=0x0D, Usage=0x05)` + `HidP_GetValueCaps` / `HidP_GetUsageValue` / `HidP_GetUsages`。
- 窗口管理：`EnumWindows` + `IVirtualDesktopManager` + `SetWindowPos` / `SetWindowPlacement`。
- App Expose：DWM 缩略图 + 内存位图 GDI 绘制（静态壁纸背景只画一次）。
- 桌面切换：`Win+Ctrl+Left/Right` + 自绘的滑动层（当前桌面截图 + 壁纸/任务栏合成）。
- 键盘/鼠标钩子：`WH_KEYBOARD_LL` / `WH_MOUSE_LL`，只用于 App Expose 的键盘导航和贴边状态的 Win+D 保护。

---

## 测试记录（本机）

本机环境：Windows 10（19044），Goodix `27c6:01e0` 精密触摸板，5 个接触点，报告 ID 4，输入报告 30 字节。

- `--selftest`：**PASS=40 FAIL=0**。
- `DockTest.exe`：全部通过（窗口尺寸不变、动画中间位置 ≥ 4、恢复误差 ≤ 2px、最大化恢复、任务栏边不贴底、点击贴边窗口可恢复、点击关闭按钮不会关窗）。
- `ExposeTest.exe`：全部通过（overlay 打开、DWM 缩略图可见、壁纸加载、过渡中间帧、Esc 关闭）。
- `DesktopSwipeTest.exe`：全部通过（overlay 几何、跟手偏移、范围限制、提交一次且方向正确、销毁、取消不切换）。
- `SettingsDialogTest.exe`：全部通过（对话框能打开且尺寸合理、五个下拉框显示当前映射、点“确定”回传的值与配置一致、保存后窗口销毁）；截图 `tests/artifacts/settings-dialog.png`。
- 启动测试：Release `Gestosh.exe` 可启动，检测到触摸板并注册 Raw Input，热键注册成功，`--exit` 正常退出。
- 版本资源：打补丁后 6 个语言的 `StringTable` 都能用 `VerQueryValue` 逐条读出“文件说明 + 备注”；`RT_ICON`（7 个尺寸）、`RT_GROUP_ICON`、`RT_VERSION`、`RT_MANIFEST` 资源齐全，内嵌 manifest 的字节与 `src\Gestosh.manifest` 完全一致。签名流程用 `sign.ps1 -SelfSigned` 验证过：签名可写入、可回读签名者和时间戳状态。

说明：DSH 的命令会话运行在低完整性环境里，**无法接收物理触摸板 Raw Input、无法添加托盘图标、无法写 HKCU**；`Gestosh.exe` 检测到低完整性会直接提示并退出。这些是会话隔离限制，不是程序逻辑错误；请在正常桌面会话中运行 `Gestosh.Debug.exe --diagnose 30` 做最终硬件验证。

---

## 常见问题

**Q：Windows SmartScreen 提示“Windows 已保护你的电脑”，或者提示发布者未知？**  
这是签名/信誉问题，不是程序损坏。先 `Unblock-File .\Gestosh.exe`（或右键 → 属性 → 解除锁定）；仍有提示就点“更多信息”→“仍要运行”。要彻底消除（尤其是分发给别人），需要公共 CA 的代码签名证书并长期使用，见 [程序元数据与数字签名](#程序元数据与数字签名)。

**Q：五指张开后窗口只是闪一下/没有贴边？**  
先看诊断输出 `active` 是否 5；如果只有 4，说明硬件或 Confidence 过滤导致。再检查 `pinchOutRatio` / `pinchMinTravel`。

**Q：三指移动变成了切换窗口？**  
Windows 自带三指滑动没有被关闭。确认 `manageThreeFingerGesture=true`，并检查日志中的 `Temporarily disabled ...` 或 `Cannot change ...`。也可以手动在“设置 → 蓝牙和其他设备 → 触摸板 → 三指手势”中关闭。

**Q：四指向下没有打开 App Expose？**  
先看日志里有没有 `4-finger swipe down`。确认 `manageFourFingerGesture=true`；四指系统滑动必须被临时关闭，否则 Windows 会同时最小化桌面。再检查 `exposeSwipeDistance` 是否太大、`exposeSwipeVerticalRatio` 是否太严格，或滑动是否超过了 `exposeSwipeMaxMs`。三指不再触发 App Expose，三指向下仍然是拖拽。

**Q：四指左右滑动没有切换桌面？**  
先看日志里有没有 `4-finger swipe left/right` 和 `Desktop switch: next/previous`。如果有 `Desktop switch did not take effect`，通常是只有 1 个虚拟桌面、前台窗口是管理员权限（UIPI 拦截按键注入），或者窗口处于贴边/App Expose 状态。滑动距离不够时只会弹回，这是设计行为：需要滑过约 30% 屏宽或快速轻扫。

**Q：四指左右滑动时新桌面的窗口是“突然出现”的？**  
Windows 不提供渲染其他虚拟桌面实时内容的接口，其他桌面的窗口在切换前会被 DWM cloak。滑动中的当前桌面是真实截图，新桌面先显示共享壁纸和任务栏，窗口在切换完成时出现；这已经是公开 API 下能做出的最接近原生渐进动画的效果。

**Q：App Expose 里某个窗口是黑块或没有出现？**  
黑块通常是使用独占全屏/硬件覆盖层/受保护表面的窗口，DWM 缩略图拿不到内容；管理员权限窗口在普通权限下也可能无法注册缩略图。NVIDIA GeForce Overlay、Xbox Game Bar、任务栏、桌面、右键菜单等会被主动排除。

**Q：贴边时按 Win+D 后窗口卡住？**  
新版在贴边状态安装了 Win+D 键盘钩子，会先把窗口恢复到真实位置再交给 Windows。确认日志里有 `Dock Win+D hook installed`；如果安装失败，点击贴边窗口恢复，或重新做一次贴边/恢复。

**Q：托盘没有图标？**  
先确认不是从聊天文件卡片/沙箱资源管理器启动的。正常桌面启动时，如果程序检测到自己处于低完整性（受限）权限，会弹出提示并退出，而不是留下后台进程。若确认是正常启动仍无图标，检查 Explorer 是否运行、托盘是否被安全软件阻止；热键和 `--dock/--restore/--exit` 仍可用。

**Q：`--dock` 提示 not running？**  
先启动 `Gestosh.exe`，再执行命令。

**Q：贴边后窗口被其他窗口挡住了？**  
本项目不改变 Z 序；贴边只是把窗口移到边缘，如果某个全屏/置顶窗口覆盖整个屏幕，它会挡住边缘的细条。可先最小化/移走该窗口，或在配置中排除它。

**Q：多显示器/不同 DPI？**  
每个窗口按自己显示器的 `rcWork` 和 DPI 计算；露出像素会按 DPI 缩放。

---

## License

Personal use; modify freely. No warranty.
