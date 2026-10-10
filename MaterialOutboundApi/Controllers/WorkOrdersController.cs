using System.Collections.Generic;
using System.Web.Http;
using System.Web.Http.Description;
using MaterialOutboundApi.Examples;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;
using Swashbuckle.Examples;

namespace MaterialOutboundApi.Controllers
{
    [RoutePrefix("api/v1/work-order-lines")]
    public sealed class WorkOrdersController : ApiController
    {
        /// <summary>模糊查询生产订单明细，或按工单号和行号精准查询。</summary>
        [HttpGet]
        [Route("")]
        [ResponseType(typeof(ApiResponse<List<WorkOrderLineDto>>))]
        [SwaggerResponseExample(
            System.Net.HttpStatusCode.OK,
            typeof(WorkOrderLinesResponseExample))]
        public IHttpActionResult GetLines(
            string keyword, int? sortSeq = null, bool includeInactive = false)
        {
            List<WorkOrderLineDto> data =
                AppRuntime.MaterialOutbound.SearchWorkOrderLines(
                    keyword,
                    sortSeq,
                    includeInactive);
            return Ok(ApiResponse<List<WorkOrderLineDto>>.Ok(data));
        }
    }
}
