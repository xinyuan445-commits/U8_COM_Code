using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MaterialOutboundApi.Models;

namespace MaterialOutboundApi.Infrastructure
{
    internal sealed class RequestIdHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string requestId = null;
            if (request.Headers.Contains("X-Request-Id"))
            {
                requestId = string.Join(
                    string.Empty,
                    request.Headers.GetValues("X-Request-Id"));
            }
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64)
            {
                requestId = Guid.NewGuid().ToString("N");
            }

            RequestContext.Id = requestId;
            HttpResponseMessage response =
                await base.SendAsync(request, cancellationToken);
            response.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
            return response;
        }
    }
}
