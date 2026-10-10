using System.IO;
using System.Web;
using System.Web.Http;
using Swashbuckle.Application;
using Swashbuckle.Examples;

namespace MaterialOutboundApi
{
    internal static class SwaggerConfig
    {
        public static void Register(HttpConfiguration configuration)
        {
            configuration
                .EnableSwagger(c =>
                {
                    c.SingleApiVersion("v1", "U8 库存单据 API")
                        .Description("用友 U8 16.0 材料出库、形态转换和通用盘点单接口。");
                    c.ApiKey("apiKey")
                        .Description("在本机配置页生成的 API Key。")
                        .Name("X-API-Key")
                        .In("header");
                    c.OperationFilter<ExamplesOperationFilter>();
                    c.OperationFilter<RequestHeaderFilter>();
                    string xml = HttpContext.Current == null
                        ? null
                        : HttpContext.Current.Server.MapPath(
                            "~/bin/MaterialOutboundApi.xml");
                    if (!string.IsNullOrWhiteSpace(xml) && File.Exists(xml))
                    {
                        c.IncludeXmlComments(xml);
                    }
                    string generalXml = HttpContext.Current == null
                        ? null
                        : HttpContext.Current.Server.MapPath(
                            "~/bin/GeneralMaterialOutbound.xml");
                    if (!string.IsNullOrWhiteSpace(generalXml) &&
                        File.Exists(generalXml))
                    {
                        c.IncludeXmlComments(generalXml);
                    }
                    string inventoryCountXml = HttpContext.Current == null
                        ? null
                        : HttpContext.Current.Server.MapPath(
                            "~/bin/GeneralInventoryCount.xml");
                    if (!string.IsNullOrWhiteSpace(inventoryCountXml) &&
                        File.Exists(inventoryCountXml))
                    {
                        c.IncludeXmlComments(inventoryCountXml);
                    }
                })
                .EnableSwaggerUi(c =>
                {
                    c.DocumentTitle("U8 库存单据 API 文档");
                    c.EnableApiKeySupport("X-API-Key", "header");
                });
        }
    }
}
