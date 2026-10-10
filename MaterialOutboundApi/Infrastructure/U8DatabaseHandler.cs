using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MaterialOutboundApi.Models;

namespace MaterialOutboundApi.Infrastructure
{
    /// <summary>
    /// Lets FastAPI select the U8 account database from its single
    /// U8_DATABASE setting. Missing headers keep the IIS admin setting for
    /// backwards compatibility with local Swagger diagnostics.
    /// </summary>
    internal sealed class U8DatabaseHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string supplied = request.Headers.TryGetValues(
                    "X-U8-Database",
                    out var values)
                ? values.FirstOrDefault()
                : null;
            string database = U8DatabaseContext.Normalize(supplied);
            if (!string.IsNullOrWhiteSpace(supplied) && database == null)
            {
                return request.CreateResponse(
                    HttpStatusCode.BadRequest,
                    new ApiResponse<object>
                    {
                        Success = false,
                        Code = "U8_DATABASE_NOT_ALLOWED",
                        Message = "X-U8-Database 只允许 UFDATA_008_2021、UFDATA_888_2021 或 UFDATA_998_2021。",
                        RequestId = RequestContext.Id
                    });
            }
            if (string.Equals(
                    database,
                    "UFDATA_888_2021",
                    StringComparison.Ordinal) &&
                !IsInventoryCountRequest(request))
            {
                return request.CreateResponse(
                    HttpStatusCode.BadRequest,
                    new ApiResponse<object>
                    {
                        Success = false,
                        Code = "U8_DATABASE_NOT_ALLOWED_FOR_ROUTE",
                        Message = "888 账套只允许用于盘点单接口。",
                        RequestId = RequestContext.Id
                    });
            }

            string previous = U8DatabaseContext.Current;
            U8DatabaseContext.Current = database;
            try
            {
                HttpResponseMessage response =
                    await base.SendAsync(request, cancellationToken);
                if (database != null)
                {
                    response.Headers.TryAddWithoutValidation(
                        "X-U8-Database",
                        database);
                }
                return response;
            }
            finally
            {
                U8DatabaseContext.Current = previous;
            }
        }

        private static bool IsInventoryCountRequest(HttpRequestMessage request)
        {
            const string routePrefix =
                "/api/v1/general-inventory-count-orders";
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!path.StartsWith(
                    routePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return path.Length == routePrefix.Length ||
                   path[routePrefix.Length] == '/';
        }
    }
}
