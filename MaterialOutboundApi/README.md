# U8 库存单据 API（1.4.8）

## 通用盘点单 API（1.4.8）

- `POST /api/v1/general-inventory-count-orders/validate`：只读校验仓库、存货、批号、货位和账面结存。
- `POST /api/v1/general-inventory-count-orders`：创建 U8 盘点单；默认不审核。
- `GET /api/v1/general-inventory-count-orders/{voucherCode}`：查询盘点单和数量差异。
- `POST /api/v1/general-inventory-count-orders/{voucherCode}/actual-quantities`：按行号修改未审核盘点单的实盘数量。
- `POST /api/v1/general-inventory-count-orders/{voucherCode}/line-updates`：按行号修改数量、批号、货位名称、原因和行备注，必须提供查询返回的行版本。

盘点单使用 U8 库存单据类型 `18`、编号类型 `0307`，运行时从当前账套的历史盘点单读取原生 XML 结构，再通过 `VoucherCO` 自动编号和新增。完整字段、示例和安全说明见部署包中的 `GENERAL-INVENTORY-COUNT-API.md`。

服务器本机可双击站点根目录的 `Create-U8InventoryCount.cmd` 交互创建盘点单。脚本会校验账套、仓库和部门；货位管理仓库必须选择整仓或指定货位，指定货位按名称输入并支持多个。库存超过 1000 行时自动拆单，最后可选择将实盘数量全部置为 `0`，所有新单均保持未审核状态。存货存在辅计量单位时，脚本和 API 会自动带入辅单位、固定换算率及账面/实盘辅数量；启用保质期管理且未传有效期时，默认将“有效期至”写为盘点单日期所在年份的 `12月31日`，将 U8 “失效日期”写为次年 `1月1日`，并按 U8 历史盘点单规则补齐推算方式和生产日期。

在源码目录双击同名根目录启动文件时，脚本会自动读取仓库根目录 `.env.local` 中的 SQL 和盘点 API 配置；在 IIS 服务器运行时则读取 DPAPI 加密配置。

双击站点根目录的 `Edit-U8InventoryCount.cmd`，输入盘点单号和行号，再选择 `1=盘点数量、2=批号、3=货位名称、4=盘点原因、5=行备注`。一行可连续修改多项，`0` 进入下一行，`8` 撤销本行；回车不改，文本字段输入 `-` 清空（货位不可清空）。输入完行号后回车结束，核对清单并输入 `UPDATE` 才保存。默认使用008账套。

接口通过 U8 原生 `Load/Update` 保存，不直接更新数据库。拒绝已审核、审批控制、已有库存调整的行；行版本防止覆盖并发修改。货位按名称在本仓库唯一解析；批次管理存货批号必填。更改批号或货位保留原账面数量、自由项、保质期和批次属性，不直接搬库存；操作者必须核对账实关系。固定换算率辅数量及数量金额差异随实盘数量重算。保存成功后回读目标字段；网络超时或回读异常须先查询核对，不能盲目重试。

## 形态转换 API（1.2.9）

- `POST /api/v1/shape-conversions/previews`：按当前 `X-U8-Database` 复核仓库、货位、存货、批号、单位、现存量和固定参数；可选 `remark` 会随预览进入形态转换单表头，未提供时仍使用“拼皮线重新分色”。可选 `autoVerify` 控制是否自动审核；为兼容 PDA，未提供时默认为 `true`。
- `POST /api/v1/shape-conversions`：必须带 `Idempotency-Key`；单据日期固定为 IIS 当天。
- `GET /api/v1/shape-conversions/{voucherCode}`：返回形态转换单及派生 `09/08` 单的实际 ID、单号和审核状态。
- `POST /api/v1/shape-conversion-imports/previews`：Excel 专用整单预览，一次接收最多 5000 组转换明细并汇总校验转换前库存；同组转换前后货位可以不同。
- `POST /api/v1/shape-conversion-imports`：把预览中的所有组写入同一张形态转换单；与 PDA 单组接口和扫码转换记录区分。

管理页需分别维护 008、998 的部门、入库类别、出库类别和形态转换 `vt_id`。审核顺序固定为 `15 → 09 → 08`；部分成功不回滚，使用同一幂等键仅继续未完成步骤。升级继续使用原端口 9879 和原网站，并保留 `App_Data`。

仓库根目录的 `导入形态转换Excel.bat` 会固定提交表头备注“IT代替调整拼皮库存”，把整个 Excel 创建为一张单，并让操作员选择是否自动审核，默认不自动审核。同一文件再次运行会创建新单，不读取或写入扫码转换记录；普通 PDA 行为不变。

## 地址

```text
API：http://<server>:9879/api/v1/
Swagger：http://<server>:9879/swagger
本机配置：http://localhost:9879/admin/config
```

配置页面只允许在 API 服务器本机打开。业务接口需要：

