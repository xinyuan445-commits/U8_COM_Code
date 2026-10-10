# U8 IIS 服务部署与运维指南（Agent Handoff）

本文整理本仓库 `MaterialOutboundApi` 的 IIS 架构、构建、XML 获取、安装、配置、升级、回滚、安全、验证和排障要求，供维护人员或其他 Agent 在新环境中部署与接管。

本文只记录必要的配置结构与操作边界，不包含真实 SQL 密码、U8 密码、API Key、管理员密码或签名材料。

## 1. 服务定位

`MaterialOutboundApi` 是独立运行的 ASP.NET Web API / IIS 应用，负责调用 U8 COM 组件完成：

- 生产领料材料出库；
- 通用材料出库；
- U8 盘点单创建、查询和未审核单修改；
- 木皮形态转换及派生出入库单；
- 工单、追溯标签和单据查询；
- 服务器本机配置管理。

它与 FastAPI 位于同一仓库，但必须作为独立 IIS 进程运行。不要把 U8 COM 组件加载进 Python/FastAPI 进程。

主要源码：

```text
U8_COM_Code/
├─ MaterialOutboundApi/          IIS 宿主
├─ GeneralMaterialOutbound/      通用材料出库模块
├─ GeneralInventoryCount/        通用盘点模块
└─ MaterialOutboundConsole/      U8 COM 模型、模板和原始实现
```

## 2. 当前标准部署参数

源码和安装脚本使用以下默认值：

```text
网站名称：MaterialOutboundApi
应用程序池：MaterialOutboundApiPool
端口：9879
物理目录：D:\MaterialOutboundApi
CLR：v4.0
管道模式：Integrated
32 位应用：启用
最大工作进程：1
重叠回收：禁用
空闲超时：0
定时回收：0
启动模式：AlwaysRunning
LoadUserProfile：true
```

服务地址格式为：

```text
http://<server>:9879
```

迁移或重新部署前必须在目标环境核对实际服务器地址、端口占用、防火墙、DNS 和 U8 账套。

常用入口：

```text
健康检查：http://<server>:9879/api/v1/health
Swagger：http://<server>:9879/swagger
本机配置：http://localhost:9879/admin/config
```

配置页只能从 IIS 服务器本机回环地址访问。

## 3. 运行依赖

### 3.1 IIS 服务器

目标服务器至少需要：

- Windows Server 或支持 IIS 的 Windows；
- IIS Web Server；
- ASP.NET 4.x、.NET Extensibility 4.x、ISAPI Extensions、ISAPI Filters；
- .NET Framework 4.8 运行时；
- 匹配版本和位数的 U8 客户端、U8 COM 组件；
- 32 位 U8 互操作依赖；
- 可访问 SQL Server 和目标 U8 账套的网络；
- 管理员 PowerShell，用于创建网站、应用池、防火墙和目录 ACL。

项目文件的实际目标框架是 `net48`、`x86`、`Prefer32Bit=true`。旧说明中提到的 .NET Framework 4.7.2 Developer Pack 不应覆盖项目文件事实；构建环境应安装 .NET Framework 4.8 Developer Pack/Targeting Pack。

### 3.2 构建机

构建机需要：

- 可用的 `dotnet` SDK；
- .NET Framework 4.8 Targeting Pack；
- PowerShell；
- 仓库内的 U8 Interop 引用文件；
- NuGet 包恢复能力。

构建机可以没有完整 U8 客户端，但目标 IIS 服务器必须安装可运行的 U8 COM 环境。

## 4. 应用程序池要求

必须使用独立 32 位应用程序池。推荐保持安装脚本的设置：

```text
managedRuntimeVersion = v4.0
managedPipelineMode = Integrated
enable32BitAppOnWin64 = true
processModel.maxProcesses = 1
recycling.disallowOverlappingRotation = true
processModel.idleTimeout = 00:00:00
recycling.periodicRestart.time = 00:00:00
processModel.loadUserProfile = true
startMode = AlwaysRunning
```

原因：

