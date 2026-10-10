# 用友 U8 材料出库单控制台项目说明

## 1. 项目用途

`MaterialOutboundConsole` 是一个基于用友 U8 库存管理 CO 接口的 .NET Framework
控制台程序，用于根据生产工单生成材料出库单。

主要功能：

- 输入生产工单号。
- 从账套视图 `dbo.momlist_yx` 查询并显示工单明细。
- 选择一条、多条或全部工单明细。
- 从 `dbo.mom_moallocate` 取得生产订单子件用料。
- 生成材料出库单表头、表体 XML 预览。
- 用户输入大写 `YES` 后，通过 `USERPCO.VoucherCO` 提交到 U8。
- 支持查询、修改数量、审核、弃审和删除材料出库单。
- 发生异常时生成包含完整异常堆栈和 HRESULT 的日志。

当前程序面向以下环境：

| 项目 | 当前值 |
|---|---|
| U8 服务器 | `<U8-server>` |
| 账套 | `998`（生产领料 V2 测试账套） |
| 账套数据库 | `UFDATA_998_2021` |
| 登录年度 | `2026` |
| U8 操作员编码 | `yx` |
| U8 模块 | `ST` |
| 单据日期 | `2026-07-29` |
| 制单人 | `yuanxin` |
| 固定仓库 | `KML_KSW` |
| 固定货位编码 | `480` |
| 货位名称 | `1210`，运行时从 U8 档案校验取得 |
| 固定批号 | `20260514GXS` |

数据库密码和 U8 操作员密码不保存在项目中，运行时隐藏输入。

## 2. 技术环境

| 项目 | 要求 |
|---|---|
| 目标框架 | .NET Framework 4.8 |
| CPU 平台 | x86 |
| U8 版本 | U8 16.0，当前服务器组件版本 `16.00.8702` |
| 数据库 | SQL Server |
| U8 接口 | `U8Login.clsLogin`、`USERPCO.VoucherCO` |
| XML | MSXML 2.6 COM 接口 |

项目必须编译为 x86。当前 U8 COM 组件为 32 位组件，如果编译成
`Any CPU` 或 x64，容易出现“无法创建 ActiveX 组件”、类未注册或 Automation
错误。

## 3. 项目目录

```text
MaterialOutboundConsole
├─ App.config                         程序配置
├─ MaterialOutboundConsole.csproj     项目文件，目标 net48/x86
├─ Program.cs                         命令入口和交互流程
├─ Configuration
│  └─ AppOptions.cs                   配置加载和校验
├─ ConsoleUi
│  ├─ ConsoleSecret.cs                密码隐藏输入
│  └─ SelectionParser.cs              解析 1,3-5、all 等选择表达式
├─ Data
│  └─ U8Repository.cs                 账套数据库只读查询
├─ Models
│  └─ U8Models.cs                     工单、表头和表体数据模型
├─ Services
│  ├─ MaterialOutboundService.cs      材料出库业务及 CO 调用
│  ├─ U8CoSession.cs                  U8 登录和 VoucherCO 初始化
│  ├─ U8ComException.cs               COM 异常增强
│  └─ VoucherXmlBuilder.cs            表头、表体 XML 生成
├─ XML
│  ├─ head.xml                        U8 材料出库单表头模板
│  └─ body.xml                        U8 材料出库单表体模板
└─ lib                                编译所需托管 DLL
```

运行后还会生成：

```text
Output\<单号-时间>\head.xml
Output\<单号-时间>\body.xml
Logs\error-yyyyMMdd-HHmmss.log
```

## 4. DLL 和运行依赖

发布目录包含：

| 文件 | 用途 |
|---|---|
| `Dapper.dll` | SQL 查询映射 |
| `adodb.dll` | U8 CO 接口需要的 ADODB 类型 |
| `Interop.U8Login.dll` | U8 登录 COM 的 .NET 封装 |
| `Interop.USERPCO.dll` | 库存 VoucherCO 的 .NET 封装 |
| `Interop.USCOMMON.dll` | U8 公共接口封装 |
| `Interop.MSXML2.dll` | MSXML COM 封装 |
| `Interop.Scripting.dll` | U8 相关 COM 类型封装 |
| `Interop.VBA.dll` | U8 相关 COM 类型封装 |

