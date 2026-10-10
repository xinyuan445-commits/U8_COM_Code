using System.Web.Http;
using System.Web.Http.Description;
using System.Collections.Generic;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;

namespace MaterialOutboundApi.Controllers
{
    [RoutePrefix("api/v1/traceback-labels")]
    public sealed class TraceBackLabelsController : ApiController
    {
        /// <summary>按二维码完整追溯码读取 TraceBack 标签和货物信息。</summary>
        [HttpPost]
        [Route("lookups")]
        [ResponseType(typeof(ApiResponse<TraceBackLabelDto>))]
        public IHttpActionResult Lookup(TraceBackLabelLookupRequest request)
        {
            if (request == null || !ModelState.IsValid)
            {
                throw new ApiException(
                    System.Net.HttpStatusCode.BadRequest,
                    "TRACEBACK_CODE_REQUIRED",
                    "标签追溯码不能为空。");
            }
            TraceBackLabelDto data =
                AppRuntime.MaterialOutbound.GetTraceBackLabel(
                    request.TraceBackCode);
            return Ok(ApiResponse<TraceBackLabelDto>.Ok(data));
        }

        /// <summary>一次读取多张 TraceBack 标签，供整单上传批量复核。</summary>
        [HttpPost]
        [Route("batch-lookups")]
        [ResponseType(typeof(ApiResponse<List<TraceBackLabelDto>>))]
        public IHttpActionResult BatchLookup(
            TraceBackLabelBatchLookupRequest request)
        {
            if (request == null || !ModelState.IsValid ||
                request.TraceBackCodes == null ||
                request.TraceBackCodes.Count == 0)
            {
                throw new ApiException(
                    System.Net.HttpStatusCode.BadRequest,
                    "TRACEBACK_CODES_REQUIRED",
                    "标签追溯码列表不能为空。");
            }
            List<TraceBackLabelDto> data =
                AppRuntime.MaterialOutbound.GetTraceBackLabels(
                    request.TraceBackCodes);
            return Ok(ApiResponse<List<TraceBackLabelDto>>.Ok(data));
        }
    }
}
