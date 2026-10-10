# 通用材料出库单 API 接口说明

## 1. 接口概览

通用接口作为独立 `GeneralMaterialOutbound.dll` 发布，由现有
`MaterialOutboundApi` IIS 应用加载。它与现有生产领料接口共用端口、API Key、
账套选择、U8 登录、单 STA 队列和幂等数据库，但使用独立路由。

基础地址：

```text
http://<server>:9879/api/v1/general-material-outbound-orders
```

| 方法 | 路径 | 说明 | 是否写 U8 |
| --- | --- | --- | --- |
| POST | `/validate` | 校验并解析请求 | 否 |
| POST | `/` | 创建材料出库单，可选自动审核 | 是 |
| GET | `/{voucherCode}` | 查询材料出库单和审核状态 | 否 |

## 2. 请求头

| 名称 | 必填 | 说明 |
| --- | --- | --- |
| `Content-Type` | 是 | `application/json` |
| `X-API-Key` | 是 | 与现有 IIS API 共用 |
| `X-U8-Database` | 建议 | 当前允许的 U8 账套，如 `UFDATA_008_2021` |
| `Idempotency-Key` | 创建时必填 | 8～64 个字符；重试必须保持不变 |

建议让 `Idempotency-Key` 与 `externalRequestNo` 使用相同值。

## 3. 单头字段

| JSON 字段 | 类型 | 必填 | 长度/范围 | 对应 U8 字段或规则 |
| --- | --- | --- | --- | --- |
| `externalRequestNo` | string | 是 | 1～64 | 外部请求号，用于调用方追踪 |
| `issueMode` | string | 是 | `direct`/`productionOrder` | 直接领用或工单领用 |
| `voucherDate` | string | 是 | `yyyy-MM-dd` | `dDate`，年份必须等于 U8 登录年度 |
| `voucherCode` | string | 否 | ≤30 | `cCode`；不传时由 U8 自动取号 |
| `warehouseCode` | string | 是 | ≤10 | `cWhCode` |
| `departmentCode` | string | 是 | ≤12 | `cDepCode` |
| `rdCode` | string | 是 | ≤5 | `cRdCode` |
| `personCode` | string | 否 | ≤20 | `cPersonCode` |
| `maker` | string | 否 | ≤20 | `cMaker`；不传时使用服务器配置 |
| `productionBatch` | string | 否 | ≤60 | `cProBatch` |
| `source` | string | 否 | ≤50 | `cSource`；工单模式默认“生产订单” |
| `businessType` | string | 否 | ≤12 | `cBusType`，默认“领料” |
| `businessCode` | string | 否 | ≤30 | `cBusCode` |
| `memo` | string | 否 | ≤255 | `cMemo` |
| `productCode` | string | 否 | ≤60 | `cPspCode`；工单模式由 U8 记录复核 |
| `productionOrderCode` | string | 工单模式必填 | ≤30 | `cMPoCode` |
| `productionOrderId` | int | 否 | >0 | `iProOrderId`；工单模式传入时会复核 |
| `voucherTemplateId` | int | 否 | >0 | `vt_id`；不传使用 IIS 配置 |
| `autoVerify` | bool | 否 | 默认 `true` | 建单后是否自动审核 |
| `definedFields` | object | 否 | 见下表 | U8 单头自定义项 |
| `lines` | array | 是 | 1～500 条 | 材料出库明细 |

`ID`、`AutoID`、系统条码、审核人、审核日期、记账人、修改人、时间戳、工作流状态、
打印次数和编辑标记等字段由 U8 或接口维护，不接受调用方传入。审核行为只由
`autoVerify` 控制。

现存量沿用生产领料专用接口的规则，按仓库、材料和批号汇总；货位负责校验其
是否存在并属于所选仓库，不作为 `CurrentStock` 汇总列。

### 单头自定义项 `definedFields`

