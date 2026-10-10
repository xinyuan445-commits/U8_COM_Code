using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Description;
using GeneralMaterialOutbound.Models;

namespace GeneralMaterialOutbound.Controllers
{
    /// <summary>通用 U8 材料出库单接口。</summary>
    [RoutePrefix("api/v1/general-material-outbound-orders")]
    public sealed class GeneralMaterialOutboundOrdersController : ApiController
    {
        /// <summary>只读校验并解析通用材料出库请求，不生成单据。</summary>
        [HttpPost]
        [Route("validate")]
        [ResponseType(typeof(GeneralApiResponse<GeneralMaterialOutboundValidationDto>))]
        public IHttpActionResult Validate(GeneralMaterialOutboundRequest request)
        {
            ValidateModel();
            GeneralMaterialOutboundValidationDto data =
                GeneralMaterialOutboundRuntime.Gateway.Validate(request);
            return Ok(Response(data, "校验通过"));
        }

        /// <summary>新增通用材料出库单，并按 autoVerify 决定是否自动审核。</summary>
        [HttpPost]
        [Route("")]
        [ResponseType(typeof(GeneralApiResponse<GeneralMaterialOutboundCreatedDto>))]
        public async Task<IHttpActionResult> Create(
            GeneralMaterialOutboundRequest request)
        {
            ValidateModel();
            string key = Request.Headers.TryGetValues(
                    "Idempotency-Key",
                    out IEnumerable<string> values)
                ? values.FirstOrDefault()
                : null;
            GeneralMaterialOutboundCreatedDto data =
                await GeneralMaterialOutboundRuntime.Gateway.Create(request, key);
            return Content(
                HttpStatusCode.Created,
                Response(
                    data,
                    data.Replayed ? "返回原请求结果" : "材料出库单新增成功"));
        }

        /// <summary>按材料出库单号查询单头、单身及审核状态。</summary>
        [HttpGet]
        [Route("{voucherCode}")]
        [ResponseType(typeof(GeneralApiResponse<GeneralMaterialOutboundDto>))]
        public IHttpActionResult Get(string voucherCode)
        {
            GeneralMaterialOutboundDto data =
                GeneralMaterialOutboundRuntime.Gateway.Get(voucherCode);
            return Ok(Response(data, "查询成功"));
        }

        private static GeneralApiResponse<T> Response<T>(T data, string message)
        {
            return new GeneralApiResponse<T>
            {
                Success = true,
                Code = "OK",
                Message = message,
                RequestId = RequestId(),
                Data = data
            };
        }

        private void ValidateModel()
        {
            if (ModelState.IsValid) return;
            string message = string.Join(
                "；",
                ModelState.Values
                    .SelectMany(x => x.Errors)
                    .Select(x => string.IsNullOrWhiteSpace(x.ErrorMessage)
                        ? x.Exception?.Message
                        : x.ErrorMessage)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            throw new GeneralMaterialOutboundException(
                400,
                "VALIDATION_ERROR",
                string.IsNullOrWhiteSpace(message)
                    ? "请求参数不合法。"
                    : message);
        }

        private static string RequestId()
        {
            object value = HttpContext.Current?.Items[
                "MaterialOutboundApi.RequestId"];
            return value?.ToString();
        }
    }
}
