using System;
using System.Threading.Tasks;
using GeneralInventoryCount.Models;

namespace GeneralInventoryCount
{
    public interface IGeneralInventoryCountGateway
    {
        GeneralInventoryCountValidationDto Validate(
            GeneralInventoryCountRequest request);

        Task<GeneralInventoryCountCreatedDto> Create(
            GeneralInventoryCountRequest request,
            string idempotencyKey);

        GeneralInventoryCountDto Get(string voucherCode);

        Task<InventoryCountQuantityUpdateDto> UpdateActualQuantities(
            string voucherCode,
            InventoryCountQuantityUpdateRequest request);

        Task<InventoryCountQuantityUpdateDto> UpdateLines(
            string voucherCode,
            InventoryCountQuantityUpdateRequest request);
    }

    public static class GeneralInventoryCountRuntime
    {
        private static readonly object Sync = new object();
        private static IGeneralInventoryCountGateway _gateway;

        public static IGeneralInventoryCountGateway Gateway
        {
            get
            {
                lock (Sync)
                {
                    if (_gateway == null)
                    {
                        throw new GeneralInventoryCountException(
                            503,
                            "INVENTORY_COUNT_API_NOT_INITIALIZED",
                            "通用盘点单接口尚未初始化。");
                    }
                    return _gateway;
                }
            }
        }

        public static void Initialize(IGeneralInventoryCountGateway gateway)
        {
            if (gateway == null) throw new ArgumentNullException(nameof(gateway));
            lock (Sync) _gateway = gateway;
        }

        public static void Reset()
        {
            lock (Sync) _gateway = null;
        }
    }

    public sealed class GeneralInventoryCountException : Exception
    {
        public GeneralInventoryCountException(
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
