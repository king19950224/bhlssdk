# 商业化工业软件路线图

用于指导后续开发，不替代总体方案（plan.md）。

## 当前产品级别

当前状态：实验室 MVP → 商业试用版底座过渡。

已具备：

- 真机采集、EchoTime、预紧力计算。
- 扭矩阶梯实验和 torque_fit active calibration。
- Watchdog 自动恢复。
- Vue Dashboard。
- 基础告警。
- 参数变更复核流程（requested → approved → executed → verified，双人复核，见 0016 迁移与 ConfigChanges 页）。
- License 授权（RSA-SHA256 验签 + tools/LicenseSigner 签发，容量限额与写操作拦截）。

仍不具备：

- 多设备运行时完整管理。
- 正式载荷标定中心。
- 波形诊断中心完整 UI。
- Windows 离线安装包。

## 已经落地的商业化底座

1. `DeviceConnectorManager`
   - 已把单例 connector 构造逻辑收口。
   - 当前只有 primary runtime，但 API 和数据结构已按多设备设计。

2. `ChannelConfigService`
   - 可查看 DB 主配置。
   - 可对比 DB 与设备最近读回参数。

3. 参数变更请求表
   - `channel_config_change_requests`
   - `configure-channel` 已写入请求、执行、失败记录。

4. 波形管理一期
   - `wave_features`
   - `reference_waves`
   - 最近波形特征查询。
   - 设置参考波形。

5. 标定中心一期
   - `torque_fit` 支持截距。
   - 标定中心 summary 按 method/trustLevel 统计。

6. 工业部署底座
   - Windows Service 支持。
   - 安装/卸载脚本。
   - PostgreSQL 备份脚本。
   - 诊断包脚本。

7. License 授权底座
   - 机器码接口 `/api/license/machine-code`。
   - 授权状态接口 `/api/license/status`。
   - 离线 `license.json` 模板。
   - runtime 接口暴露当前设备数/通道数与授权上限。
   - RSA-SHA256 签名校验已落地（LocalFileLicenseService 验签 + tools/LicenseSigner 签发）。

## 下一轮建议

优先级（参数审批与 License 签名已交付，从清单销掉）：

1. 多设备动态加载：从 `devices` 表加载多个 runtime。
2. 波形诊断 UI：A-Scan、阈值、零交叉、SNR。
3. 标定中心 UI：标定点、拟合线、报告导出。

## 后续硬件升级项：双波法（±3%）

现状是单波法 ±5%，双波法受硬件与厂家 SDK 双重阻塞，不在软件迭代范围内。要推进需按顺序办三件事：

1. 厂家在 model=3 多通道监测设备的固件与 SDK 上补横波支持（横波窗口/位置参数下发 + 声时2解码）。
   现有实现只在 model=0/1 单通道仪器上有，查证细节见 `资料/工程文档/sdk-api-facts.md` §13。
2. 现场换装纵横波复合探头，按被监测螺栓数逐颗更换。
3. 软件侧补 K_T、v_T0、x₀、A_L−A_T 字段与对应标定流程，`GbBiWaveCalculator.IsAvailable` 才能置 true。

第 1 项是关键路径，且属供应商依赖，建议先按 plan §9.16 走协议问询与合同约定，再评估第 2 项的采购成本。

## 商业风险提醒

- `torque_fit` 仍是扭矩法临时拟合，不能作为正式安全验收。
- 正式商用必须至少支持 `load_calibrated` 或符合 GB/T 43232 的标定流程。
- 参数修改、标定应用、告警确认必须可审计、可追责。
- 对外精度承诺按 ±5%（单波法）。说明书若仍写 ±3%，属超出实际能力的承诺，交付前必须改。