这些 `Interop.*.dll` 只是 .NET 调用 COM 的类型封装，不能代替服务器上安装的
U8 客户端和底层 COM 组件。运行机器仍必须安装与账套版本匹配的 U8 客户端/CO
组件。

当前 `Interop.USERPCO.dll` 根据服务器上的下列组件重新生成：

```text
D:\U8SOFT\ufcomsql\USERPCO.dll
FileVersion    = 16.00.8702
ProductVersion = 16.00.8702
SHA256         = 66BE037A9DCB849AC8BA5BF2BF3B2D1C7D0D887A20587C3A934476A3EBEED1D0
VoucherCO CLSID = {5630AF7A-34CF-4897-9AE9-9A77206843F1}
```

不要使用其他 U8 版本生成的 `Interop.USERPCO.dll` 覆盖当前文件。

## 5. 数据来源和单据关联

程序查询以下对象：

| 对象 | 用途 |
|---|---|
| `dbo.momlist_yx` | 查询工单及工单明细 |
| `dbo.mom_moallocate` | 查询生产订单子件及用量 |
| `dbo.Warehouse` | 校验仓库及是否启用货位管理 |
| `dbo.Position` | 校验货位编码并取得货位名称 |
| `dbo.Rd_Style` | 核对收发类别 |
| `dbo.rdrecord11` | 材料出库单表头查询 |
| `dbo.rdrecords11` | 材料出库单表体查询 |

SQL 查询本身是只读的。新增、修改、审核、弃审和删除操作通过 U8
`VoucherCO` 执行，不直接向 U8 单据表写 SQL。

生产订单关联字段：

| U8 字段 | 数据来源 |
|---|---|
| 表头 `cmpocode` | 生产工单号 |
| 表头 `iproorderid` | `MoId` |
| 表体 `cmocode` | 生产工单号 |
| 表体 `imoseq` | 工单行号 `SortSeq` |
| 表体 `impoids` | 子件分配记录 `AllocateId` |
| 表体 `iopseq` | 工序号 `OpSeq` |
| 表体 `cinvcode` | 子件存货编码 |
| 表体 `invcode` | 工单产品编码 |

材料出库数量按以下方式计算：

```text
出库数量 = BaseQtyN / BaseQtyD × (1 + CompScrap) × 工单数量
```

如果基本用量分母为空或为零，程序停止生成单据并提示具体子件。

## 6. 当前材料出库单默认值

### 6.1 表头

| 字段 | 值 | 说明 |
|---|---|---|
| `brdflag` | `0` | 出库方向 |
| `cvouchtype` | `11` | 材料出库单 |
| `crdcode` | `202` | 领料出库 |
| `csource` | `生产订单` | 单据来源 |
| `cbustype` | `领料` | 业务类型 |
| `vt_id` | `65` | U8 单据模板 |
| `bisstqc` | `False` | 库存质检标志 |
| `bpufirst` | `False` | 非采购先行 |
| `biafirst` | `False` | 非存货核算先行 |
| `biscomplement` | `0` | 非补料单 |
| `ireturncount` | `0` | 非退料 |
| `bmotran` | `0` | 非生产调拨 |
| `iPrintCount` | `0` | 初始打印次数 |
| `cwhcode` | `KML_KSW` | 固定仓库 |
| `cdepcode` | 工单部门 | 从 `momlist_yx` 取得 |
| `cmaker` | `yuanxin` | 制单人 |
| `ccode` | 手工输入或尝试自动编号 | 当前建议手工输入 |

### 6.2 表体

| 字段 | 值或来源 |
|---|---|
| `bcosting` | `True` |
| `bvmiused` | `False` |
| `iexpiratdatecalcu` | `0` |
| `iquantity` | 计算出的子件出库数量 |
| `inquantity` | 与出库数量相同 |
| `iunitcost` | `0` |
| `iprice` | `0` |
| `cposition` | `480` |
| `cposname` | `1210`，从货位档案取得 |
| `iposflag` | `1` |
| `cbatch` | `20260514GXS` |
| `editprop` | `A`，新增行 |