- U8 COM 依赖 32 位宿主；
- COM 操作必须在 STA 线程串行执行；
- 多工作进程或重叠回收可能产生两个 U8 操作进程；
- DPAPI、U8 登录及部分 COM 组件依赖稳定的 Windows 运行环境和用户配置。

安装脚本默认使用 `ApplicationPoolIdentity`。目标服务器必须确认该身份能够：

- 读取站点程序文件；
- 修改 `App_Data` 和 `App_Data\Logs`；
- 启动并调用 U8 COM；
- 访问需要的注册表、组件和网络资源。

如果现场 U8 COM 只能由指定 Windows 账号启动，可把应用池切换为专用受控账号，但必须重新验证 DPAPI、目录权限、U8 登录和服务重启。

## 5. 目录与持久数据

标准站点目录：

```text
D:\MaterialOutboundApi\
├─ bin\
├─ Admin\
├─ App_Data\
│  ├─ settings.json
│  ├─ state.db
│  └─ Logs\
├─ XML\
├─ ShapeConversionXml\
├─ Deployment\
├─ Global.asax
└─ Web.config
```

`App_Data` 是持久数据目录：

- `settings.json`：SQL、U8 和 API 配置；敏感字段为 DPAPI 密文；
- `state.db`：LiteDB 预览和幂等操作状态；
- `Logs`：性能与运行日志。

升级时不得覆盖或删除现有 `App_Data`。回滚程序文件时也要保留最新有效的幂等状态和日志，防止已经执行过的 U8 请求被当成新请求重复执行。

DPAPI 使用 `LocalMachine` 范围。`settings.json` 可以在同一台服务器和正确环境下解密，但复制到另一台服务器通常无法解密。迁移机器时应在新服务器本机重新配置密码和 API Key，不要把旧密文当作可移植配置。

## 6. 构建

### 6.1 编译检查

在仓库根目录或使用完整路径执行：

```powershell
dotnet build .\MaterialOutboundApi\MaterialOutboundApi.csproj `
  -c Release `
  -p:Platform=x86
```

编译必须为 0 error。警告应在发布前检查，尤其是平台位数、Interop 引用和缺失内容文件。

### 6.2 生成标准部署包

使用仓库脚本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\MaterialOutboundApi\Deployment\Build-Deployment.ps1 `
  -Configuration Release
```

默认输出：

```text
artifacts/MaterialOutboundApi
```

`OutputDirectory` 必须位于该工作区的 `artifacts` 目录内，脚本会拒绝其他路径。

部署包应包含：

- `bin` 中的宿主、通用出库、通用盘点及依赖程序集；
- `Web.config`、`Global.asax`；
- `Admin` 管理页；
- `XML/head.xml`、`XML/body.xml`；
- `ShapeConversionXml/head.xml`、`ShapeConversionXml/body.xml`；
- `Deployment` 脚本；
- 服务器交互式 `.cmd` 入口；
- API 说明文档。

缺少 XML 模板会导致 U8 VoucherCO 或形态转换运行失败。不要只复制主 DLL。

打包脚本会把部署包中的 `.ps1` 和 `.psm1` 转换为带 BOM 的 UTF-8，保证 Windows PowerShell 5.1 正确显示和解析中文；不会修改仓库源文件编码。

### 6.3 获取 U8 XML 文件

本项目所说的 XML 有三种，不能混用：

- **源码模板**：带 `@z_row` 占位符，由程序填充业务行；应纳入版本控制；
- **U8 原始 XML**：从一张已存在的 U8 单据只读导出，包含真实业务数据；用于核对字段和制作模板，不应提交到仓库；
- **预览 XML**：程序根据源码模板和当前输入生成，尚未提交 U8；用于检查本次制单内容，不是新的通用模板。

导出操作应在安装了匹配版本 U8 客户端和 COM 组件的目标服务器执行。应选择与目标业务相同账套、同 U8 版本、字段完整且已确认正确的历史单据作为样本。原始 XML 可能包含料号、批号、部门、仓库、操作员等业务数据，导出目录应受控，使用完及时归档或删除，不得直接提交 Git。

#### 6.3.1 材料出库 XML

当前程序实际使用的模板位于：

