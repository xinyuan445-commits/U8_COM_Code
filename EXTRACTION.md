# 剥离为独立项目

本仓库已从木皮扫码主项目中剥离。生产领料 V2 对 IIS API 的调用边界只有
HTTP，不依赖 Python 或小程序源码。

## 必须复制

1. `MaterialOutboundApi` 全目录。
2. `MaterialOutboundConsole` 全目录。API 项目通过项目引用复用其中的模型、XML
   模板和必要 COM 引用，不能只复制 API 的 `.cs` 文件。
3. `GeneralMaterialOutbound` 与 `GeneralInventoryCount` 全目录。
4. 根目录文档、安装脚本和脱敏配置模板。

不要复制 `bin`、`obj`、服务器的 `App_Data` 密文或任何本地密钥。目标服务器应
重新发布并通过后台重新录入配置，因为 DPAPI 密文通常只能由原机器/身份解密。

## 独立仓库建议结构

```text
u8-material-outbound/
  MaterialOutboundApi/
  MaterialOutboundConsole/
  GeneralMaterialOutbound/
  GeneralInventoryCount/
  AGENTS.md
  LOCAL_CONFIGURATION_AND_SECRETS.md
  README.md
  EXTRACTION.md
```

剥离后先执行：

```powershell
dotnet build .\MaterialOutboundApi\MaterialOutboundApi.csproj -c Release
```

然后按 `README.md` 部署到独立 IIS 应用。FastAPI 无需改代码，只需把
`MATERIAL_OUTBOUND_API_BASE_URL` 和 `MATERIAL_OUTBOUND_API_KEY` 指向新地址。

## 兼容约束

- 对外路径和 JSON 字段保持向后兼容。
- `Idempotency-Key` 的语义不得改变；重复请求必须恢复原 U8 单号。
- 继续按 `allocateId` 汇总校验，允许同一需求行多个标签/批号。
- 保持仓库 `KML_PP`、货位 `PP2`、类别 `202` 为可部署配置的当前固定值。
- 创建材料出库单后自动审核；同一幂等键重试不会重复生成单据。