注意：当前批号会应用到本次材料出库单的所有表体行。如果一次选择的多种子件
不属于同一批号，应先修改 `DefaultBatchCode`，或者按批号分别生成材料出库单，
否则 U8 可能提示批号无效、现存量不足，或者造成批次归属错误。

## 7. 配置文件

部署后维护：

```text
MaterialOutboundConsole.exe.config
```

关键配置：

```xml
<add key="U8SubId" value="ST" />
<add key="U8AccId" value="(default)@998" />
<add key="U8Year" value="2026" />
<add key="U8UserId" value="yx" />
<add key="U8Date" value="2026-07-29" />
<add key="U8Server" value="YOUR_U8_SERVER" />

<add key="WarehouseCode" value="KML_KSW" />
<add key="DepartmentCode" value="" />
<add key="Maker" value="yuanxin" />
<add key="WorkCenterCode" value="" />
<add key="DefaultPositionCode" value="480" />
<add key="DefaultBatchCode" value="20260514GXS" />
<add key="DefaultUnitCost" value="0" />
```

连接字符串中保留空密码：

```xml
Password=
```

运行时再输入 SQL Server 密码。不要把数据库密码或 U8 密码写入配置文件、说明
文档、批处理文件或提交到源码仓库。

配置中的：

```xml
<startup useLegacyV2RuntimeActivationPolicy="true">
```

用于兼容 U8 老版本 CLR/COM 组件，不应删除。

## 8. 部署和运行

### 8.1 部署

把 Release 发布包完整解压到：

```text
D:\MaterialOutboundConsole
```

必须同时保留：

- `MaterialOutboundConsole.exe`
- `MaterialOutboundConsole.exe.config`
- 所有依赖 DLL
- `XML\head.xml`
- `XML\body.xml`

### 8.2 交互生成材料出库单

```bat
cd /d D:\MaterialOutboundConsole
MaterialOutboundConsole.exe select-add
```

操作流程：

1. 输入 SQL Server 密码。
2. 输入工单号，例如 `SCDD26072885`。
3. 查看工单明细。
4. 输入 `1`、`1,3-5` 或 `all` 选择记录。
5. 程序显示固定仓库、货位和批号。
6. 输入材料出库单号；直接回车时尝试调用 U8 自动编号。
7. 查看生成的 `head.xml`、`body.xml`。
8. 仅预览时直接回车。
9. 确认提交时输入大写 `YES`。
10. 输入 U8 操作员密码。

程序只有在输入大写 `YES` 后才调用 U8 新增接口。

### 8.3 其他命令

```text
preview-add <生产订单号> <材料出库单号|auto> <仓库编码>
add         <生产订单号> <材料出库单号|auto> <仓库编码> --confirm
query       <材料出库单号>
export-shape-template <形态转换单号>
update      <材料出库单号> <表体AutoID> <新数量> --confirm
verify      <材料出库单号> --confirm
unverify    <材料出库单号> --confirm
delete      <材料出库单号> --confirm
```

除交互式 `select-add` 的二次 `YES` 确认外，其他写操作必须显式添加
`--confirm`。

### 8.4 导出形态转换单 XML 模板

形态转换主单存放在 `AssemVouch/AssemVouchs`，当前账套的单据类型为
`15`、模板号为 `91`。执行：

```bat
MaterialOutboundConsole.exe export-shape-template XTZH00001508
```

输入 SQL Server 密码和 U8 操作员密码后，程序只读调用
`VoucherCO.Load("15", "ID=...")`，不会新增、修改或审核单据。输出目录包含：

```text
Output\shape-template-单号-时间
├─ raw
│  ├─ head.xml
│  ├─ body.xml
│  └─ position.xml
└─ template
   ├─ head.xml
   └─ body.xml
```

