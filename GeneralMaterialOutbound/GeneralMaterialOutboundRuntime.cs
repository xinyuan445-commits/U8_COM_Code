using System;
using System.Threading.Tasks;
using GeneralMaterialOutbound.Models;

namespace GeneralMaterialOutbound
{
    public interface IGeneralMaterialOutboundGateway
    {
        GeneralMaterialOutboundValidationDto Validate(
            GeneralMaterialOutboundRequest request);

        Task<GeneralMaterialOutboundCreatedDto> Create(
            GeneralMaterialOutboundRequest request,
            string idempotencyKey);

        GeneralMaterialOutboundDto Get(string voucherCode);
    }

    public static class GeneralMaterialOutboundRuntime
    {
        private static readonly object Sync = new object();
        private static IGeneralMaterialOutboundGateway _gateway;

        public static IGeneralMaterialOutboundGateway Gateway
        {
            get
            {
                lock (Sync)
                {
                    if (_gateway == null)
                    {
                        throw new GeneralMaterialOutboundException(
                            503,
                            "GENERAL_API_NOT_INITIALIZED",
                            "通用材料出库接口尚未初始化。");
                    }
                    return _gateway;
                }
            }
        }

        public static void Initialize(IGeneralMaterialOutboundGateway gateway)
        {
            if (gateway == null) throw new ArgumentNullException(nameof(gateway));
            lock (Sync)
            {
                _gateway = gateway;
            }
        }

        public static void Reset()
        {
            lock (Sync)
            {
                _gateway = null;
            }
        }
    }

    public sealed class GeneralMaterialOutboundException : Exception
    {
        public GeneralMaterialOutboundException(
            int statusCode,
            string code,
            string message,
            Exception innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            Code = code;
        }

        public int StatusCode { get; }
        public string Code { get; }
    }
}
