using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using MaterialOutboundApi.Examples;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;
using Swashbuckle.Examples;
using Swashbuckle.Swagger.Annotations;

namespace MaterialOutboundApi.Controllers
{
    [RoutePrefix("api/v1")]
    public sealed class MaterialOutboundOrdersController : ApiController
    {
        /// <summary>生成材料出库预览，返回计划量、已出库量和剩余量。</summary>
        [HttpPost]
        [Route("material-outbound/previews")]
        [ResponseType(typeof(ApiResponse<PreviewDto>))]
        [SwaggerRequestExample(
            typeof(PreviewRequest),
            typeof(PreviewRequestExample))]
        [SwaggerResponseExample(
            System.Net.HttpStatusCode.OK,
            typeof(PreviewResponseExample))]
        public IHttpActionResult Preview(PreviewRequest request)
        {
            ValidateModel();
            PreviewDto data = AppRuntime.MaterialOutbound.Preview(request);
            return Ok(ApiResponse<PreviewDto>.Ok(data, "预览生成成功"));
        }

        /// <summary>根据预览结果新增材料出库单。</summary>
        [HttpPost]
        [Route("material-outbound-orders")]
        [ResponseType(typeof(ApiResponse<MaterialOutboundCreatedDto>))]
        [SwaggerRequestExample(
            typeof(CreateMaterialOutboundRequest),
            typeof(CreateRequestExample))]
        [SwaggerRequestHeader(
            "Idempotency-Key",
            isRequired: true,
            description: "8 到 64 个字符；重试必须沿用同一个值。")]
        [SwaggerResponse(
            System.Net.HttpStatusCode.Created,
            "材料出库单新增成功",
            typeof(ApiResponse<MaterialOutboundCreatedDto>))]
        [SwaggerResponseRemoveDefaults]
        [SwaggerResponseExample(
            System.Net.HttpStatusCode.Created,
            typeof(CreateResponseExample))]
        public async Task<IHttpActionResult> Create(
            CreateMaterialOutboundRequest request)
        {
            ValidateModel();
            string key = GetIdempotencyKey();
            MaterialOutboundCreatedDto data =
                await AppRuntime.MaterialOutbound.Create(request, key);
            return Content(
                System.Net.HttpStatusCode.Created,
                ApiResponse<MaterialOutboundCreatedDto>.Ok(
                    data,
                    data.Replayed ? "返回原请求结果" : "材料出库单新增成功"));
        }

        /// <summary>按材料出库单号查询表头、表体及审核状态。</summary>
        [HttpGet]
        [Route("material-outbound-orders/{voucherCode}")]
        [ResponseType(typeof(ApiResponse<MaterialOutboundDto>))]
        [SwaggerResponseExample(
            System.Net.HttpStatusCode.OK,
            typeof(VoucherResponseExample))]
        public IHttpActionResult Get(string voucherCode)
        {
            MaterialOutboundDto data =
                AppRuntime.MaterialOutbound.Query(voucherCode);
            return Ok(ApiResponse<MaterialOutboundDto>.Ok(data));
        }

        /// <summary>审核材料出库单。</summary>
        [HttpPost]
        [Route("material-outbound-orders/{voucherCode}/verify")]
        [ResponseType(typeof(ApiResponse<OperationResultDto>))]
        [SwaggerRequestExample(
            typeof(OperationRequest),
            typeof(OperationRequestExample))]
        [SwaggerRequestHeader(
            "Idempotency-Key",
            isRequired: true,
            description: "8 到 64 个字符；重试必须沿用同一个值。")]
        [SwaggerResponseExample(
            System.Net.HttpStatusCode.OK,
            typeof(VerifyResponseExample))]
        public async Task<IHttpActionResult> Verify(
            string voucherCode,
            OperationRequest request)
        {
            ValidateModel();
            OperationResultDto data = await AppRuntime.MaterialOutbound.Verify(
                voucherCode,
                request,
                GetIdempotencyKey());
            return Ok(ApiResponse<OperationResultDto>.Ok(data, "审核成功"));
        }

        /// <summary>弃审材料出库单。</summary>
        [HttpPost]
        [Route("material-outbound-orders/{voucherCode}/unverify")]
        [ResponseType(typeof(ApiResponse<OperationResultDto>))]
        [SwaggerRequestExample(
            typeof(OperationRequest),
            typeof(OperationRequestExample))]
        [SwaggerRequestHeader(
            "Idempotency-Key",
            isRequired: true,
            description: "8 到 64 个字符；重试必须沿用同一个值。")]
        [SwaggerResponseExample(
            System.Net.HttpStatusCode.OK,
            typeof(UnverifyResponseExample))]
        public async Task<IHttpActionResult> Unverify(
            string voucherCode,
            OperationRequest request)
        {
            ValidateModel();
            OperationResultDto data = await AppRuntime.MaterialOutbound.Unverify(
                voucherCode,
                request,
                GetIdempotencyKey());
            return Ok(ApiResponse<OperationResultDto>.Ok(data, "弃审成功"));
        }

        private string GetIdempotencyKey()
        {
            return Request.Headers.TryGetValues(
                    "Idempotency-Key",
                    out var values)
                ? values.FirstOrDefault()
                : null;
        }

        private void ValidateModel()
        {
            if (!ModelState.IsValid)
            {
                string message = string.Join(
                    "；",
                    ModelState.Values
                        .SelectMany(x => x.Errors)
                        .Select(x => string.IsNullOrWhiteSpace(x.ErrorMessage)
                            ? x.Exception?.Message
                            : x.ErrorMessage)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                throw new ApiException(
                    System.Net.HttpStatusCode.BadRequest,
                    "VALIDATION_ERROR",
                    string.IsNullOrWhiteSpace(message)
                        ? "请求参数不合法。"
                        : message);
            }
        }
    }
}
