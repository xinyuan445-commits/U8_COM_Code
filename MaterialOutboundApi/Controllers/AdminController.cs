using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Description;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;

namespace MaterialOutboundApi.Controllers
{
    [ApiExplorerSettings(IgnoreApi = true)]
    [RoutePrefix("admin")]
    public sealed class AdminController : ApiController
    {
        [HttpGet]
        [Route("config")]
        public HttpResponseMessage Page()
        {
            RequireLocal();
            string path = HttpContext.Current.Server.MapPath(
                "~/Admin/index.html");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    File.ReadAllText(path),
                    System.Text.Encoding.UTF8,
                    "text/html")
            };
        }

        [HttpGet]
        [Route("api/config")]
        public IHttpActionResult GetConfig()
        {
            RequireLocal();
            RequireAdminWhenConfigured();
            return Ok(ApiResponse<SettingsView>.Ok(
                AppRuntime.Settings.GetView()));
        }

        [HttpPost]
        [Route("api/databases")]
        public IHttpActionResult GetDatabases(SettingsInput input)
        {
            RequireLocal();
            RequireAdminWhenConfigured();
            ValidateSqlLoginInput(input);
            ApiSettings settings = AppRuntime.Settings.Merge(input);
            List<string> databases =
                AppRuntime.MaterialOutbound.GetAccountDatabases(settings);
            return Ok(ApiResponse<object>.Ok(
                new { databases },
                databases.Count == 0
                    ? "未找到在线的 UFDATA_* 账套数据库"
                    : "账套数据库读取成功"));
        }

        [HttpPost]
        [Route("api/test-sql")]
        public IHttpActionResult TestSql(SettingsInput input)
        {
            RequireLocal();
            RequireAdminWhenConfigured();
            ValidateInput(input);
            ApiSettings settings = AppRuntime.Settings.Merge(input);
            AppRuntime.MaterialOutbound.TestSql(settings);
            AppRuntime.ShapeConversion.ValidateProfiles(settings);
            return Ok(ApiResponse<object>.Ok(
                new { connected = true },
                "SQL Server 连接及 momlist_yx 视图检查成功"));
        }

        [HttpPost]
        [Route("api/test-u8")]
        public async Task<IHttpActionResult> TestU8(SettingsInput input)
        {
            RequireLocal();
            RequireAdminWhenConfigured();
            ValidateInput(input);
            ApiSettings settings = AppRuntime.Settings.Merge(input);
            DateTime date = ParseTestDate(input.TestDate, settings.U8Year);
            bool connected = await AppRuntime.MaterialOutbound.TestU8(
                settings,
                date);
            return Ok(ApiResponse<object>.Ok(
                new { connected },
                "U8 登录、库存模块初始化和注销成功"));
        }

        [HttpPost]
        [Route("api/save")]
        public async Task<IHttpActionResult> Save(SettingsInput input)
        {
            RequireLocal();
            RequireAdminWhenConfigured();
            ValidateInput(input);
            ApiSettings settings = AppRuntime.Settings.Merge(input);
            AppRuntime.MaterialOutbound.TestSql(settings);
            AppRuntime.ShapeConversion.ValidateProfiles(settings);
            DateTime date = ParseTestDate(input.TestDate, settings.U8Year);
            await AppRuntime.MaterialOutbound.TestU8(settings, date);
            string apiKey = AppRuntime.Settings.Save(
                settings,
                input.AdminPassword);
            return Ok(ApiResponse<object>.Ok(
                new
                {
                    saved = true,
                    apiKey,
                    revision = settings.Revision
                },
                "配置已加密保存并立即生效"));
        }

        [HttpPost]
        [Route("api/rotate-key")]
        public IHttpActionResult RotateKey()
        {
            RequireLocal();
            RequireAdmin();
            string key = AppRuntime.Settings.RotateApiKey();
            return Ok(ApiResponse<object>.Ok(
                new { apiKey = key },
                "API Key 已轮换，旧 Key 立即失效"));
        }

        private void RequireAdminWhenConfigured()
        {
            if (AppRuntime.Settings.IsConfigured)
            {
                RequireAdmin();
            }
        }

        private void RequireAdmin()
        {
            string password = Request.Headers.Contains("X-Admin-Password")
                ? string.Join(
                    string.Empty,
                    Request.Headers.GetValues("X-Admin-Password"))
                : null;
            if (!AppRuntime.Settings.VerifyAdmin(password))
            {
                throw new ApiException(
                    HttpStatusCode.Unauthorized,
                    "INVALID_ADMIN_PASSWORD",
                    "配置管理密码不正确。");
            }
        }

        private static void RequireLocal()
        {
            string address = HttpContext.Current?.Request.UserHostAddress;
            if (!IPAddress.TryParse(address, out IPAddress ip) ||
                !IPAddress.IsLoopback(ip))
            {
                throw new ApiException(
                    HttpStatusCode.Forbidden,
                    "LOCAL_ONLY",
                    "配置页面只允许在 API 服务器本机访问。");
            }
        }

        private static DateTime ParseTestDate(
            string value,
            string u8Year)
        {
            if (!DateTime.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime date))
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    "INVALID_TEST_DATE",
                    "U8 测试日期必须使用 yyyy-MM-dd 格式。");
            }
            if (!int.TryParse(u8Year, out int year) || date.Year != year)
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    "TEST_DATE_YEAR_MISMATCH",
                    "U8 测试日期年份必须与登录年度一致。");
            }
            return date;
        }

        private void ValidateInput(SettingsInput input)
        {
            if (input == null || !ModelState.IsValid)
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
                    HttpStatusCode.BadRequest,
                    "CONFIG_VALIDATION_ERROR",
                    string.IsNullOrWhiteSpace(message)
                        ? "配置参数不完整或格式不正确。"
                        : message);
            }
        }

        private static void ValidateSqlLoginInput(SettingsInput input)
        {
            if (input == null ||
                string.IsNullOrWhiteSpace(input.SqlServer) ||
                string.IsNullOrWhiteSpace(input.SqlUser))
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    "SQL_LOGIN_REQUIRED",
                    "请先填写 SQL Server 和用户名。");
            }
        }
    }
}
