using System.Web;
using System.Web.Http;
using MaterialOutboundApi.Infrastructure;

namespace MaterialOutboundApi
{
    public class WebApiApplication : HttpApplication
    {
        protected void Application_Start()
        {
            AppRuntime.Initialize(Server.MapPath("~/App_Data"));
            GlobalConfiguration.Configure(config =>
            {
                WebApiConfig.Register(config);
                SwaggerConfig.Register(config);
            });
        }

        protected void Application_End()
        {
            AppRuntime.Dispose();
        }
    }
}
