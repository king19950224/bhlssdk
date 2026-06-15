// FxDeviceSimulator: TCP 设备模拟器（独立 exe，Phase 2+ 完整实现）
//
// Phase 1 这个进程暂时空跑——业务侧的模拟器已经在 Api 进程内通过 SimulatedFxConnector 跑了。
// Phase 2 把这个改成"真正监听 TCP 端口、按厂家协议帧格式构造响应"的进程，
// 让 BoltPreload.Api 用 FxSdkConnector 连这个 exe，效果跟连真机一样。
//
// 这是 §9.16 SDK 供应商风险的兜底：模拟器独立于 SDK，按协议字节流自己实现，
// 万一 SDK 失联，模拟器可以作为参考实现。

Console.WriteLine("FxDeviceSimulator (Phase 1 占位实现)");
Console.WriteLine("Phase 1 业务侧用进程内 SimulatedFxConnector，不需要独立模拟器进程");
Console.WriteLine("Phase 2 将实现：TCP Server 监听 + 帧头 0xDE×4 + 命令码 0x21/0x22 + 模拟 ChannelResult/EchoWave");
Console.WriteLine();
Console.WriteLine("按 Enter 退出...");
Console.ReadLine();
