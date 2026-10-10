# 其他出入库单补空金额

双击项目根目录 `Repair-U8OtherVoucherAmounts.cmd`。选择其他出库或其他入库，输入单号（逗号分隔或逐行粘贴，空行结束），预览后输入 `SAVE` 保存。默认 008，一次最多 100 张同类型单；重复单号自动去重。服务器入口：`D:\MaterialOutboundApi\Repair-U8OtherVoucherAmounts.cmd`。

此脚本独立于 `Update-U8OtherVoucherPrices.cmd`，不依赖、不执行其补价或备注逻辑。

- 仅当金额 `iPrice` 为 NULL 且当前行数量、单价都有值时，按当前数量 × 当前单价补金额，四舍五入保留四位小数。
- 单价 0、数量 0 都是有效值，会补金额 0。已有金额（包括 0）保持不变；不重算不一致的非空金额。
- 缺单价或数量的空金额行跳过并列出原因，不查历史入库价，不推算单价。其余可处理行仍可保存，结果会提示跳过行数。
- 脚本只改金额和必要的修改标记，不改数量、单价、备注、批号、货位等字段；U8 原生保存可能执行自身字段规范化。
- 拒绝已审核、审批控制或版本变化的单据，不审核、不弃审、不直接 SQL 写库。预览仅查 SQL，不登录 U8。

源码端使用当前 Windows 账号的服务器管理共享/计划任务权限；服务器端以隐藏的 32 位 STA PowerShell 进程运行。凭据仅在 159 本机从 IIS 加密配置解密。无需更新 API DLL 或重启 IIS。

```powershell
# 只查，不保存
.\MaterialOutboundApi\Deployment\Repair-U8OtherVoucherAmounts.ps1 -VoucherType 09 -VoucherCodes 0000006569,0000006579 -PreviewOnly

# 预览后直接保存（明确使用 Apply 才跳过 SAVE 提示）
.\MaterialOutboundApi\Deployment\Repair-U8OtherVoucherAmounts.ps1 -VoucherType 08 -VoucherCodes 0000005831 -Apply
```

可选 `-Database 998`。08=其他入库，09=其他出库。

任务日志保留在 `D:\MupiOtherVoucherAmountJobs\<任务ID>`，包含预览快照、修改前及请求 XML、结果和保存尝试标记。U8 返回保存成功即结束，不再次查询单据。若超时或网络中断，先查 `Save-response.json`、每单目录内的 `save-attempted.txt` 与 `u8-save-success.txt` 并在 U8 核对，不要盲目新建任务重放。同一任务有保存尝试标记时拒绝再次保存。