```text
MaterialOutboundConsole\XML\head.xml
MaterialOutboundConsole\XML\body.xml
```

要从一张现有材料出库单取得 U8 返回的原始 XML，在控制台发布目录执行：

```bat
cd /d D:\MaterialOutboundConsole
MaterialOutboundConsole.exe query <材料出库单号>
```

按提示输入所需密码。该命令只读调用材料出库单 `VoucherCO.Load`，输出到：

```text
D:\MaterialOutboundConsole\Output\<单号>-query-<时间>\head.xml
D:\MaterialOutboundConsole\Output\<单号>-query-<时间>\body.xml
```

如果只需要查看程序准备提交的 XML，可执行：

```bat
cd /d D:\MaterialOutboundConsole
MaterialOutboundConsole.exe select-add
```

完成工单和明细选择后，在“确认提交到 U8”处直接回车，不输入 `YES`。程序不会新增单据，只会把预览写入：

```text
D:\MaterialOutboundConsole\Output\<单号或AUTO>-<时间>\head.xml
D:\MaterialOutboundConsole\Output\<单号或AUTO>-<时间>\body.xml
```

`query` 得到的是含真实行数据的 U8 原始 XML，`select-add` 得到的是按现有模板生成的预览 XML。两者都不能直接覆盖源码模板。若要更新模板，应比较字段名、顺序、类型和命名空间，只把确认需要的 Schema 变化合入源码模板，并保留 `@z_row` 占位符。

#### 6.3.2 形态转换 XML

当前 IIS 使用的模板位于：

```text
MaterialOutboundApi\ShapeConversionXml\head.xml
MaterialOutboundApi\ShapeConversionXml\body.xml
```

选择一张字段完整、已确认正确的历史形态转换单，在控制台发布目录执行：

```bat
cd /d D:\MaterialOutboundConsole
MaterialOutboundConsole.exe export-shape-template <形态转换单号>
```

例如：

```bat
MaterialOutboundConsole.exe export-shape-template XTZH00001508
```

该命令只读调用 `VoucherCO.Load("15", "ID=...")`，不会新增、修改或审核单据。输出结构为：

```text
D:\MaterialOutboundConsole\Output\shape-template-<单号>-<时间>\
├─ raw\
│  ├─ head.xml
│  ├─ body.xml
│  └─ position.xml
└─ template\
   ├─ head.xml
   └─ body.xml
```

`raw` 是 U8 返回的原始 XML；`template` 已把真实数据行替换为 `@z_row`。确认 U8 版本、账套和字段结构一致后，才可用 `template` 中的文件更新 `ShapeConversionXml` 源码模板。

#### 6.3.3 盘点单 XML

盘点单有两种只读导出脚本，均在 IIS 包的 `Deployment` 目录中：

- `Export-U8InventoryCountXml.ps1`：通过 `VoucherCO` 类型 `18` 导出原生 XML，优先用于核对 CO Schema；
- `Export-U8InventoryCountRecordsets.ps1`：通过盘点持久化对象导出 ADO Recordset XML，主要用于诊断和交叉核对，不应视为可直接替换的 CO 模板。

脚本依赖目标服务器的 U8 COM、站点 `bin` 中的 x86 互操作程序集，以及本机 `LocalMachine` DPAPI 加密的 `App_Data\settings.json`。因此应在目标 IIS/U8 服务器上使用 **32 位 Windows PowerShell 5.1** 执行：

```powershell
& "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" `
  -NoProfile -ExecutionPolicy Bypass `
  -File "D:\MaterialOutboundApi\Deployment\Export-U8InventoryCountXml.ps1" `
  -SettingsPath "D:\MaterialOutboundApi\App_Data\settings.json" `
  -Database "UFDATA_008_2021" `
  -VoucherCode "<盘点单号>" `
  -OutputDirectory "D:\MupiInventoryCountResearch\xml-<盘点单号>"
```

原生导出结果位于：

```text
<OutputDirectory>\18\head.xml
<OutputDirectory>\18\body.xml
<OutputDirectory>\18\position.xml
<OutputDirectory>\result.json
```

