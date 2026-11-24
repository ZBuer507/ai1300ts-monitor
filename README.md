# Ai1300TS 电源与显卡监控

[![build](https://github.com/ZBuer507/ai1300ts-monitor/actions/workflows/build.yml/badge.svg)](https://github.com/ZBuer507/ai1300ts-monitor/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/ZBuer507/ai1300ts-monitor?display_name=tag&sort=semver)](https://github.com/ZBuer507/ai1300ts-monitor/releases/latest)
[![license](https://img.shields.io/github/license/ZBuer507/ai1300ts-monitor)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6)](#)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)](#)

两个独立运行的 Windows 托盘程序（.NET Framework 4.8 / WinForms）：直接通过 USB HID 读取
MSI MPG Ai1300TS（PCIE5）电源的实时数据；GPU 版另外通过 NVAPI I2C 读取
ASUS ROG Astral GeForce RTX 5090 Edition 20（32GB，型号
`ROG-ASTRAL-RTX5090-P32G-EDITION20`）板载重力传感器，给出俯仰/横滚角与相对基准的偏移。

电源协议来自对 MSI「UC_Power Supply Unit」WPF 插件的 IL 逆向，整理后的协议要点见下文；
原始 IL 导出属于第三方程序内容，不随仓库发布。

## 界面

托盘悬停卡（自绘，按系统 DPI 缩放）：

<img src="docs/gpu-tray-card.png" width="380" alt="托盘悬停卡">

主窗口（4K / 250% 缩放）：

<img src="docs/gpu-monitor-window.png" width="760" alt="主窗口">

## 下载

最新版本见 [Releases](https://github.com/ZBuer507/ai1300ts-monitor/releases/latest)：

| 产物 | 版本 | 说明 |
| --- | --- | --- |
| `Ai1300TS-PsuMonitor.exe` | 1.1.3.0 | 仅电源监控，托盘提示为文本卡片 |
| `Ai1300TS-GpuMonitor.exe` | 1.1.3.0 | 电源 + 显卡姿态，托盘悬停为自绘悬浮卡 |

两个 exe 放到同一目录即可运行，互不依赖；构建产物不入库。

## 仓库结构

```
build.ps1              构建 / 测试 / 部署脚本
src/                   产品源码（两个 exe 共用一套源码）
  PsuMonitor.cs        入口、主窗口、轮询、托盘、启动项（#if GPU_LEVEL 双产物）
  MonitorGpuLevelPanel.cs  GPU 面板 partial 类（基准、0.30° 提示）
  MonitorTrayPopup.cs   GPU 版托盘悬浮卡（GDI+ 自绘）
  GpuLevelSense.cs      NVAPI I2C 姿态读取与角度解算
  PsuTrayHover.cs       托盘悬停触发器（文本弹窗 / 悬浮卡两种实现）
  PsuHidTransport.cs    HID 打开、握手、寄存器读写、取消与重连
  PsuTelemetry.cs       0xE0/0xC1 报文解码
  PsuStartup.cs         登录启动注册与系统禁用检测
  assets/               图标与应用程序清单
tests/                  自检与冒烟测试（与产品源码一起编译）
tools/                  辅助工具（图标生成、显卡姿态探针）
docs/                   截图、发版说明
.github/workflows/      CI（构建检查）与发版（tag → Release）
artifacts/              构建输出（不入库）
work/                   本地逆向资料与旧构建（不入库）
```

## 架构

单份源码编译出两个 exe，靠 `GPU_LEVEL` 编译符号切换：

- `PsuMonitor.cs`：产品名、窗口尺寸、数据行数、托盘内容；
- GPU 版额外链接 `MonitorGpuLevelPanel.cs`、`GpuLevelSense.cs`、`MonitorTrayPopup.cs`，
  `PsuTrayHover` 的悬浮实现也随之切换为 `MonitorTrayPopup`。

### 电源采集

- USB HID `0DB0:AA6F`，Usage Page `FF00`，报文 65 字节（首字节为报文号）。
- 握手 `FA/51`；读寄存器用 `51 + 寄存器号`：`0xE0` 遥测（22 个 16 位字），
  `0xC1` 接口告警（偏移 3 / 24 两个字节）。
- 与 MSI 软件共用 `Global\MSI_PSU_Mutex`（等待 2000 ms）；overlapped I/O +
  `CancelIoEx`，取消未被内核确认前不重连（`pendingCancellation`）。
- 每个字 = 11 位无符号尾数 × 2^(有符号 5 位指数)；总线忙/全 FF 回包会被拒绝。
- 输入功率为估算值：`输出功率 × 100 / 效率`。
- 输出：功率、效率、温度、风扇、+12/+5/+3.3V 电压电流、2 路 12V-2x6 各 6 针电流。

### 显卡姿态

- `nvapi64.dll!nvapi_QueryInterface` 动态绑定 `NvAPI_Initialize/Unload`、
  `EnumPhysicalGPUs`、`GetPCIIdentifiers`、`GetFullName`、`I2CReadEx`。
- 目标卡：ASUS ROG Astral GeForce RTX 5090 Edition 20（`ROG-ASTRAL-RTX5090-P32G-EDITION20`），
  `DEV 2B85 / VEN 10DE`，`SUBSYS 1043:8A8C`；同型号多卡直接报错，避免读错传感器。
- I2C 地址 `0x2B << 1`、Port 1；芯片 ID 寄存器 `0x21 = 0x89`；重力向量从 `0x70` 起 6 字节，
  大端有符号 16 位。
- `Pitch = atan2(X, -Z)`，`Roll = atan2(Y, -Z)`；与基准比较用两向量夹角，竖装/倒装均可用。
- 基准存 `HKCU\Software\Ai1300TS-GpuMonitor\LevelBaseline_8A8C1043`（3 个 int16），
  偏移 ≥ 0.30° 变色提示。

### 交互

- 单实例 `Local\Ai1300TS.*.Instance` + `Local\Ai1300TS.*.OpenWindow` 事件：
  重复启动会唤起已运行实例的窗口；`--replace-instance` 用于提权重启时等待旧实例退出。
- 关闭窗口 = 隐藏到托盘；托盘菜单退出；GPU 版支持 `Ctrl+Q`。
- 登录启动写 HKCU Run，并读取 `StartupApproved\Run` 识别被 Windows 设置禁用的情况。
- GPU 版托盘悬停 350 ms 后显示悬浮卡（逻辑尺寸 432×424，按系统 DPI 缩放），
  PSU 版显示文本弹窗。

## 构建

需要 .NET Framework 4.8 的 `csc.exe`（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`），
目标 `winexe`、x64，无 NuGet 依赖。

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1                     # 只构建
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test               # 构建 + 全部测试
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Test -NoHardware   # 仅逻辑测试（CI 用）
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Deploy -DeployRoot "E:\file"   # 构建 + 部署
```

`-NoHardware` 会跳过需要本机电源/显卡的两个 GUI 冒烟测试；GitHub Actions 使用它做构建检查。
`-Deploy` 会停止部署目录下正在运行的实例、覆盖 exe、以 `--background` 隐藏启动，
并校验进程存活且没有主窗口。`-DeployRoot` 在 `-Deploy` 时必填。

构建结果与 SHA-256 记录在 `artifacts/build-info.txt`。

## 测试

| 测试 | 覆盖 | 是否需要硬件 |
| --- | --- | --- |
| `PsuTelemetrySmoke` | 0xE0/0xC1 解码、指数/尾数、忙与全 FF 回包 | 否 |
| `GpuLevelSenseSmoke` | NVAPI x64 结构布局、角度/偏移解码、竖装与倒装 | 否 |
| `PsuCancellationAudit` | 100 次取消挂起后单请求释放（反射） | 否 |
| `PsuDesktopSmoke` | 真实 HID、隐藏窗口、模拟悬停、托盘文本、截图 | 是 |
| `GpuMonitorTraySmoke` | 真实悬停路径、悬浮卡按系统缩放渲染（1080×1060 @2.5x） | 是 |

测试程序与报告输出到 `artifacts/tests/`。两个 GUI 冒烟测试使用与产品相同的 DPI 清单编译，
按真实系统缩放验证。PSU 的登录启动项归 GPU 版所有，因此 PSU 冒烟测试只在
“登记存在时必须指向 PSU 版 exe”时校验注册。

## 工具

```powershell
# 生成图标（ico 已提交，一般无需重跑）
csc /nologo /target:exe /out:BuildPsuIcon.exe tools\BuildPsuIcon.cs
BuildPsuIcon.exe src\assets\PsuMonitor.ico
BuildPsuIcon.exe src\assets\GpuMonitor.ico gpu

# 显卡姿态探针（读取 5 次并打印原始向量）
csc /nologo /platform:x64 /out:GpuLevelProbe.exe src\GpuLevelSense.cs tools\GpuLevelProbe.cs
GpuLevelProbe.exe
```

## 部署与回滚

- 部署：`build.ps1 -Deploy -DeployRoot <目录>`，同时更新 PSU 与 GPU 两个 exe，并重启 GPU 版。
- 回滚：覆盖回旧版 exe 后重启进程；`work/` 内保留了历史构建备份。
- 部署后可用 `Get-Process Ai1300TS-GpuMonitor` 与注册表 Run 项确认状态。

## 发版

打 tag 后由 `.github/workflows/release.yml` 自动构建并创建 Release（附件为 zip 和两个 exe，
说明取自 `docs/releases/<tag>.md`）：

```powershell
git tag -a v1.1.2 -m "Ai1300TS Monitor v1.1.2"
git push origin v1.1.2
```

首次配置远程仓库与认证的步骤见 [docs/publish.md](docs/publish.md)。

## 已知限制与风险

1. 托盘悬停位置依赖反射 `NotifyIcon` 私有字段 `id`/`window` 调用 `Shell_NotifyIconGetRect`，
   .NET 运行时升级可能失效；失效时退化为光标附近 20px 的触发区。
2. 清单声明的是 System DPI 感知，不是 per-monitor v2；多显示器不同缩放时悬浮卡定位可能偏移。
3. 外部工具用 `WM_CLOSE` 关闭窗口会被 WinForms 判为 `TaskManagerClosing`，进程直接退出；
   用户点 X（`SC_CLOSE`）是隐藏。属通用行为，未做特殊处理。
4. 电源协议与显卡传感器均依赖特定型号（MPG Ai1300TS、ROG Astral RTX 5090 P32G Edition 20），
   换硬件需重新适配。
5. exe 未签名，首次运行或被移动后可能触发 SmartScreen。

## 许可证

[MIT](LICENSE) © 2026 zheng.jiatao
