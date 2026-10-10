# 通用盘点单 API

本项目是独立源码程序集，发布时与 `MaterialOutboundApi` 放在同一 IIS 应用中。它使用独立路径，不改变原材料出库接口。

## 接口

- `POST /api/v1/general-inventory-count-orders/validate`：只读校验，不生成单据。
- `POST /api/v1/general-inventory-count-orders`：新增盘点单。
- `GET /api/v1/general-inventory-count-orders/{voucherCode}`：按盘点单号查询。
- `POST /api/v1/general-inventory-count-orders/{voucherCode}/actual-quantities`：按行号修改未审核盘点单的实盘数量。

请求头：

- `X-API-Key`：必填，与现有材料出库 API 共用。
- `X-U8-Database`：必填，允许 `UFDATA_008_2021`、`UFDATA_888_2021` 或 `UFDATA_998_2021`。
  `UFDATA_888_2021` 仅对盘点单接口开放，不扩展到其他业务接口。
- `Idempotency-Key`：新增时必填，长度 8～64；同一个业务请求重试时必须保持不变。
- `Content-Type: application/json`

## 新增示例

```http
POST /api/v1/general-inventory-count-orders HTTP/1.1
X-API-Key: <现有 API Key>
X-U8-Database: UFDATA_008_2021
Idempotency-Key: inventory-count-20260927-001
Content-Type: application/json
```

```json
{
  "externalRequestNo": "inventory-count-20260927-001",
  "voucherDate": "2026-09-27",
  "countDate": "2026-09-27",
  "warehouseCode": "KML_11_SY",
  "inboundRdCode": "107",
  "outboundRdCode": "207",
  "countType": "普通仓库盘点",
  "memo": "API 盘点",
  "autoVerify": false,
  "lines": [
    {
      "lineNo": 1,
      "inventoryCode": "21-MOB-B2-4908-HYA8064-3-B",
      "batchCode": "20260721JP",
      "actualQuantity": 5,
      "reason": "现场实盘"
    }
  ]
}
```

`bookQuantity` 不传时，接口按仓库、存货、批号、货位和自由项读取 U8 当前结存；传入时按传入值制单，同时在校验结果中提示它与当前结存的差异。`actualQuantity` 是实盘数量，可为 0。启用存货自由项管理的存货必须在 `freeFields` 中提供对应值；例如 U8 将自由项 1 命名为“存货等级”时，应传 `freeFields.free1`。使用辅计量单位的存货会从 U8 存货档案自动取得辅单位和固定换算率，并从当前结存 `iNum` 取得账面辅数量；调用方可以显式传入对应字段覆盖数量，但不需要手工查找辅单位。启用保质期管理的存货未传日期时，接口默认将“有效期至”设为盘点单日期所在年份的 `12月31日`，将 U8 “失效日期”设为次年 `1月1日`，自动带入档案中的保质期和单位，将有效期推算方式设为 `2`，并按“有效期至 − 保质期 + 1 天”计算生产日期。`autoVerify` 默认 `false`；设为 `true` 可能在 U8 审核时产生盘盈/盘亏调整单。

## 修改未审核盘点数量

`POST /api/v1/general-inventory-count-orders/{voucherCode}/actual-quantities` 按 `iRowNo` 修改实盘数量。接口先通过 U8 `VoucherCO.Load` 读取整张原始单据，只替换目标行的实盘数量，并自动重算数量差异、固定换算率辅数量、辅数量差异、实盘金额和差异金额，再通过 `VoucherCO.Update` 保存。已审核单据、找不到的行号、重复行号和负数数量会被拒绝。

```json
{
  "lines": [
    {
      "lineNo": 5,
      "actualQuantity": 8,
      "expectedActualQuantity": 0
    },
    {
      "lineNo": 8,
      "actualQuantity": 10,
      "expectedActualQuantity": 12
    }
  ]
}
```

`expectedActualQuantity` 是可选的并发保护值。交互脚本会自动传入查询时的原实盘数量；如果其他操作员已经修改该行，接口返回冲突并要求重新查询，避免覆盖新数据。

## 修改盘点明细（1.4.8）

`POST /api/v1/general-inventory-count-orders/{voucherCode}/line-updates`，请求头仍使用 `X-API-Key`、`X-U8-Database`。先 GET 查询目标行，带回 `lineVersion` 作为 `expectedLineVersion`。

```json
{
  "lines": [{
    "lineNo": 5,
    "expectedLineVersion": "从查询响应复制的lineVersion",
    "actualQuantity": 8,
    "updateBatchCode": true,
    "batchCode": "20260821",
    "updatePosition": true,
    "positionName": "1101",
    "updateReason": true,
    "reason": "复盘更正",
    "updateRemark": true,
    "remark": ""
  }]
}
```