`raw` 是 U8 返回的原始单据 XML；`template` 已将真实数据行替换为
`@z_row`，可作为同版本 U8 的 CO XML 模板。

## 9. 编译

```powershell
dotnet build .\MaterialOutboundConsole.csproj -c Debug
dotnet build .\MaterialOutboundConsole.csproj -c Release
```

输出目录：

```text
bin\Debug\net48
bin\Release\net48
```

发布前应确认生成结果为：

```text
0 个警告
0 个错误
```

## 10. 遇到的问题及解决办法

### 10.1 控制台只显示“Automation 错误”

现象：

```text
错误：Automation 错误
```

原因：

- Release 环境最初只输出简化异常消息。
- COMException 中真正有用的 HRESULT、InnerException 和堆栈没有显示。

解决：

- 控制台改为输出完整 `exception.ToString()`。
- 对 U8 登录、VoucherCO 创建、初始化、自动编号和新增分别捕获
  `COMException`。
- 日志记录 HRESULT、异常详情和调用阶段。
- 日志保存到 `Logs\error-yyyyMMdd-HHmmss.log`。

定位此类错误时，应先看日志中的“在哪一步失败”和 HRESULT，不要只看最外层
“Automation 错误”。

### 10.2 HRESULT `0x80131700`

现象：

```text
CLR initialization failed
HRESULT=0x80131700
```

原因：

U8 的部分 COM 组件依赖旧版 CLR 激活方式，而当前程序运行在 .NET Framework
4.8。

解决：

在配置文件中加入：

```xml
<startup useLegacyV2RuntimeActivationPolicy="true">
  <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
</startup>
```

同时保持程序为 x86。

### 10.3 `USERPCO.dll` 已存在，但查询指定 CLSID 不存在

检查过：

```bat
dir D:\U8SOFT\ufcomsql\USERPCO.dll
C:\Windows\SysWOW64\regsvr32.exe "D:\U8SOFT\ufcomsql\USERPCO.dll"
reg query "HKCR\CLSID\{86C74D5E-EED0-4D44-9258-103DC8948300}" /reg:32
```

最后一条提示找不到注册表项。

原因：

`{86C74D5E-EED0-4D44-9258-103DC8948300}` 不是当前服务器
U8 16.0 `VoucherCO` 的 CLSID。仅凭其他版本案例中的 CLSID 判断当前服务器会
产生误导。

解决：

1. 读取服务器实际 `USERPCO.dll` 的版本和哈希。
2. 根据服务器实际 DLL 的类型库重新生成 `Interop.USERPCO.dll`。
3. 从生成的 Interop 元数据确认当前 `VoucherCOClass` CLSID。
4. 当前实际 CLSID 为：

```text
{5630AF7A-34CF-4897-9AE9-9A77206843F1}
```

结论：U8 COM 问题必须以运行服务器上的实际版本和类型库为准，不能直接套用
课程案例或其他服务器的 CLSID。

### 10.4 自动取得材料出库单号失败

现象：

调用 `VoucherCO.GetNewVouchCode` 后没有返回有效单号。

原因：

当前账套的材料出库单自动编号规则可能未配置、不可用，或当前操作员没有相应
编号权限。

解决：

- 交互流程增加材料出库单号输入。
- 输入单号时直接使用手工单号。
- 只有直接回车时才尝试 U8 自动编号。

后续如需恢复自动编号，应在 U8 中检查单据编号设置，而不是在程序中模拟编号，
以免出现重复单号。

### 10.5 “收发类别不合法”

原值：

```text
crdcode = 21
```

原因：

`21` 来自课程案例，但 008 账套的 `Rd_Style` 档案中不存在该编码。不同账套的
收发类别属于基础档案，不能直接照搬。

只读核对结果：

```text
cRdCode = 202
cRdName = 领料出库
bRdFlag = 0
```

账套内已有 8,581 张“生产订单 + 领料”的材料出库单使用：

```text
crdcode  = 202
cbustype = 领料
csource  = 生产订单
vt_id    = 65
```

解决：