| 字段 | 类型 | U8 字段 | 限制 |
| --- | --- | --- | --- |
| `define1` | string | `cDefine1` | ≤20 |
| `define2` | string | `cDefine2` | ≤20 |
| `define3` | string | `cDefine3` | ≤20 |
| `define4` | datetime | `cDefine4` | ISO 日期时间 |
| `define5` | int | `cDefine5` | 32 位整数 |
| `define6` | datetime | `cDefine6` | ISO 日期时间 |
| `define7` | decimal | `cDefine7` | 数值 |
| `define8` | string | `cDefine8` | ≤4 |
| `define9` | string | `cDefine9` | ≤8 |
| `define10` | string | `cDefine10` | ≤60 |
| `define11`～`define14` | string | `cDefine11`～`cDefine14` | 每项≤120 |
| `define15` | int | `cDefine15` | 32 位整数 |
| `define16` | decimal | `cDefine16` | 数值 |

## 4. 明细字段

| JSON 字段 | 类型 | 必填 | 对应 U8 字段或规则 |
| --- | --- | --- | --- |
| `lineNo` | int | 否 | `iRowNo`；默认按数组顺序生成，不可重复 |
| `materialCode` | string | 直接模式必填 | `cInvCode`；工单模式从分配记录取得并复核 |
| `quantity` | decimal | 是 | `iQuantity`，必须大于 0 |
| `number` | decimal | 否 | `iNum` |
| `inQuantity` | decimal | 否 | `inQuantity`，默认等于 `quantity` |
| `inNumber` | decimal | 否 | `inNum` |
| `unitCost` | decimal | 否 | `iUnitCost`；不传时取最近入库成本 |
| `price` | decimal | 否 | `iPrice`；不传时为数量×单价 |
| `exchangeRate` | decimal | 否 | `iInvExchRate` |
| `batchCode` | string | 批次存货必填 | `cBatch` |
| `positionCode` | string | 货位仓必填 | `cPosition` |
| `productCode` | string | 否 | `invCode`；工单模式从工单取得 |
| `productionOrderDetailId` | int | 工单模式必填 | `moDId`，用于复核工单行 |
| `allocationId` | long | 工单模式必填 | `iMPoIds`/`AllocateId` |
| `productionOrderCode` | string | 否 | `cMoCode`；工单模式从工单取得 |
| `productionOrderSequence` | int | 否 | `iMoSeq`；工单模式从工单取得 |
| `operationSequence` | string | 否 | `iOpSeq`；工单模式从分配记录取得 |
| `operationDescription` | string | 否 | `cOpDesc` |
| `workCenterCode` | string | 否 | `cMWorkCenterCode` |
| `salesOrderType` | int | 否 | `iSoType` |
| `orderType` | int | 否 | `iOrderType` |
| `orderDetailId` | int | 否 | `iOrderDId` |
| `orderCode` | string | 否 | `iOrderCode` |
| `orderSequence` | int | 否 | `iOrderSeq` |
| `peSoDetailId` | long | 否 | `iPeSoDId` |
| `peSoType` | int | 否 | `iPeSoType` |
| `peSoCode` | string | 否 | `cPeSoCode` |
| `peSoSequence` | int | 否 | `iPeSoSeq` |
| `remark` | string | 否 | `cBMemo`，≤255 |
| `freeFields` | object | 否 | `cFree1`～`cFree10` |
| `definedFields` | object | 否 | `cDefine22`～`cDefine37` |
| `batchProperties` | object | 否 | `cBatchProperty1`～`cBatchProperty10` |

### 明细自由项 `freeFields`

`free1`～`free10` 均为字符串，对应 U8 `cFree1`～`cFree10`，每项最长 60。

### 明细自定义项 `definedFields`

| 字段 | 类型 | U8 字段 |
| --- | --- | --- |
| `define22`～`define25` | string | `cDefine22`～`cDefine25` |
| `define26`～`define27` | decimal | `cDefine26`～`cDefine27` |
| `define28`～`define33` | string | `cDefine28`～`cDefine33` |
| `define34`～`define35` | int | `cDefine34`～`cDefine35` |
| `define36`～`define37` | datetime | `cDefine36`～`cDefine37` |

### 批次属性 `batchProperties`

`property1`～`property5` 为 decimal，`property6`～`property9` 为最长 120 的
string，`property10` 为 datetime；分别对应 U8
`cBatchProperty1`～`cBatchProperty10`。

