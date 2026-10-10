# 本机配置、密码与密钥说明

本仓库只保存可共享的 U8 COM / IIS 源码和脱敏配置模板。数据库密码、U8 密码、API Key、管理员密码、Token、邮件密码、签名密钥和服务器运行状态不得提交到 Git。

## 1. 仓库中的配置模板

### MaterialOutboundConsole/App.config

该文件必须保留，因为控制台项目编译时需要它；仓库版本只包含 `YOUR_*` 占位符。

需要在部署副本中填写：

- `U8Database`：SQL Server、账套数据库、SQL 用户和密码；
- `U8SubId`、`U8AccId`、`U8Year`：U8 子系统、账套和年度；
- `U8UserId`、`U8Password`、`U8Server`、`U8Serial`：U8 登录参数；
- `WarehouseCode`、`DepartmentCode`、`Maker`、`WorkCenterCode`；
- `DefaultPositionCode`、`DefaultBatchCode`、`DefaultUnitCost`。

建议复制 Release 输出目录后只修改 `MaterialOutboundConsole.exe.config`。不要把填写后的文件复制回源码目录。SQL 和 U8 密码可以留空或保留 `YOUR_*`，由控制台在运行时以掩码方式询问；其他必填占位符未替换时程序会拒绝运行。

### 源码中的业务默认值

仓库可能保留单据类型、模板号、账套白名单、仓库货位规则等业务合同。这些不是密码，但迁移到其他 U8 环境前必须逐项核对，不能直接假设与目标账套一致。

源码中的服务器地址和登录用户默认值已清空或改为占位符。实际值应通过服务器本机管理页或部署副本配置，不应重新硬编码进源码。

部分运维脚本使用 `192.0.2.10` 作为默认服务器地址。它属于文档专用的 TEST-NET 保留网段，不代表真实服务器，也不应能连接生产环境；执行脚本时必须通过 `-Server` 显式传入实际服务器地址。

## 2. IIS 服务器配置

IIS 首次安装后，在服务器本机打开：

```text
http://localhost:9879/admin/config
```

运行配置保存在：

```text
D:\MaterialOutboundApi\App_Data\settings.json
```

其中 SQL 密码、U8 密码和 API Key 使用 Windows `LocalMachine` DPAPI 保护。该密文只能在原服务器环境中正常解密，不能当作可跨机器迁移的配置。迁移服务器时，应在新服务器本机重新填写并保存。

以下整个目录都不得提交：

```text
D:\MaterialOutboundApi\App_Data\
├─ settings.json
├─ state.db
└─ Logs\
```

`state.db` 包含预览和幂等状态；日志可能包含单据号、请求 ID 和错误上下文。升级服务器时需要保留这些文件，但不能放进公开仓库。

## 3. 调用方配置

FastAPI、网页或其他调用方通常需要：

```dotenv
MATERIAL_OUTBOUND_API_BASE_URL="http://<server>:9879/api/v1"
MATERIAL_OUTBOUND_API_KEY="<secret>"
MATERIAL_OUTBOUND_API_TIMEOUT_SECONDS="120"
```

本机需要运行盘点辅助脚本时，把仓库根目录 `.env.example` 复制为 `.env.local` 后填写。真实 `.env`、`.env.local`、`.env.profiles.local` 不得提交；仓库中只允许保留空值或明显占位符组成的 `.env.example`。

`Edit-U8InventoryCount.ps1` 还支持 `-EnvironmentPath` 指定外部 dotenv 文件，便于自动化测试或把配置放在仓库外；未指定时仍读取仓库根目录 `.env.local`。

## 4. 不得提交的文件

- `App_Data/settings.json`、`state.db` 和日志；
- 填写过真实值的 `.env*`；
- Android 或代码签名的 `.jks`、`.keystore`、`.pfx`、`.p12`、私钥及密码文件；
- 控制台 `Output`、U8 原始 XML、盘点 Recordset XML；
- `artifacts`、`bin`、`obj` 和临时部署包；
- 生产数据库备份、导入 Excel、导出结果和故障现场数据。

原始 U8 XML 可能包含单号、料号、批号、部门、仓库、货位和操作员信息。允许在受控服务器上临时导出用于核对 Schema，但不得直接提交。

## 5. 提交前检查

提交或推送前至少执行：

```powershell
git status --short
git diff --check
git grep -n -i -E "password|secret|token|api[_-]?key|private[_-]?key"
```

最后一条命令会同时命中文档和字段名称，需要人工确认值是空值、占位符或代码变量，而不是真实凭据。还应检查暂存区：

```powershell
git diff --cached --name-only
git diff --cached
```

发现凭据已经进入 Git 历史时，仅删除当前文件不够；应立即轮换该凭据，并在发布前清理历史或重新建立无历史的干净仓库。

## 6. Agent 处理规则

- 不读取、回显或复制真实密码和密钥到文档、日志、提交说明或聊天消息；
- 不把现有私有项目的 Git 历史直接推送到公开仓库；
- 需要示例时使用 `<server>`、`<secret>` 或 `YOUR_*`；
- 运行真实新增、审核、弃审、删除或修改单据前，必须取得人工明确确认；
- 网络超时或结果未知时先查询 U8 和幂等状态，不生成新幂等键盲目重试。
