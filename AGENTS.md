# Agent 工作说明

开始修改前先阅读：

- `README.md`
- `IIS_DEPLOYMENT_AND_OPERATIONS_AGENT_GUIDE.md`
- `LOCAL_CONFIGURATION_AND_SECRETS.md`

## 运行边界

- 项目目标为 .NET Framework 4.8、x86；U8 COM 只能在安装了匹配 U8 客户端和组件的 Windows 环境运行。
- IIS 应用池必须启用 32 位、单工作进程，并保持 U8 COM 操作串行化。
- 构建和自动验证不得调用真实新增、审核、弃审、删除或修改单据接口。
- 创建类请求必须保持原 `Idempotency-Key` 重试；结果未知时先查询，不得换 Key 重复制单。

## 配置与数据

- 禁止提交真实密码、API Key、Token、签名文件、`.env`、`App_Data`、日志和 U8 原始 XML。
- `MaterialOutboundConsole/App.config` 必须保持为占位模板；真实值只写入部署副本。
- 更新 XML 模板时保留命名空间、字段顺序、类型和 `@z_row`，并先完成只读预览。
- 不提交 `artifacts`、`bin`、`obj` 或其他生成目录。

## 基本验证

```powershell
dotnet build .\MaterialOutboundApi\MaterialOutboundApi.csproj -c Release -p:Platform=x86
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\MaterialOutboundApi\Deployment\Build-Deployment.ps1 `
  -Configuration Release
```

提交前执行 `git diff --check`，并按 `LOCAL_CONFIGURATION_AND_SECRETS.md` 完成脱敏检查。