## 5. 直接领用示例

```http
POST /api/v1/general-material-outbound-orders/validate HTTP/1.1
Content-Type: application/json
X-API-Key: <api-key>
X-U8-Database: UFDATA_998_2021
```

```json
{
  "externalRequestNo": "MES-20260919-0001",
  "issueMode": "direct",
  "voucherDate": "2026-09-19",
  "warehouseCode": "KML_PP",
  "departmentCode": "04125",
  "rdCode": "202",
  "source": "库存",
  "businessType": "领料",
  "autoVerify": false,
  "memo": "外部系统直接领用",
  "lines": [
    {
      "lineNo": 1,
      "materialCode": "44020001",
      "quantity": 10,
      "batchCode": "20260901",
      "positionCode": "PP2",
      "remark": "测试领用"
    }
  ]
}
```

建议先调用 `/validate`。校验通过后，把相同 JSON 提交到创建接口，并增加：

```http
Idempotency-Key: MES-20260919-0001
```

## 6. 生产工单领用示例

工单模式参考现有“生产工单领料专用”接口。调用方提供工单号、`moDId`、
`allocateId` 和本次领用数量；服务端重新读取 U8 工单和子件分配记录，不直接信任
调用方传入的产品、材料、工序和生产订单内部 ID。

```json
{
  "externalRequestNo": "MES-MO-20260919-0008",
  "issueMode": "productionOrder",
  "voucherDate": "2026-09-19",
  "warehouseCode": "KML_PP",
  "departmentCode": "04125",
  "rdCode": "202",
  "productionOrderCode": "0000123456",
  "autoVerify": true,
  "memo": "生产订单材料领用",
  "lines": [
    {
      "lineNo": 1,
      "productionOrderDetailId": 67890,
      "allocationId": 45678,
      "quantity": 10,
      "batchCode": "20260901",
      "positionCode": "PP2",
      "remark": "工单领用"
    }
  ]
}
```

## 7. 创建成功响应

```json
{
  "success": true,
  "code": "OK",
  "message": "材料出库单新增成功",
  "requestId": "8f90e6fef2a64a718946f064f55890c1",
  "data": {
    "externalRequestNo": "MES-MO-20260919-0008",
    "voucherId": "1000123456",
    "voucherCode": "0000001234",
    "createdAt": "2026-09-19T11:30:00+08:00",
    "isVerified": true,
    "replayed": false,
    "u8Database": "UFDATA_998_2021"
  }
}
```

## 8. 主要错误码

| 错误码 | 含义 |
| --- | --- |
| `VALIDATION_ERROR` | JSON 类型、长度或范围不正确 |
| `ISSUE_MODE_INVALID` | 领用模式不正确 |
| `INVALID_DATE` | 单据日期格式不正确 |
| `VOUCHER_DATE_YEAR_MISMATCH` | 单据日期与 U8 年度不一致 |
| `WAREHOUSE_NOT_FOUND` | 仓库不存在 |
| `DEPARTMENT_NOT_FOUND` | 部门不存在 |
| `RD_CODE_NOT_FOUND` | 出库类别不存在 |
| `MATERIAL_NOT_FOUND` | 存货不存在 |
| `BATCH_REQUIRED` | 批次存货未传批号 |
| `POSITION_REQUIRED` | 货位仓未传货位 |
| `POSITION_NOT_FOUND` | 货位不存在或不属于仓库 |
| `ALLOCATION_NOT_FOUND` | 工单、工单行和子件分配记录不匹配 |
| `DEPARTMENT_MISMATCH` | 传入部门与工单部门不一致 |
| `U8_STOCK_INSUFFICIENT` | U8 现存量不足 |
| `IDEMPOTENCY_KEY_REUSED` | 同一幂等键提交了不同内容 |
| `U8_INSERT_FAILED` | U8 建单失败 |
| `U8_AUTO_VERIFY_FAILED` | 单据已创建，但自动审核失败 |

自动审核失败时不得更换 `Idempotency-Key` 重建单据。修正审核条件后，使用原请求和
原幂等键重试，接口会定位已创建单据并继续审核。
