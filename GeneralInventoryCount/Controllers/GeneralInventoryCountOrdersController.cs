using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Description;
using GeneralInventoryCount.Models;

namespace GeneralInventoryCount.Controllers
{
    /// <summary>通用 U8 盘点单接口。</summary>
    [RoutePrefix("api/v1/general-inventory-count-orders")]
    public sealed class GeneralInventoryCountOrdersController : ApiController
    {
        /// <summary>只读校验盘点数据，不生成单据。</summary>
        [HttpPost]
        [Route("validate")]
        [ResponseType(typeof(InventoryCountApiResponse<GeneralInventoryCountValidationDto>))]
        public IHttpActionResult Validate(GeneralInventoryCountRequest request)
        {
            ValidateModel();
            GeneralInventoryCountValidationDto data =
                GeneralInventoryCountRuntime.Gateway.Validate(request);
            return Ok(Response(data, "校验通过"));
        }

        /// <summary>新增盘点单；默认只制单，不自动审核。</summary>
        [HttpPost]
        [Route("")]
        [ResponseType(typeof(InventoryCountApiResponse<GeneralInventoryCountCreatedDto>))]
        public async Task<IHttpActionResult> Create(
            GeneralInventoryCountRequest request)
        {
            ValidateModel();
            string key = Request.Headers.TryGetValues(
                    "Idempotency-Key",
                    out IEnumerable<string> values)
                ? values.FirstOrDefault()
                : null;
            GeneralInventoryCountCreatedDto data =
                await GeneralInventoryCountRuntime.Gateway.Create(request, key);
            return Content(
                HttpStatusCode.Created,
                Response(
                    data,
                    data.Replayed ? "返回原请求结果" : "盘点单新增成功"));
        }

        /// <summary>按盘点单号查询表头、明细及审核状态。</summary>
        [HttpGet]
        [Route("{voucherCode}")]
        [ResponseType(typeof(InventoryCountApiResponse<GeneralInventoryCountDto>))]
        public IHttpActionResult Get(string voucherCode)
        {
            GeneralInventoryCountDto data =
                GeneralInventoryCountRuntime.Gateway.Get(voucherCode);
            return Ok(Response(data, "查询成功"));
        }

        /// <summary>按行号修改未审核盘点单的实盘数量。</summary>
        [HttpPost]
        [Route("{voucherCode}/actual-quantities")]
        [ResponseType(typeof(InventoryCountApiResponse<InventoryCountQuantityUpdateDto>))]
        public async Task<IHttpActionResult> UpdateActualQuantities(
            string voucherCode,
            InventoryCountQuantityUpdateRequest request)
        {
            ValidateModel();
            InventoryCountQuantityUpdateDto data =
                await GeneralInventoryCountRuntime.Gateway
                    .UpdateActualQuantities(voucherCode, request);
            return Ok(Response(data, "盘点数量修改成功"));
        }

        /// <summary>按行号修改未审核盘点单的数量、批号、货位名称、原因和备注。</summary>
        [HttpPost]
        [Route("{voucherCode}/line-updates")]
        [ResponseType(typeof(InventoryCountApiResponse<InventoryCountQuantityUpdateDto>))]
        public async Task<IHttpActionResult> UpdateLines(
            string voucherCode,
            InventoryCountQuantityUpdateRequest request)
        {
            ValidateModel();
            var data = await GeneralInventoryCountRuntime.Gateway
                .UpdateLines(voucherCode, request);
            return Ok(Response(data, "盘点明细修改成功"));
        }

        private static InventoryCountApiResponse<T> Response<T>(
            T data,
            string message)
        {
            return new InventoryCountApiResponse<T>
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
            throw new GeneralInventoryCountException(
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
