using System;
using System.Net;

namespace MaterialOutboundApi.Infrastructure
{
    internal sealed class ApiException : Exception
    {
        public ApiException(
            HttpStatusCode statusCode,
            string code,
            string message,
            Exception inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
            Code = code;
        }

        public HttpStatusCode StatusCode { get; }
        public string Code { get; }
    }
}
