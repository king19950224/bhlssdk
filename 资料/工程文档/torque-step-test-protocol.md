# M20×80 扭矩阶梯实验操作单

> 目的：在没有拉伸机/载荷传感器的情况下，先检查"扭矩变化 → 超声声时变化"是否具备稳定线性响应。
> 这不是正式标定，正式验收仍需多点已知载荷标定。

## 一、实验对象

- 螺栓：M20×80
- 探头/通道：device 1 / channel 9
- 扭力扳手：最大 200 N·m
- 临时扭矩系数：K = 0.20
- 开始前先确认系统状态（EchoTime 接受率、Watchdog）：查 `real-health` 接口（见下文第三节）

## 二、建议扭矩点

按从低到高做：

| 步骤 | 扭矩 | 保持时间 | 记录接口 |
|---|---:|---:|---|
| 1 | 50 N·m | 60 秒 | `POST /api/channels/1/torque-test-points` |
| 2 | 100 N·m | 60 秒 | 同上 |
| 3 | 150 N·m | 60 秒 | 同上 |
| 4 | 200 N·m | 60 秒 | 同上 |

（历次实测结果不记在操作单里，见 lab-torque-step-report-*.md 系列报告。）

## 三、每个扭矩点的操作

1. 将螺栓调整到指定扭矩。
2. 不要立即采点，先等待 15~30 秒，让机械和耦合状态稳定。
3. 确认健康接口：

```bash
curl http://localhost:5000/api/devices/1/diagnostic/real-health?channel=9&minutes=1
```

重点看：

- `healthy = true`
- `echoRatePct >= 95`
- `withEcho` 足够多

4. 采集实验点：

```bash
curl -X POST http://localhost:5000/api/channels/1/torque-test-points \
  -H 'Content-Type: application/json' \
  -d '{
    "torqueNm": 50,
    "nutFactor": 0.20,
    "nominalDiameterMm": 20,
    "sampleWindowSeconds": 60,
    "minSamples": 20,
    "note": "M20×80，扭矩阶梯实验 50 N·m 稳定点"
  }'
```

把 `torqueNm` 换成 100 / 150 / 200。

## 四、查看结果

查看实验点：

```bash
curl http://localhost:5000/api/channels/1/torque-test-points
```

查看线性拟合摘要：

```bash
curl http://localhost:5000/api/channels/1/torque-test-points/summary
```

解释：

- `pointCount < 3`：只能记录，不能判断线性；
- `rSquared` 越接近 1，扭矩-超声响应越线性；
- `kLByLinearFitNsPerN` 是基于扭矩估算预紧力得到的临时 K_L；
- 若点与点之间不单调，优先检查耦合剂、探头压紧、螺栓是否松动、扭矩是否回退。

## 五、注意事项

- 每次调扭矩时尽量不要碰探头和耦合层。
- 扭矩从低到高做，避免来回卸载导致摩擦滞回。
- 如果必须回退扭矩，记录清楚，因为扭矩-预紧力有明显滞回。
- 扭矩法误差大，K=0.20 只是工程经验值；本实验用于看趋势和线性，不作为安全验收结论。
