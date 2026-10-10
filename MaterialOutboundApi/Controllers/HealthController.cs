using System;
using System.Reflection;
using System.Web.Http;
using System.Web.Http.Description;
using MaterialOutboundApi.Examples;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;
using Swashbuckle.Examples;

namespace MaterialOutboundApi.Controllers
{
    [RoutePrefix("api/v1")]
    public sealed class HealthController : ApiController
    {
        /// <summary>返回 API 进程和配置状态，不连接 U8。</summary>
        [HttpGet]
        [Route("health")]
        [ResponseType(typeof(ApiResponse<HealthDto>))]
        [SwaggerResponseExample(
            System.Net.HttpStatusCode.OK,
            typeof(HealthResponseExample))]
        public IHttpActionResult Get()
        {
            return Ok(ApiResponse<HealthDto>.Ok(new HealthDto
            {
                Status = "running",
                Configured = AppRuntime.Settings.IsConfigured,
                ServerTime = DateTime.Now,
                Version = Assembly.GetExecutingAssembly()
                    .GetName().Version.ToString()
            }));
        }
    }
}