`actualQuantity` 必填；不改数量时传查询得到的原值。数量允许0，不能为负。文本字段只有对应 `update…=true` 才修改；省略标记和值即保留，标记为true且值为空会清空。原因最多120字、备注255字、批号60字；批次管理存货批号不能清空。货位按本仓库的名称唯一解析，不接受空名称或停用名称，不列候选货位。

修改接口保留原账面数量、自由项、保质期与批次属性，不因换批号或货位自动重新提取库存；不是库存调拨操作。修改后若与其他行的存货、批号、货位及全部自由项重复会拒绝。关联收发记录、独立货位分配、表头限定货位、审批控制或已有调整量等情况须在 U8 中处理。实际保存由 U8 最终校验。

旧 `/actual-quantities` 路由继续兼容；使用新增文本字段时同样必须带行版本。版本或查询数量发生变化返回409，须重新查询再编辑。接口成功后回读目标字段；超时、网络异常或 `U8_UPDATE_RESULT_UNCONFIRMED` 不表示保存失败，应先查询或在 U8 中核对，不能盲目重发。

交互入口：`D:\MaterialOutboundApi\Edit-U8InventoryCount.cmd`（源码根目录也有同名入口）。输入单号和行号后选择 `1`数量、`2`批号、`3`货位名称、`4`原因、`5`行备注；可连续编辑多个字段、多行。`0`进入下一行、`8`撤销本行，回车跳过或结束，`-`清空可空文本。最终输入 `UPDATE` 才提交，取消不会改动单据。

## 表头字段

| JSON 字段 | 类型 | 必填 | 默认/说明 |
|---|---:|:---:|---|
| externalRequestNo | string | 是 | 调用方业务流水号 |
| voucherDate | yyyy-MM-dd | 是 | 单据日期，年份须与 U8 登录年度一致 |
| countDate | yyyy-MM-dd | 否 | 盘点日期，默认 voucherDate |
| voucherCode | string | 否 | 不传时按 U8 类型 `18/0307` 自动编号 |
| warehouseCode | string | 是 | 仓库编码 |
| departmentCode | string | 否 | 部门编码 |
| inboundRdCode | string | 否 | 盘盈入库类别，默认 `107` |
| outboundRdCode | string | 否 | 盘亏出库类别，默认 `207` |
| personCode | string | 否 | 业务员编码 |
| maker | string | 否 | 制单人，默认服务器配置的制单人/U8 用户 |
| source | string | 否 | 单据来源，默认 `1` |
| countBatch | string | 否 | 盘点批号 |
| positionCode | string | 否 | 表头货位；明细未传货位时继承此值 |
| countType | string | 否 | 默认 `普通仓库盘点` |
| accountingPeriod | string | 否 | 盘点会计期间 |
| memo | string | 否 | 备注 |
| voucherTemplateId | int | 否 | U8 单据模板号，默认 `29` |
| autoVerify | bool | 否 | 默认 `false` |
| definedFields | object | 否 | 表头自定义项 1～16 |
| lines | array | 是 | 1～1000 条明细 |

`definedFields` 包含 `define1`～`define16`。其中 `define4`、`define6` 为日期，`define5`、`define15` 为整数，`define7`、`define16` 为数值，其余为字符串。

## 明细字段