需要导出 Recordset XML 进行比对时执行：

```powershell
& "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" `
  -NoProfile -ExecutionPolicy Bypass `
  -File "D:\MaterialOutboundApi\Deployment\Export-U8InventoryCountRecordsets.ps1" `
  -SettingsPath "D:\MaterialOutboundApi\App_Data\settings.json" `
  -Database "UFDATA_008_2021" `
  -VoucherCode "<盘点单号>" `
  -OutputDirectory "D:\MupiInventoryCountResearch\recordsets-<盘点单号>"
```

Recordset 导出结果直接位于 `<OutputDirectory>` 下，包括 `head.xml`、`body.xml`、`position.xml` 和 `result.json`。两个脚本当前只允许 `UFDATA_008_2021` 或 `UFDATA_998_2021`；不要通过改脚本绕过账套白名单。

#### 6.3.4 更新模板后的处理

更新任何源码 XML 模板后：

1. 检查命名空间、字段名、字段顺序、数据类型和 `@z_row` 是否完整；
2. 确认没有把真实单号、批号、料号、密码或其他生产数据带入模板；
3. 重新执行 `Build-Deployment.ps1`，不要只修改服务器上的部署副本；
4. 核对部署包中的 `XML` 和 `ShapeConversionXml` 与源码一致；
5. 先做只读预览和查询验证，再由人工明确确认是否进行真实制单测试。

不要根据数据库列名自行拼造 U8 CO XML，也不要把 API JSON 响应当作 XML 模板。U8 COM 返回的同版本原始 XML 才是字段结构核对依据。

## 7. 首次安装 IIS

### 7.1 放置部署包

把完整部署包复制到：

```text
D:\MaterialOutboundApi
```

### 7.2 执行安装脚本

