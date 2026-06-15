# FxSDK 多通道采集示例（C#）

iFast 超声设备 C# SDK v1.4.0，含多通道监测版（Model = 3）采集示例工程。

## 目录结构

```
FxSDK.MultiChannelDemo/
├── FxSDK.MultiChannelDemo.sln
├── README.md
├── build.sh / build.cmd
└── src/
    ├── FxSDK/                       SDK 源码（net6.0）
    │   ├── FxSDK.csproj
    │   ├── FxClient.cs / FxCommunication.cs
    │   ├── Codec/                   协议编解码
    │   ├── Core/                    TCP / 串口会话
    │   ├── Models/                  设备 / 通道 / 结果模型
    │   └── Utility/                 计算 / 滤波 / AGC
    └── MultiChannelDemo/            采集示例（net8.0 Console）
        ├── MultiChannelDemo.csproj
        └── Program.cs
```

## 环境

- .NET 8 SDK（[下载](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)）
- Windows / Linux / macOS 均可。串口模式（CommunicationType = 0）依赖 `System.IO.Ports`，仅 Windows 与 Linux 支持；TCP 模式全平台可用。

## 编译

```bash
dotnet build FxSDK.MultiChannelDemo.sln -c Release
```

也可直接执行 `./build.sh`（macOS / Linux）或 `build.cmd`（Windows）。

## 运行

修改 `src/MultiChannelDemo/Program.cs` 顶部的连接参数：

```csharp
const string DeviceEndpoint    = "192.168.0.7:23";  // 设备 IP:端口
const byte   CommunicationType = 1;                 // 0=串口  1=TCP Client  2=TCP Server
const ushort DeviceAddress     = 1;                 // 终端地址
```

`CommunicationParameters` 格式：

- TCP Client：`远端IP:端口`，如 `192.168.0.7:23`
- TCP Server：`远端IP:端口,本机监听端口`，如 `192.168.0.7:23,6000`
- 串口：`COM3:115200,0,8,1`（端口:波特率,奇偶,数据位,停止位）

启动：

```bash
dotnet run --project src/MultiChannelDemo -c Release
```

控制台会持续输出通道 1/2/3 的回波声时、声时差、温度、波形点数：

```
[Connected] 会话 tcp://192.168.0.7:23 已连接，开始下发配置...
  ↪ 通道 1 已配置: 螺栓 82mm, EchoWin=27000ns, Gain=10, Thr=165, RefTime=33858.6ns @ 15.6℃
  ↪ 通道 2 已配置: 螺栓 80mm, EchoWin=27000ns, Gain=8,  Thr=-223, ...
  ↪ 通道 3 已配置: 螺栓 82.5mm, EchoWin=28000ns, Gain=2, Thr=0,    ...
[Connected] 多通道测量已启动，等待数据...
[Data] 09:30:12.345 addr=1 ch=1 status=0 echo=33860.2ns ΔTOF=1.6ns temp=15.7℃ wavePts=120
[Data] 09:30:12.450 addr=1 ch=2 status=0 echo=32638.1ns ΔTOF=1.1ns temp=15.7℃ wavePts=120
[Data] 09:30:12.555 addr=1 ch=3 status=0 echo=36814.3ns ΔTOF=1.5ns temp=14.9℃ wavePts=120
```

按 **Ctrl+C** 退出，会自动调用 `StopAllAsync()` 关闭所有会话。

## 示例覆盖的主要 API

- `FxClient.ConnectAsync` / `CloseAsync` / `StopAllAsync`
- `ConfigureDeviceAsync`（下发 `MeasConfig` / `MCRConfig` / `NetCheckConfig`）
- `ConfigureChannelAsync`（完整配置 + 单属性细粒度更新，例如只改增益）
- `SetDeviceModeAsync`（`Standby` / `MultiMeas` / `SingleMeas` / `FullWaveRead` …）
- `ReadDeviceParamAsync` / `ReadChannelParamAsync`
- 事件 `OnConnected` / `OnDisconnected` / `OnResultReceived`
- `CalculateExtensions.CalcEchoWin` / `GetSyncTimeBase2000`

更多 API 说明见 `src/FxSDK/FxClient.cs` 中的中文 XML 注释。

## 进阶用法

下面三段在 `Program.cs` 中以注释或调用方式标出，按需启用：

- **自动增益控制（AGC）**：
  `FxClient.RunAgcControlAsync(sessionId, channelConfig, MultiChIotProtocolHandler.SignalAuto, MultiChIotProtocolHandler.GainRange, MultiChIotProtocolHandler.GainStep, 20)`
- **单通道全波抓取**：
  `SetDeviceModeAsync(sessionId, WorkMode.FullWaveRead, channel, address)`
- **参考回波写回设备 Flash**：
  `channelConfig.LEchoWave = waveFromResult; await ConfigureChannelAsync(sessionId, channelConfig, nameof(ChannelConfig.RefTime), true);`

## 常见问题

- **`CommunicationException: Connection refused`** —— 设备 IP / 端口不对，或设备未上电。
- **能连上但 `OnResultReceived` 没数据** —— 检查是否切到 `WorkMode.MultiMeas`，以及 `MCRConfig.EnabledChannels` 是否为空。
- **`status = -1`（信号过小）/ `status = -3`（信号溢出）** —— 增益不合适，启用 AGC 或手动调 `Gain`。
- **温度变化大但 `ΔTOF` 没补偿** —— 通道 `TcMode = 0` 关掉了温度补偿，改为 `1`（材料系数）或 `2`（自定义系数）。

## 设备型号约定（`DeviceConfig.Model`）

| 取值 | 型号 |
| --- | --- |
| 0 | 标准版 |
| 1 | 专家版 |
| 2 | 实时 24 通道版 |
| 3 | 多通道监测版（LoraMesh，本示例使用） |

## 工作模式约定（`WorkMode`）

`Standby` 待机 ／ `SingleMeas` 单通道 ／ `MultiMeas` 多通道 ／ `DualMeas` 双波 ／ `FullWaveRead` 全波读取 ／ `LWaveRead` 纵波扫波 ／ `TWaveRead` 横波扫波 ／ `ReadParam` 读参 ／ `Reboot` 重启。

## 版本

- FxSDK：`1.4.0`
- 示例工程：`net8.0`
- SDK 库：`net6.0`

## 版权

(c) Beijing iFast Technology Co., Ltd. All rights reserved.
