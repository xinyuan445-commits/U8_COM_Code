# 其他出入库单逐行改单价

双击项目根目录 `Edit-U8OtherVoucherLinePrice.cmd`。服务器入口是 `D:\MaterialOutboundApi\Edit-U8OtherVoucherLinePrice.cmd`。

1. 选择其他出库或其他入库，输入单号和 U8 明细行号。
2. 脚本显示该行料号、名称、批号、货位编码、数量、原单价和金额。
3. 输入新单价（允许 0），选择 Y 同步重算本行金额，或 N 只改单价。
4. 核对预览的新旧值，输入 `SAVE` 保存；其他输入取消，不审核。

默认 008，一次只改一张单的一行。行号是 `irowno`，不是 AutoID，也不是当前列表第几条；缺失或重复行号拒绝处理。

- 新单价须非负，最多 18 位整数及 10 位小数。
- 选择只改单价时，金额保持原值（包括 NULL、0 或非零值）。选择重算时，该行金额按当前数量 × 新单价四舍五入到四位小数，会覆盖原金额；缺数量或金额超出 money 范围则拒绝保存。
- 不查最近入库价，不改备注、数量、批号、货位等字段，也不处理其他行。原生 U8 保存仍可能执行自身字段规范化。
- 只允许未审核单。预览与保存之间单据发生变更、原生 Load 身份或状态不符时拒绝；保留原生 XML Schema 和时间戳，经 U8 原生 Load/Update 保存，不直接 SQL 写库，不审核或弃审。

```powershell
# 只预览其他出库第1行改为114.73，金额保留
.\MaterialOutboundApi\Deployment\Edit-U8OtherVoucherLinePrice.ps1 -VoucherType 09 -VoucherCode 0000006569 -LineNo 1 -UnitPrice 114.73 -AmountMode Keep -PreviewOnly

# 预览其他入库第3行改为176.995，同时重算该行金额；不保存
.\MaterialOutboundApi\Deployment\Edit-U8OtherVoucherLinePrice.ps1 -VoucherType 08 -VoucherCode 0000005831 -LineNo 3 -UnitPrice 176.995 -AmountMode Recalculate -PreviewOnly
```

可选 `-Database 998`；08=其他入库、09=其他出库。命令行未指定 AmountMode 时默认为 Keep；双击交互运行会要求选择 Y/N。`-Apply` 表示用户明确跳过 SAVE 提示，仍先查询与预览。不要直接执行保存示例测试生产单据。

源码端使用当前 Windows 管理共享和计划任务权限，在 159 的隐藏 32 位 STA PowerShell 中执行；服务器端直接启动本机工作进程。密码仅在服务器从 IIS 的 LocalMachine DPAPI 配置解密，不发送回客户端。无需改 API DLL 或重启 IIS。

日志在 `D:\MupiOtherVoucherLinePriceJobs\<任务ID>`，包含查询、预览、修改前/请求 XML 和保存结果。U8 返回成功即结束，不再次查询单据。网络中断或超时后，先检查 `Save-response.json`、每单目录的 `save-attempted.txt` 和 `u8-save-success.txt`，在 U8 核对后再决定后续动作，不要盲目重放未知结果。同一任务有保存尝试标记则不能再次保存。
