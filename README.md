# U8 材料出库 IIS 集成

本仓库保存生产领料 V2 使用的 U8 材料出库接口及其原始控制台依赖。它必须
作为独立的 IIS / .NET Framework 应用运行：
U8 API 使用 U8 COM 组件，不能加载到 Python 进程。

部署、接管和安全配置请先阅读：

- [`IIS_DEPLOYMENT_AND_OPERATIONS_AGENT_GUIDE.md`](./IIS_DEPLOYMENT_AND_OPERATIONS_AGENT_GUIDE.md)
- [`LOCAL_CONFIGURATION_AND_SECRETS.md`](./LOCAL_CONFIGURATION_AND_SECRETS.md)
- [`AGENTS.md`](./AGENTS.md)

## 目录

- `MaterialOutboundApi`：IIS Web API，生产领料实际调用的接口。
- `GeneralMaterialOutbound`：独立编译、由同一 IIS Web API 加载的通用材料出库模块。
- `GeneralInventoryCount`：独立编译、由同一 IIS Web API 加载的通用盘点单模块。
- `MaterialOutboundConsole`：XML 模板、U8 COM 数据结构和原始控制台实现。
- `EXTRACTION.md`：以后从本仓库完整剥离为独立项目的方法。

## 生产领料 V2 使用的接口

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/api/v1/work-order-lines?keyword=&sortSeq=` | 工单模糊查询或二维码精准查询 |
| GET | `/api/v1/work-order-lines?keyword=&sortSeq=&includeInactive=true` | 邮件历史生产数量查询，不限制状态、释放时间或材料需求；必须精准指定工单及行号 |
| POST | `/api/v1/material-outbound/previews` | 实时读取选中产品行的材料需求 |
| POST | `/api/v1/traceback-labels/lookups` | 查询标签及 `SUM(TraceBackOnlyCode.sum)` 余额 |
| POST | `/api/v1/traceback-labels/batch-lookups` | 整单上传时一次批量复核最多 500 张标签 |
| POST | `/api/v1/material-outbound-orders` | 创建并自动审核 U8 材料出库单 |
| POST | `/api/v1/general-material-outbound-orders/validate` | 只读校验通用材料出库请求 |
| POST | `/api/v1/general-material-outbound-orders` | 按调用方参数创建通用材料出库单 |
| POST | `/api/v1/general-inventory-count-orders/validate` | 只读校验通用盘点单请求 |
| POST | `/api/v1/general-inventory-count-orders` | 创建通用盘点单，默认不审核 |
| GET | `/api/v1/general-inventory-count-orders/{voucherCode}` | 查询通用盘点单 |

通用接口字段、直接领用和工单领用示例见
[`GeneralMaterialOutbound/README.md`](./GeneralMaterialOutbound/README.md)。
通用盘点单字段和示例见
[`GeneralInventoryCount/README.md`](./GeneralInventoryCount/README.md)。

所有接口均使用请求头 `X-API-Key`。创建单据还必须传
`Idempotency-Key`；同一业务请求重试时不得更换。

## 固定业务字段

- 测试账套数据库：`UFDATA_998_2021`
- 仓库：`KML_PP`（拼皮仓）
- 货位：`PP2`
- 出入库类别：`202`
- 制单人：IIS 配置中的 `U8UserId`
- 单据日期：FastAPI 确认上传当天
- 表头产品：一个产品时填写；多个产品时留空
- 明细：按“标签 × 产品材料分配行”展开
- `RdRecords11.cbmemo`：`SN + 裁切长度×宽度`
- `RdRecords11.iUnitCost`：按材料编码和批号从 `IA_Subsidiary` 取最新未税
  入库单价 `iInCost`；批号字段使用 `cBatchIA`，无匹配记录时写 `0`。
- `RdRecords11.iPrice`：本次数量乘以上述未税入库单价。

IIS API会按 `allocateId` 汇总全部标签、批号的领用量后校验，不能通过拆行
绕过剩余量限制。单据新增成功后立即自动审核；若新增成功但审核失败，同一个
`Idempotency-Key` 重试时只补做审核，不重复生成单据。

整单上传不再逐张建立 HTTP 连接。FastAPI 将本地草稿中的标签一次提交给批量
查询接口，IIS 使用一条汇总 SQL 返回全部标签及余额；U8 单据新增阶段的准备、
登录、取号、新增、查询实际单号和审核耗时记录在 `App_Data/Logs/performance-*.log`。

## 编译

在装有 .NET Framework 4.7.2 Developer Pack 的 Windows 机器执行：

```powershell
dotnet build .\MaterialOutboundApi\MaterialOutboundApi.csproj -c Release
```

如果构建机缺少 U8 COM 组件，项目使用仓库内控制台项目所带的必要引用完成编译；
实际运行机仍必须安装匹配版本的 U8 客户端及 COM 组件。

## IIS 部署

1. 将 `MaterialOutboundApi` 发布到 U8 应用服务器。
2. IIS 应用程序池使用 .NET CLR v4.0、集成模式，并使用可启动 U8 COM 的 Windows
   身份运行。
3. 给应用程序池身份授予站点目录和 `App_Data` 的读写权限。
4. 打开 `/admin/`，通过管理员密码写入加密配置。
5. 配置完成后调用健康检查，再用测试工单执行预览；不要直接用生产工单试建单。

IIS 敏感配置由 `SettingsStore` 使用 Windows DPAPI 加密后保存在 `App_Data`，
该目录必须排除 Git。本机运行盘点辅助脚本时，把仓库根目录 `.env.example`
复制为 `.env.local` 并填写连接参数；该文件不得提交。调用方的 API Key、SQL
账号和密码也不得写入客户端、APK 或普通业务日志。

必填配置至少包括：

- SQL Server、数据库 `UFDATA_998_2021`、SQL 登录信息
- U8 登录用户 `U8UserId`、U8 密码、账套标识 `(default)@998`
- `WarehouseCode=KML_PP`
- `PositionCode=PP2`
- `RdCode=202`
- U8 业务对象 ProgId、单据模板 `vt_id`
- API Key 和后台管理员密码

FastAPI 只配置 IIS 基址、API Key 和超时：

```dotenv
MATERIAL_OUTBOUND_API_BASE_URL="http://<server>:9879/api/v1"
MATERIAL_OUTBOUND_API_KEY="<服务器密钥>"
MATERIAL_OUTBOUND_API_TIMEOUT_SECONDS="120"
```

## 部署后核对

- 模糊查询能返回多个工单产品行，`sortSeq` 能精准定位。
- 标签余额等于该 `traceBackCode` 全部正负流水的合计。
- 同部门多产品生成一张单；不同部门由 FastAPI 分别调用并生成多张单。
- `RdRecord11/RdRecords11` 中部门、仓库、货位、产品、材料、批号、数量、备注正确。
- 新增成功后使用 U8 返回的 `voucherId` 查询
  `RdRecord11.ID = voucherId` 对应的真实 `cCode`；API 同时返回
  `voucherId` 和真实 `voucherCode`，查询不到时不得返回成功。
- 新单 `cVerifier`、`dVeriDate` 已填写，处于已审核状态。
