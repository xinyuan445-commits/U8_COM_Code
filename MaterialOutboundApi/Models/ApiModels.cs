using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MaterialOutboundApi.Models
{
    public sealed class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string RequestId { get; set; }
        public T Data { get; set; }

        public static ApiResponse<T> Ok(T data, string message = "操作成功")
        {
            return new ApiResponse<T>
            {
                Success = true,
                Code = "OK",
                Message = message,
                RequestId = RequestContext.Id,
                Data = data
            };
        }
    }

    public sealed class WorkOrderLineDto
    {
        public int MoId { get; set; }
        public string WorkOrderCode { get; set; }
        public int MoDId { get; set; }
        public int? LineNumber { get; set; }
        public string ProductCode { get; set; }
        public string ProductName { get; set; }
        public decimal? OrderQuantity { get; set; }
        public string DepartmentCode { get; set; }
        public int? Status { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int MaterialCount { get; set; }
    }

    public sealed class TraceBackLabelLookupRequest
    {
        [Required]
        public string TraceBackCode { get; set; }
    }

    public sealed class TraceBackLabelBatchLookupRequest
    {
        [Required]
        public List<string> TraceBackCodes { get; set; }
    }

    public sealed class TraceBackLabelDto
    {
        public int TraceBackOnlyCodeId { get; set; }
        public string TraceBackCode { get; set; }
        public string Sn { get; set; }
        public string PdaOrderUuid { get; set; }
        public string PdaGoodsUuid { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public string BatchCode { get; set; }
        public decimal Quantity { get; set; }
        public decimal RemainingQuantity { get; set; }
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public string OrderNumber { get; set; }
        public string OrderType { get; set; }
    }

    public sealed class PreviewRequest
    {
        [Required]
        public string WorkOrderCode { get; set; }
        [Required]
        public List<int> SelectedMoDIds { get; set; }
        [Required]
        public string VoucherDate { get; set; }
    }

    public sealed class PreviewDto
    {
        public string PreviewToken { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string WorkOrderCode { get; set; }
        public DateTime VoucherDate { get; set; }
        public string DepartmentCode { get; set; }
        public string WarehouseCode { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public List<PreviewLineDto> Lines { get; set; }
    }

    public sealed class PreviewLineDto
    {
        public int AllocateId { get; set; }
        public int MoDId { get; set; }
        public int? WorkOrderLineNumber { get; set; }
        public string ProductCode { get; set; }
        public string ProductName { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public string DepartmentCode { get; set; }
        public string OpSeq { get; set; }
        public bool RequiresBatch { get; set; }
        public decimal PlannedQuantity { get; set; }
        public decimal IssuedQuantity { get; set; }
        public decimal RemainingQuantity { get; set; }
        public decimal SuggestedQuantity { get; set; }
    }

    public sealed class CreateMaterialOutboundRequest
    {
        [Required]
        public string PreviewToken { get; set; }
        public string VoucherCode { get; set; }
        [Required]
        public List<CreateMaterialOutboundLineRequest> Lines { get; set; }
    }

    public sealed class CreateMaterialOutboundLineRequest
    {
        public int AllocateId { get; set; }
        public decimal Quantity { get; set; }
        public string BatchCode { get; set; }
        [StringLength(80)]
        public string LabelSn { get; set; }
        [Range(typeof(decimal), "0.01", "10000")]
        public decimal? LengthCm { get; set; }
        [Range(typeof(decimal), "0.01", "10000")]
        public decimal? WidthCm { get; set; }
        [StringLength(255)]
        public string Remark { get; set; }
    }

    public sealed class MaterialOutboundCreatedDto
    {
        public string VoucherId { get; set; }
        public string VoucherCode { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsVerified { get; set; }
        public bool Replayed { get; set; }
    }

    public sealed class OperationRequest
    {
        [Required]
        public string OperationDate { get; set; }
    }

    public sealed class OperationResultDto
    {
        public string VoucherCode { get; set; }
        public string Action { get; set; }
        public bool Replayed { get; set; }
    }

    public sealed class MaterialOutboundDto
    {
        public int Id { get; set; }
        public string VoucherCode { get; set; }
        public DateTime VoucherDate { get; set; }
        public string WorkOrderCode { get; set; }
        public string WarehouseCode { get; set; }
        public string DepartmentCode { get; set; }
        public string Maker { get; set; }
        public string Auditor { get; set; }
        public DateTime? AuditDate { get; set; }
        public bool IsVerified { get; set; }
        public List<MaterialOutboundLineDto> Lines { get; set; }
    }

    public sealed class MaterialOutboundLineDto
    {
        public int AutoId { get; set; }
        public int? LineNumber { get; set; }
        public string MaterialCode { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public decimal Price { get; set; }
        public string BatchCode { get; set; }
        public string PositionCode { get; set; }
        public long? AllocateId { get; set; }
    }

    public sealed class HealthDto
    {
        public string Status { get; set; }
        public bool Configured { get; set; }
        public DateTime ServerTime { get; set; }
        public string Version { get; set; }
    }

    internal static class RequestContext
    {
        private const string Key = "MaterialOutboundApi.RequestId";

        public static string Id
        {
            get
            {
                object value = System.Web.HttpContext.Current?.Items[Key];
                return value == null ? Guid.NewGuid().ToString("N") : value.ToString();
            }
            set
            {
                if (System.Web.HttpContext.Current != null)
                {
                    System.Web.HttpContext.Current.Items[Key] = value;
                }
            }
        }
    }
}
