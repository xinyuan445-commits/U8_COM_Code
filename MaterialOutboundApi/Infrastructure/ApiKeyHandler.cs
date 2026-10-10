using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MaterialOutboundApi.Models;
using MaterialOutboundApi.Services;

namespace MaterialOutboundApi.Infrastructure
{
    internal sealed class ApiKeyHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri.AbsolutePath.ToLowerInvariant();
            if (path.StartsWith("/swagger", StringComparison.Ordinal) ||
                path.StartsWith("/admin/", StringComparison.Ordinal) ||
                path.EndsWith("/api/v1/health", StringComparison.Ordinal))
            {
                return base.SendAsync(request, cancellationToken);
            }

            ApiSettings settings = AppRuntime.Settings?.Current;
            if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return Task.FromResult(request.CreateResponse(
                    HttpStatusCode.ServiceUnavailable,
                    new ApiResponse<object>
                    {
                        Success = false,
                        Code = "NOT_CONFIGURED",
                        Message = "API 尚未完成本机配置。",
                        RequestId = RequestContext.Id
                    }));
            }

            string supplied = request.Headers.TryGetValues(
                    "X-API-Key",
                    out var values)
                ? values.FirstOrDefault()
                : null;
            if (!SettingsStore.FixedTimeEquals(settings.ApiKey, supplied))
            {
                return Task.FromResult(request.CreateResponse(
                    HttpStatusCode.Unauthorized,
                    new ApiResponse<object>
                    {
                        Success = false,
                        Code = "INVALID_API_KEY",
                        Message = "X-API-Key 无效。",
                        RequestId = RequestContext.Id
                    }));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
