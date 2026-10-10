using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;

namespace MaterialOutboundApi.Controllers
{
    [RoutePrefix("api/v1/shape-conversions")]
    public sealed class ShapeConversionsController : ApiController
    {
        [HttpPost]
        [Route("previews")]
        [ResponseType(typeof(ApiResponse<ShapeConversionPreviewDto>))]
        public IHttpActionResult Preview(ShapeConversionPreviewRequest request)
        {
            ValidateModel();
            return Ok(ApiResponse<ShapeConversionPreviewDto>.Ok(AppRuntime.ShapeConversion.Preview(request), "预览生成成功"));
        }

        [HttpPost]
        [Route("")]
        [ResponseType(typeof(ApiResponse<ShapeConversionCreatedDto>))]
        public async Task<IHttpActionResult> Create(CreateShapeConversionRequest request)
        {
            ValidateModel();
            string key = Request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.FirstOrDefault() : null;
            ShapeConversionCreatedDto data = await AppRuntime.ShapeConversion.Create(request, key);
            bool complete = data.ShapeConversion?.IsVerified == true && data.OutboundVoucher?.IsVerified == true && data.InboundVoucher?.IsVerified == true;
            bool createdUnverified = data.AutoVerify == false && data.ShapeConversion != null && data.ShapeConversion.Id > 0 && !data.ShapeConversion.IsVerified;
            string message = complete
                ? "形态转换及派生单已全部审核"
                : createdUnverified
                    ? "形态转换单已创建，按请求保留未审核状态"
                    : "已保留当前 U8 单据状态，请使用同一请求继续处理";
            return Ok(ApiResponse<ShapeConversionCreatedDto>.Ok(data, message));
        }

        [HttpGet]
        [Route("{voucherCode}")]
        [ResponseType(typeof(ApiResponse<ShapeConversionCreatedDto>))]
        public IHttpActionResult Get(string voucherCode) => Ok(ApiResponse<ShapeConversionCreatedDto>.Ok(AppRuntime.ShapeConversion.Query(voucherCode)));

        private void ValidateModel()
        {
            if (!ModelState.IsValid)
            {
                string message = string.Join("；", ModelState.Values.SelectMany(x => x.Errors).Select(x => string.IsNullOrWhiteSpace(x.ErrorMessage) ? x.Exception?.Message : x.ErrorMessage).Where(x => !string.IsNullOrWhiteSpace(x)));
                throw new ApiException(System.Net.HttpStatusCode.BadRequest, "VALIDATION_ERROR", string.IsNullOrWhiteSpace(message) ? "请求参数不合法。" : message);
            }
        }
    }
}