将材料出库单表头 `crdcode` 改为 `202`。

### 10.6 仓库启用了货位管理，但表体没有货位

现象：

U8 提示必须录入货位，或货位信息不完整。

原因：

仓库 `KML_KSW` 的 `bWhPos=True`，材料出库表体必须携带货位信息。

解决：

- 固定仓库：`KML_KSW`
- 固定货位编码：`480`
- 从 `Position` 档案校验货位属于该仓库。
- 从档案取得货位名称：`1210`
- 表体写入：

```text
cposition = 480
cposname   = 1210
iposflag   = 1
```

如果以后更换仓库，必须重新检查仓库是否启用货位管理以及货位是否属于新仓库。

### 10.7 “没有写批号”或“批号不能为空”

现象：

提交材料出库单时，U8 提示批次管理存货没有批号。

原因：

对应子件启用了批次管理，而生成的表体最初没有输出 `cbatch`。

解决：

在材料出库表体模型和 XML 中增加：

```text
cbatch = 20260514GXS
```

修改已有材料出库单数量时，也读取并保留原表体的 `cBatch`，避免更新时将批号
清空。

如果下一步出现“批号不存在”或“现存量不足”，需要核对该批号在
`KML_KSW/480` 下的实际结存数量，而不是继续增加无关默认字段。

### 10.8 工单部门为空或选择了多个部门

现象：

- 所选工单没有生产部门。
- 一次选择的明细属于多个部门。

原因：

一张材料出库单表头只能使用一个部门。

解决：

- 部门优先从 `momlist_yx.MDeptCode_b` 取得。
- 多部门选择时程序拒绝生成，要求按部门分开操作。
- 当前没有设置默认部门，工单部门为空时直接报错，避免生成错误归属的单据。

### 10.9 U8 登录失败

应依次检查：

1. `U8SubId=ST` 是否正确。
2. `U8AccId=(default)@998` 是否是该服务器有效的数据源/账套标识。
3. 登录年度是否已建立。
4. 操作员 `yx` 是否有效。
5. 单据日期是否在可登录会计期间。
6. U8 操作员密码是否正确。
7. 操作员是否拥有库存管理和材料出库单权限。
8. U8 客户端组件版本是否与服务器一致。

### 10.10 SQL Server 连接失败

应检查：

- 配置的 U8 服务器是否可访问。
- 数据库 `UFDATA_998_2021` 是否存在。
- SQL Server 是否允许 SQL 身份验证。
- 临时密码是否过期。
- 防火墙和 SQL Server 端口是否可达。

密码必须在运行时输入，不应写回连接字符串。

## 11. 当前限制和注意事项

1. 固定批号会应用到所有生成的表体行，只适合同批材料；多批次领料需扩展为
   逐子件选择批号。
2. 当前默认单价和金额为 `0`，由 U8 后续成本计算；账套已有单据中这些字段也
   可能保存为 `NULL`。
3. 当前单据日期为固定日期，跨日运行前应确认日期是否仍符合业务要求。
4. 自动编号依赖 U8 账套编号设置，当前建议手工输入单据号。
5. 项目根据指定账套的档案和自定义视图开发，迁移到账套外环境时必须重新核对：
   收发类别、模板、仓库、货位、批号、部门、操作员和 COM 版本。
6. 正式提交前应先检查生成的 `head.xml` 和 `body.xml`，确认工单、子件、数量、
   仓库、货位和批号无误。

## 12. 建议的生产验证顺序

1. 先运行 `select-add`，生成 XML 后直接回车，不提交。
2. 检查表头 `crdcode=202`、`cwhcode=KML_KSW`。
3. 检查每行 `cposition=480`、`iposflag=1`、`cbatch=20260514GXS`。
4. 使用一个数量较小、业务人员确认过的工单进行首次提交。
5. 在 U8 客户端中查询新单，核对来源、工单关联、子件、数量、仓库、货位和
   批号。
6. 确认无误后再审核。
7. 如失败，保留 `Logs` 下最新错误日志和本次 `Output` XML，便于继续定位。