用管理员 PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File D:\MaterialOutboundApi\Deployment\Install-IIS.ps1
```

脚本会幂等地：

- 创建或修复应用程序池；
- 创建或修复网站和 9879 端口绑定；
- 创建 `App_Data`、`App_Data\Logs`；
- 给应用池身份授予这两个目录的 Modify 权限；
- 创建只允许 Domain/Private、LocalSubnet 访问的入站防火墙规则；
- 启动网站和应用池；
- 最多重试 10 次本机健康检查。

如果脚本提示 IIS、ASP.NET 4.x 或 ISAPI 未安装，先补齐 Windows 功能，不要绕过检查。

## 8. 首次本机配置

在 IIS 服务器本机浏览器打开：

```text
http://localhost:9879/admin/config
```

首次配置至少填写：

- SQL Server 地址、数据库、SQL 登录和密码；
- U8 服务器、账套、年度、子系统、用户和密码；
- 仓库、货位、出入库类别；
- 制单人、部门、工作中心等业务参数；
- U8 ProgId 和单据模板 `vt_id`；
- 008/998 形态转换固定参数；
- 至少 12 位的配置管理密码。

建议顺序：

1. 读取账套列表；
2. 执行“测试 SQL”；
3. 执行“测试 U8”；
4. 保存并应用；
5. 立即安全保存首次完整显示的 API Key。

保存前程序会验证 SQL、必要视图、形态转换配置、U8 登录、库存模块初始化和注销。配置保存后立即生效。

安全机制：

- 配置页和管理 API 只允许本机回环地址；
- 首次设置后，后续配置操作要求管理员密码；
- 管理员密码使用 PBKDF2-SHA256、随机盐和 100000 次迭代；
- SQL 密码、U8 密码和 API Key 使用 DPAPI LocalMachine 加密；
- 配置视图只返回掩码 Key 和是否已保存密码，不回显明文密码；
- 轮换 API Key 后旧 Key 立即失效。

## 9. FastAPI 或其他调用方配置

调用方只需要 IIS API 基址、API Key 和超时。例如：

```dotenv
MATERIAL_OUTBOUND_API_BASE_URL="http://<iis-server>:9879/api/v1"
MATERIAL_OUTBOUND_API_KEY="<服务器生成的密钥>"
MATERIAL_OUTBOUND_API_TIMEOUT_SECONDS="120"
```

不要把 SQL 账号、U8 账号或管理员密码放入 PDA、浏览器前端或普通客户端。

业务接口请求头：

```http
X-API-Key: <API Key>
X-U8-Database: UFDATA_008_2021
Idempotency-Key: <8至64位稳定业务请求标识>
```

说明：

- 健康检查、Swagger 和本机管理路由不要求 `X-API-Key`；
- 其他业务接口必须提供有效 `X-API-Key`；
- 创建、审核、弃审等写操作必须使用稳定 `Idempotency-Key`；
- 同一业务请求重试必须使用原 Key，不能生成新 Key；
- `X-U8-Database` 支持 008、998；888 只允许通用盘点接口。

## 10. 核心接口概览

### 健康与文档

```text
GET /api/v1/health
GET /swagger
GET /admin/config                  仅服务器本机
```

### 生产领料与追溯标签

```text
GET  /api/v1/work-order-lines
POST /api/v1/material-outbound/previews
POST /api/v1/material-outbound-orders
GET  /api/v1/material-outbound-orders/{voucherCode}
POST /api/v1/material-outbound-orders/{voucherCode}/verify
POST /api/v1/material-outbound-orders/{voucherCode}/unverify
POST /api/v1/traceback-labels/lookups
POST /api/v1/traceback-labels/batch-lookups
```

### 通用材料出库

```text
POST /api/v1/general-material-outbound-orders/validate
POST /api/v1/general-material-outbound-orders
GET  /api/v1/general-material-outbound-orders/{voucherCode}
```

### 通用盘点

```text
POST /api/v1/general-inventory-count-orders/validate
POST /api/v1/general-inventory-count-orders
GET  /api/v1/general-inventory-count-orders/{voucherCode}
POST /api/v1/general-inventory-count-orders/{voucherCode}/actual-quantities
POST /api/v1/general-inventory-count-orders/{voucherCode}/line-updates
```

### 形态转换

```text
POST /api/v1/shape-conversions/previews
POST /api/v1/shape-conversions
GET  /api/v1/shape-conversions/{voucherCode}
POST /api/v1/shape-conversion-imports/previews
POST /api/v1/shape-conversion-imports
```

精确字段、业务约束和示例以模块 README 与 Swagger 为准。不要根据本概览直接构造生产写请求。

## 11. U8 COM 并发与重试边界

所有 U8 COM 工作通过单个 STA 后台线程串行执行：

```text
队列容量：50
系统级互斥锁：Global\MaterialOutboundApi.U8Operations
互斥等待上限：120 秒
```

应用程序池同时限制为单进程。不要通过增加工作进程、启用 Web Garden 或移除全局互斥锁来“提高并发”，这会破坏 U8 COM 会话隔离。

常见忙碌返回：

- `U8_QUEUE_FULL`：进程内队列已满；
- `U8_BUSY`：其他进程或操作持有系统互斥锁；
- `U8_ACCOUNT_IN_USE`：U8 用户正在其他客户端使用。

U8 写操作出现网络超时、进程失联或结果未确认时，不能直接换新的幂等键重放。应先查询 IIS 幂等状态、U8 单据和业务表结果，再决定使用原 Key 续跑或人工处理。

## 12. 升级发布

推荐使用：

```text
MaterialOutboundApi/Deployment/Deploy-IisPackage.ps1
```

默认参数：

```text
StagingPath = D:\MupiMaterialOutboundV2Deploy\package
TargetPath  = D:\MaterialOutboundApi
BackupRoot  = D:\MaterialOutboundApi-backups
SiteName    = MaterialOutboundApi
AppPoolName = MaterialOutboundApiPool
Port        = 9879
```

标准步骤：

1. 生成并核对 Release/x86 部署包。
2. 把部署包放入受控暂存目录。
3. 记录当前健康状态、版本和关键 DLL 哈希。
4. 对整个目标目录做部署前备份。
5. 停止网站和应用程序池，并等待应用池完全停止。
6. 覆盖程序文件，但排除部署包中的 `App_Data`。
7. 启动应用程序池和网站。
8. 最多执行 10 次本机 `/api/v1/health` 检查。
9. 健康失败时恢复部署前备份并重新启动。
10. 成功后继续执行只读业务验证。

当前部署脚本采用覆盖式复制，不会主动删除目标目录中已经不存在于新包的旧文件。若版本确实移除了程序集、页面或模板，应在备份后明确核对并受控清理这些陈旧文件，同时绝不能删除 `App_Data`。

部署脚本的健康成功条件只证明进程启动且返回 `status=running`。健康接口不会连接 U8，也不能证明 SQL、U8 COM、账套、业务参数或写单能力正常。

## 13. 部署后验证

### 13.1 必做只读检查

- `GET /api/v1/health` 返回 HTTP 200、`success=true`、`status=running`；
- `configured=true`；
- 版本号与本次发布目标一致；
- Swagger 能看到预期路由；
- 不带 API Key 调用受保护接口返回 401；
- 带 API Key 的只读查询可正常访问；
- 管理页“测试 SQL”和“测试 U8”成功；
- `App_Data/settings.json` 和 `state.db` 没有被部署包覆盖；
- `App_Data/Logs` 可写；
- 应用池仍是 32 位、单进程、无重叠回收。

### 13.2 业务验证

先使用只读接口或 preview/validate 接口：

- 工单查询；
- 标签批量查询和余额；
- 材料出库预览；
- 通用出库 validate；
- 通用盘点 validate；
- 形态转换 preview。

自动化部署和通用健康检查不得调用真实建单接口。真实 U8 写单验收必须由现场明确指定测试账套、测试数据、期望结果和回退方式，并在执行前再次核对幂等键。

## 14. 回滚

程序发布失败时：

1. 停止网站和应用程序池；
2. 恢复部署前备份的程序文件；
3. 保留需要继续识别已执行请求的最新 `App_Data`、幂等状态和日志；
4. 启动应用程序池和网站；
5. 检查健康、版本和只读接口；
6. 核对失败期间是否可能已经创建 U8 单据。

文件回滚不会撤销已经生成的 U8 单据。不得通过删除 `state.db`、更换幂等键或删除站点文件来“回滚”业务单据；应按 U8 正式业务流程审核、弃审或人工处置。

## 15. 安全要求

- 配置管理页只能在服务器本机打开；
- 业务端口防火墙仅开放给 Domain/Private 的本地子网；
- API Key 不写入 Swagger 示例、普通日志、PDA 安装包或前端资源；
- SQL 和 U8 密码只保存在 DPAPI 密文中；
- 不把 `App_Data` 提交到公开仓库或复制进部署包；
- 不在日志中输出解密后的连接字符串、密码或完整 API Key；
- 管理员密码首次至少 12 位；
- API Key 轮换会立即使旧 Key 失效，调用方必须协调更新；
- 当前记录为局域网 HTTP 部署，跨网段或扩大使用范围前应配置有效 HTTPS 证书；
- 不要把 Swagger 公开到不可信网络。

## 16. Web.config 关键点

当前配置：

```text
targetFramework = 4.8
maxRequestLength = 1024 KB
executionTimeout = 180 秒
customErrors = Off
httpErrors = DetailedLocalOnly
JSON = camelCase，忽略 null
```

程序集绑定重定向包括：

- Newtonsoft.Json 13.0.0.0；
- System.Web.Http 5.2.9.0；
- System.Net.Http.Formatting 5.2.9.0。

如果请求体超过 1 MB、操作超过 180 秒或依赖程序集版本发生变化，应先评估并同步修改配置和测试，不能只在 IIS 管理器中做不可追踪的临时调整。

## 17. 日志与观测

运行日志位于：

```text
D:\MaterialOutboundApi\App_Data\Logs
```

生产领料关键阶段会记录准备、登录、取号、新增、查询真实单号和审核耗时。API 错误响应携带请求 ID，排障时应同时收集：

- 请求时间；
- HTTP 方法和路由；
- 请求 ID；
- HTTP 状态与业务错误码；
- IIS 网站和应用池状态；
- Windows 事件日志；
- 对应日期的应用日志；
- U8 用户是否被人工客户端占用。

不要在故障截图或工单中泄露请求头里的 API Key。

## 18. 常见故障

### 503：NOT_CONFIGURED

站点已启动但没有完整配置。登录服务器本机配置页，完成 SQL/U8 测试并保存。

### 401：INVALID_API_KEY

调用方 Key 与 IIS 当前 Key 不一致。确认是否刚轮换过 Key，不要在日志中打印完整值。

### 503：U8_BUSY / U8_QUEUE_FULL

查看是否有长时间 U8 操作、另一个进程或脚本正在持有全局互斥锁。等待并核对结果，不要提高工作进程数量规避。

### U8_ACCOUNT_IN_USE

配置的 U8 用户正在其他客户端登录。让人工用户正常退出，接口不会强制踢出。

### IIS 可健康访问，但业务调用失败

健康接口不访问 SQL/U8。继续检查本机“测试 SQL”“测试 U8”、U8 COM 位数、应用池身份、账套配置和网络。

### 站点启动后立即停止

检查：

- .NET Framework 4.8 与 ASP.NET 4.x IIS 功能；
- 应用池是否启用 32 位；
- `bin` 依赖是否完整；
- U8 Interop/COM 是否安装和注册；
- `App_Data` ACL；
- Windows Application 事件日志；
- `Web.config` 程序集绑定。

### 配置文件复制到新服务器后无法读取

这是 DPAPI LocalMachine 绑定的预期行为。在新服务器本机重新配置，不要尝试把密文转换成明文或关闭加密。

### 发布后形态转换失败

确认部署包保留：

```text
ShapeConversionXml/head.xml
ShapeConversionXml/body.xml
```

并检查 008/998 形态转换配置档及对应 U8 账套。

## 19. 发布检查清单

- [ ] Release/x86 编译成功，版本号正确。
- [ ] 部署包位于 `artifacts` 下且内容完整。
- [ ] `XML` 和 `ShapeConversionXml` 模板齐全。
- [ ] 已记录现网健康、版本和关键 DLL 哈希。
- [ ] 已完成整个站点目录备份。
- [ ] 部署明确排除现有 `App_Data`。
- [ ] 应用程序池为 CLR v4.0、Integrated、32 位、单进程。
- [ ] `App_Data` 和日志目录 ACL 正确。
- [ ] 本机健康检查成功且版本正确。
- [ ] 配置状态为 configured。
- [ ] SQL 和 U8 本机测试通过。
- [ ] API Key 鉴权验证通过。
- [ ] 原有路由和新增路由均存在。
- [ ] 只读查询、preview 和 validate 通过。
- [ ] 未在自动部署过程中创建真实 U8 单据。
- [ ] 真实写单测试已有现场授权、测试数据和回退方案。
- [ ] 临时部署任务、明文临时文件和暂存凭据已清理。
- [ ] 已保存部署日志、包哈希和回滚位置。

## 20. Agent 实施边界

后续 Agent 执行 IIS 工作时必须：

1. 先确认目标服务器、目标账套、网站、应用池、端口和物理目录；
2. 把构建、部署、数据库迁移和真实 U8 写单视为不同阶段；
3. 默认只做健康与只读验证，不自动创建真实 U8 单据；
4. 所有写操作保留并复用幂等键；
5. 升级和回滚都保护 `App_Data`；
6. 不通过多进程或并行 COM 调用绕过队列；
7. 不把历史部署记录自动当成当前实时状态；
8. 涉及生产部署、数据库迁移或真实业务单据时，必须取得明确范围与现场授权。

权威源码入口：

```text
MaterialOutboundApi/MaterialOutboundApi.csproj
MaterialOutboundApi/Web.config
MaterialOutboundApi/Deployment/Build-Deployment.ps1
MaterialOutboundApi/Deployment/Install-IIS.ps1
MaterialOutboundApi/Deployment/Deploy-IisPackage.ps1
MaterialOutboundApi/Services/SettingsStore.cs
MaterialOutboundApi/Services/U8OperationQueue.cs
MaterialOutboundApi/README.md
```