| JSON 字段 | 类型 | 必填 | 说明 |
|---|---:|:---:|---|
| lineNo | int | 否 | 行号，默认按数组顺序 |
| inventoryCode | string | 是 | 存货编码 |
| actualQuantity | decimal | 是 | 实盘数量，允许 0 |
| bookQuantity | decimal | 否 | 账面数量；不传时取 U8 当前结存 |
| actualNumber | decimal | 否 | 实盘件数 |
| bookNumber | decimal | 否 | 账面件数 |
| auxiliaryUnitCode | string | 否 | 辅计量单位编码；有辅单位时默认取 U8 存货档案 |
| batchCode | string | 条件 | 启用批次管理的存货必填 |
| positionCode | string | 条件 | 启用货位管理的仓库必填 |
| adjustInQuantity | decimal | 否 | 调整入库数量，默认 0 |
| adjustInNumber | decimal | 否 | 调整入库件数，默认 0 |
| adjustOutQuantity | decimal | 否 | 调整出库数量，默认 0 |
| adjustOutNumber | decimal | 否 | 调整出库件数，默认 0 |
| allowedWasteRate | decimal | 否 | 合理损耗率 |
| actualWasteRate | decimal | 否 | 实际损耗率 |
| exchangeRate | decimal | 否 | 换算率；有辅单位时默认取 U8 计量单位档案 |
| planUnitPrice | decimal | 否 | 计划单价；默认取最近入库成本 |
| bookAmount | decimal | 否 | 账面金额；默认计划单价 × 账面数量 |
| actualAmount | decimal | 否 | 实盘金额；默认计划单价 × 实盘数量 |
| differenceAmount | decimal | 否 | 差异金额；默认计划单价 × 数量差异 |
| reason | string | 否 | 盘点原因 |
| remark | string | 否 | 表体备注 |
| vendorCode | string | 否 | 供应商编码 |
| inVoucherCode | string | 否 | 对应入库单号 |
| inVoucherType | string | 否 | 对应入库单类型 |
| massDate | int | 否 | 保质期 |
| massUnit | string | 否 | 保质期单位 |
| manufactureDate | datetime | 否 | 生产日期 |
| invalidDate | datetime | 否 | U8 失效日期（`dDisDate`）；保质期管理存货默认次年 1 月 1 日 |
| expirationDate | datetime | 否 | 有效期至；保质期管理存货默认当年 12 月 31 日 |
| expirationDateText | string | 否 | 有效期文本；保质期管理存货按有效期至自动生成 |
| expirationDateCalculation | int | 否 | 有效期推算方式，默认 0 |
| recordDetailId | long | 否 | 收发记录子表 ID |
| itemClassCode | string | 否 | 项目大类编码 |
| itemCode | string | 否 | 项目编码 |
| salesOrderDetailId | long | 否 | 销售订单子表 ID |
| salesOrderType | int | 否 | 销售订单类型，默认 0 |
| freeFields | object | 否 | 自由项 `free1`～`free10` |
| definedFields | object | 否 | 表体自定义项 `define22`～`define37` |
| batchProperties | object | 否 | 批次属性 `property1`～`property10` |

表体 `definedFields` 中 `define26`、`define27` 为数值，`define34`、`define35` 为整数，`define36`、`define37` 为日期，其余为字符串。`batchProperties` 中 `property1`～`property5` 为数值，`property6`～`property9` 为字符串，`property10` 为日期。

## 返回与重试

新增成功返回 HTTP `201`，`data` 中包含 `voucherId`、`voucherCode`、`isVerified`、`replayed` 和 `u8Database`。同一个 `Idempotency-Key` 配合完全相同的请求体可安全重试；若 Key 相同但请求体不同，返回 HTTP `409`。

常见错误：

- `400`：字段、日期、仓库、存货、批号或货位校验失败。
- `401`：API Key 缺失或无效。
- `409`：幂等键复用、单号已存在或明细重复。
- `422`：U8 插入或自动审核失败。
- `503`：U8 账号被占用、接口未配置或操作队列繁忙。

建议流程：先调用 `validate`，确认 `currentBookQuantity`、`bookQuantity`、`actualQuantity` 和 `differenceQuantity`；确认无误后，用新的 `Idempotency-Key` 调用新增接口。默认保留未审核状态，待 U8 人工复核后再审核。

## 服务器交互脚本

在 API 服务器上双击：

```text
D:\MaterialOutboundApi\Create-U8InventoryCount.cmd
```

脚本依次选择账套、输入仓库编码和部门编码，并自动判断仓库是否启用货位管理。货位管理仓库必须明确选择“整个仓库”或“指定货位”；指定货位按名称输入，多个名称使用逗号分隔，名称不存在、重名或未输入时会要求重新选择。

源码目录也可以直接双击 `MaterialOutboundApi\Create-U8InventoryCount.cmd`；此时脚本自动使用仓库根目录 `.env.local` 中的数据库和 API 配置，不读取服务器 DPAPI 文件。

库存来源为 `CurrentStock`（非货位仓）或 `InvPositionSum`（货位仓）中数量大于 0 的记录，并按存货、批号、货位及 `cFree1`～`cFree10` 分组；自由项会原样传给接口，兼容“存货等级”等自由项管理。脚本显示库存摘要后，最后选择是否把全部实盘数量清零为 `0`；不清零时实盘数量按当前库存带入。脚本先调用校验接口，确认无误并输入 `CREATE` 后才创建，且始终设置 `autoVerify=false`。超过 1000 条库存时自动拆分为多张未审核盘点单。

只校验、不创建的示例：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File D:\MaterialOutboundApi\Deployment\Create-U8InventoryCount.ps1 `
  -Database 008 -WarehouseCode KML_11_SY -DepartmentCode 04125 `
  -Scope Warehouse -ActualQuantityMode Keep -DryRun
```
