using System.Net.Http.Formatting;
using System.Web.Http;
using MaterialOutboundApi.Infrastructure;
using Newtonsoft.Json.Serialization;

namespace MaterialOutboundApi
{
    internal static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            config.MapHttpAttributeRoutes();
            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional });

            config.Filters.Add(new ApiExceptionFilter());
            config.MessageHandlers.Add(new RequestIdHandler());
            config.MessageHandlers.Add(new ApiKeyHandler());
            config.MessageHandlers.Add(new U8DatabaseHandler());

            config.Formatters.Remove(config.Formatters.XmlFormatter);
            JsonMediaTypeFormatter json = config.Formatters.JsonFormatter;
            json.SerializerSettings.ContractResolver =
                new CamelCasePropertyNamesContractResolver();
            json.SerializerSettings.NullValueHandling =
                Newtonsoft.Json.NullValueHandling.Ignore;
        }
    }
}