```http
X-API-Key: 配置页生成的密钥
```

新增、审核和弃审还需要：

```http
Idempotency-Key: 调用方生成的8至64位唯一值
```

## 首次配置

1. 远程登录 API 服务器，在服务器浏览器打开
   `http://localhost:9879/admin/config`。
2. 填写 SQL Server 和 U8 登录密码；其他字段已带入当前确认的默认值。
3. 设置至少 12 位的配置管理密码。
4. 依次执行“测试 SQL”“测试 U8”“保存并应用”。
5. 立即复制保存页面只显示一次的完整 API Key；以后配置页只显示掩码。

保存时会同时验证 `momlist_yx`、U8 登录、库存模块初始化及注销。密码和
API Key 保存在 `App_Data\settings.json` 的 DPAPI 密文中。

## 接口

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/api/v1/work-orders/{workOrderCode}/lines` | 查询工单可选行 |
| POST | `/api/v1/material-outbound/previews` | 计算子件计划、已出库和剩余量 |
| POST | `/api/v1/material-outbound-orders` | 新增并自动审核材料出库单 |
| POST | `/api/v1/general-inventory-count-orders/validate` | 只读校验通用盘点单 |
| POST | `/api/v1/general-inventory-count-orders` | 新增通用盘点单，默认不审核 |
| GET | `/api/v1/general-inventory-count-orders/{voucherCode}` | 查询通用盘点单 |
| POST | `/api/v1/general-inventory-count-orders/{voucherCode}/actual-quantities` | 按行号修改未审核盘点单实盘数量 |
| POST | `/api/v1/traceback-labels/lookups` | 查询单张追溯标签 |
| POST | `/api/v1/traceback-labels/batch-lookups` | 一次批量查询最多 500 张追溯标签 |
| GET | `/api/v1/material-outbound-orders/{voucherCode}` | 查询材料出库单 |
| POST | `/api/v1/material-outbound-orders/{voucherCode}/verify` | 审核 |
| POST | `/api/v1/material-outbound-orders/{voucherCode}/unverify` | 弃审 |
| GET | `/api/v1/health` | 健康和配置状态 |

## 调用顺序

1. 查询工单可选行。
2. 提交工单行和单据日期生成预览。
3. 根据预览中的 `allocateId` 填写出库数量和逐行批号；同一 `allocateId`
   可以提交多个批号，服务端会按 `allocateId` 汇总全部批次数量后校验剩余量。
4. 提交新增接口；新增成功后接口立即自动审核。
5. 按单号查询 U8 保存及审核结果。

如果新增成功但自动审核失败，调用方应使用原 `Idempotency-Key` 重试；接口只会
补做审核，不会重复生成材料出库单。

## U8 登录并发

所有 U8 COM 操作在单个 STA 工作线程排队执行，并使用系统级互斥锁。每次操作
完成后调用 `U8Login.ShutDown()` 注销 `yx`。如果 `yx` 正在其他 U8 客户端
使用，接口返回 HTTP 503，不会强制踢出人工用户。

## IIS

项目必须运行在独立的 32 位 IIS 应用池中：

```text
网站：MaterialOutboundApi
端口：9879
物理路径：D:\MaterialOutboundApi
应用池：MaterialOutboundApiPool
CLR：v4.0
Enable32BitAppOnWin64：true
maxProcesses：1
disallowOverlappingRotation：true
idleTimeout：0
LoadUserProfile：true
```

`App_Data` 保存 DPAPI 加密配置、LiteDB 幂等记录和日志。IIS 应用池身份必须
拥有该目录的修改权限。

部署包中的 `Deployment\Build-Deployment.ps1` 用于重新生成标准 IIS
目录；服务器上的 `Deployment\Install-IIS.ps1` 可幂等地修复网站、应用池、
目录权限和防火墙配置。升级时必须保留原有 `App_Data`。

## 安全

- SQL、U8 密码只以 DPAPI Machine Scope 密文保存。
- API Key 不写入 Swagger 示例或日志。
- 配置页面仅限回环地址，并需要管理密码。
- 当前为公司局域网 HTTP 测试部署；正式推广前应配置有效 HTTPS 证书。

## 常用返回码

- `NOT_CONFIGURED`：尚未完成服务器本机配置。
- `INVALID_API_KEY`：业务调用的 API Key 无效。
- `U8_ACCOUNT_IN_USE`：操作员正在其他 U8 客户端使用。
- `PREVIEW_EXPIRED`：15 分钟预览已失效。
- `QUANTITY_EXCEEDS_REMAINING`：提交数量超过实时剩余量。
- `BATCH_REQUIRED`：批次管理存货未提供批号。
- `MULTIPLE_DEPARTMENTS`：所选工单行跨部门，必须拆单。
- `IDEMPOTENCY_KEY_REUSED`：幂等键已用于不同请求。
