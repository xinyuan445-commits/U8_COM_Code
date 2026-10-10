using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Web.Http.Filters;
using GeneralInventoryCount;
using GeneralMaterialOutbound;
using MaterialOutboundApi.Models;

namespace MaterialOutboundApi.Infrastructure
{
    internal sealed class ApiExceptionFilter : ExceptionFilterAttribute
    {
        public override void OnException(HttpActionExecutedContext context)
        {
            var api = context.Exception as ApiException;
            var general = context.Exception as GeneralMaterialOutboundException;
            var inventoryCount = context.Exception as GeneralInventoryCountException;
            HttpStatusCode status = api?.StatusCode ??
                                    (general != null
                                        ? (HttpStatusCode)general.StatusCode
                                        : inventoryCount != null
                                            ? (HttpStatusCode)inventoryCount.StatusCode
                                            : HttpStatusCode.InternalServerError);
            string code = api?.Code ?? general?.Code ?? inventoryCount?.Code ??
                          "INTERNAL_ERROR";
            string message = api?.Message ?? general?.Message ??
                             inventoryCount?.Message ??
                             "服务器内部错误，请使用 requestId 查询日志。";

            WriteLog(context.Exception, api, status, code, message);
            context.Response = context.Request.CreateResponse(
                status,
                new ApiResponse<object>
                {
                    Success = false,
                    Code = code,
                    Message = message,
                    RequestId = RequestContext.Id
                });
        }

        private static void WriteLog(
            Exception exception,
            ApiException api,
            HttpStatusCode status,
            string code,
            string message)
        {
            try
            {
                string directory = Path.Combine(
                    AppRuntime.DataDirectory,
                    "Logs");
                Directory.CreateDirectory(directory);
                string prefix = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                                " requestId=" + RequestContext.Id +
                                " status=" + (int)status +
                                " code=" + code;
                string safeMessage = (message ?? string.Empty)
                    .Replace("\r", " ")
                    .Replace("\n", " ");
                // 4xx 是已知业务结果，只保留一行，避免“查不到工单”等正常
                // 操作把整段控制器堆栈写进日志。真正的 5xx 仍保留完整堆栈。
                string line = api != null && (int)status < 500
                    ? prefix + " message=" + safeMessage + Environment.NewLine
                    : prefix + Environment.NewLine + exception +
                      Environment.NewLine + new string('-', 80) +
                      Environment.NewLine;
                File.AppendAllText(
                    Path.Combine(
                        directory,
                        "api-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    line);
            }
            catch
            {
                // 日志失败不能覆盖原始业务异常。
            }
        }
    }
}
