using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace GeneralInventoryCount.Models
{
    public sealed class GeneralInventoryCountRequest
    {
        [Required, StringLength(64, MinimumLength = 1)]
        public string ExternalRequestNo { get; set; }

        [Required, StringLength(10)]
        public string VoucherDate { get; set; }

        [StringLength(10)]
        public string CountDate { get; set; }

        [StringLength(30)]
        public string VoucherCode { get; set; }

        [Required, StringLength(10)]
        public string WarehouseCode { get; set; }

        [StringLength(12)]
        public string DepartmentCode { get; set; }

        [StringLength(5)]
        public string InboundRdCode { get; set; }

        [StringLength(5)]
        public string OutboundRdCode { get; set; }

        [StringLength(20)]
        public string PersonCode { get; set; }

        [StringLength(20)]
        public string Maker { get; set; }

        [StringLength(20)]
        public string Source { get; set; }

        [StringLength(60)]
        public string CountBatch { get; set; }

        [StringLength(10)]
        public string PositionCode { get; set; }

        [StringLength(40)]
        public string CountType { get; set; }

        [StringLength(20)]
        public string AccountingPeriod { get; set; }

        [StringLength(255)]
        public string Memo { get; set; }

        [Range(1, int.MaxValue)]
        public int? VoucherTemplateId { get; set; }

        public bool? AutoVerify { get; set; }
        public InventoryCountHeadDefinedFields DefinedFields { get; set; }

        [Required]
        public List<GeneralInventoryCountLineRequest> Lines { get; set; }
    }

    public sealed class GeneralInventoryCountLineRequest
    {
        [Range(1, int.MaxValue)]
        public int? LineNo { get; set; }

        [Required, StringLength(60)]
        public string InventoryCode { get; set; }

        [Required]
        public decimal? ActualQuantity { get; set; }

        public decimal? BookQuantity { get; set; }
        public decimal? ActualNumber { get; set; }
        public decimal? BookNumber { get; set; }

        [StringLength(10)]
        public string AuxiliaryUnitCode { get; set; }

        [StringLength(60)]
        public string BatchCode { get; set; }

        [StringLength(10)]
        public string PositionCode { get; set; }

        public decimal? AdjustInQuantity { get; set; }
        public decimal? AdjustInNumber { get; set; }
        public decimal? AdjustOutQuantity { get; set; }
        public decimal? AdjustOutNumber { get; set; }
        public decimal? AllowedWasteRate { get; set; }
        public decimal? ActualWasteRate { get; set; }
        public decimal? ExchangeRate { get; set; }
        public decimal? PlanUnitPrice { get; set; }
        public decimal? BookAmount { get; set; }
        public decimal? ActualAmount { get; set; }
        public decimal? DifferenceAmount { get; set; }

        [StringLength(120)]
        public string Reason { get; set; }

        [StringLength(255)]
        public string Remark { get; set; }

        [StringLength(60)] public string VendorCode { get; set; }
        [StringLength(30)] public string InVoucherCode { get; set; }
        [StringLength(10)] public string InVoucherType { get; set; }
        public int? MassDate { get; set; }
        [StringLength(10)] public string MassUnit { get; set; }
        public DateTime? ManufactureDate { get; set; }
        public DateTime? InvalidDate { get; set; }
        public DateTime? ExpirationDate { get; set; }
        [StringLength(60)] public string ExpirationDateText { get; set; }
        public int? ExpirationDateCalculation { get; set; }
        public long? RecordDetailId { get; set; }

        [StringLength(20)] public string ItemClassCode { get; set; }
        [StringLength(60)] public string ItemCode { get; set; }
        public long? SalesOrderDetailId { get; set; }
        public int? SalesOrderType { get; set; }

        public InventoryCountFreeFields FreeFields { get; set; }
        public InventoryCountBodyDefinedFields DefinedFields { get; set; }
        public InventoryCountBatchPropertyFields BatchProperties { get; set; }
    }

    public sealed class InventoryCountHeadDefinedFields
    {
        [StringLength(20)] public string Define1 { get; set; }
        [StringLength(20)] public string Define2 { get; set; }
        [StringLength(20)] public string Define3 { get; set; }
        public DateTime? Define4 { get; set; }
        public int? Define5 { get; set; }
        public DateTime? Define6 { get; set; }
        public decimal? Define7 { get; set; }
        [StringLength(4)] public string Define8 { get; set; }
        [StringLength(8)] public string Define9 { get; set; }
        [StringLength(60)] public string Define10 { get; set; }
        [StringLength(120)] public string Define11 { get; set; }
        [StringLength(120)] public string Define12 { get; set; }
        [StringLength(120)] public string Define13 { get; set; }
        [StringLength(120)] public string Define14 { get; set; }
        public int? Define15 { get; set; }
        public decimal? Define16 { get; set; }
    }

    public sealed class InventoryCountFreeFields
    {
        [StringLength(60)] public string Free1 { get; set; }
        [StringLength(60)] public string Free2 { get; set; }
        [StringLength(60)] public string Free3 { get; set; }
        [StringLength(60)] public string Free4 { get; set; }
        [StringLength(60)] public string Free5 { get; set; }
        [StringLength(60)] public string Free6 { get; set; }
        [StringLength(60)] public string Free7 { get; set; }
        [StringLength(60)] public string Free8 { get; set; }
        [StringLength(60)] public string Free9 { get; set; }
        [StringLength(60)] public string Free10 { get; set; }
    }

    public sealed class InventoryCountBodyDefinedFields
    {
        [StringLength(60)] public string Define22 { get; set; }
        [StringLength(60)] public string Define23 { get; set; }
        [StringLength(60)] public string Define24 { get; set; }
        [StringLength(60)] public string Define25 { get; set; }
        public decimal? Define26 { get; set; }
        public decimal? Define27 { get; set; }
        [StringLength(120)] public string Define28 { get; set; }
        [StringLength(120)] public string Define29 { get; set; }
        [StringLength(120)] public string Define30 { get; set; }
        [StringLength(120)] public string Define31 { get; set; }
        [StringLength(120)] public string Define32 { get; set; }
        [StringLength(120)] public string Define33 { get; set; }
        public int? Define34 { get; set; }
        public int? Define35 { get; set; }
        public DateTime? Define36 { get; set; }
        public DateTime? Define37 { get; set; }
    }

    public sealed class InventoryCountBatchPropertyFields
    {
        public decimal? Property1 { get; set; }
        public decimal? Property2 { get; set; }
        public decimal? Property3 { get; set; }
        public decimal? Property4 { get; set; }
        public decimal? Property5 { get; set; }
        [StringLength(120)] public string Property6 { get; set; }
        [StringLength(120)] public string Property7 { get; set; }
        [StringLength(120)] public string Property8 { get; set; }
        [StringLength(120)] public string Property9 { get; set; }
        public DateTime? Property10 { get; set; }
    }

    public sealed class GeneralInventoryCountValidationDto
    {
        public bool Valid { get; set; }
        public string U8Database { get; set; }
        public DateTime VoucherDate { get; set; }
        public DateTime CountDate { get; set; }
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public bool UsesPosition { get; set; }
        public string InboundRdCode { get; set; }
        public string OutboundRdCode { get; set; }
        public List<GeneralInventoryCountValidationLineDto> Lines { get; set; }
        public List<string> Warnings { get; set; }
    }

    public sealed class GeneralInventoryCountValidationLineDto
    {
        public int LineNo { get; set; }
        public string InventoryCode { get; set; }
        public string InventoryName { get; set; }
        public bool RequiresBatch { get; set; }
        public string BatchCode { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public bool RequiresExpirationDate { get; set; }
        public int? MassDate { get; set; }
        public string MassUnit { get; set; }
        public DateTime? ManufactureDate { get; set; }
        public DateTime? InvalidDate { get; set; }
        public int? ExpirationDateCalculation { get; set; }
        public DateTime? ExpirationDate { get; set; }
        public decimal CurrentBookQuantity { get; set; }
        public decimal BookQuantity { get; set; }
        public decimal ActualQuantity { get; set; }
        public decimal DifferenceQuantity { get; set; }
        public string AuxiliaryUnitCode { get; set; }
        public decimal? ExchangeRate { get; set; }
        public decimal? CurrentBookNumber { get; set; }
        public decimal? BookNumber { get; set; }
        public decimal? ActualNumber { get; set; }
    }

    public sealed class GeneralInventoryCountCreatedDto
    {
        public string ExternalRequestNo { get; set; }
        public string VoucherId { get; set; }
        public string VoucherCode { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsVerified { get; set; }
        public bool Replayed { get; set; }
        public string U8Database { get; set; }
    }

    public sealed class GeneralInventoryCountDto
    {
        public int Id { get; set; }
        public string VoucherCode { get; set; }
        public DateTime VoucherDate { get; set; }
        public DateTime? CountDate { get; set; }
        public string WarehouseCode { get; set; }
        public string DepartmentCode { get; set; }
        public string InboundRdCode { get; set; }
        public string OutboundRdCode { get; set; }
        public string Maker { get; set; }
        public string Auditor { get; set; }
        public DateTime? AuditDate { get; set; }
        public string CountType { get; set; }
        public string Memo { get; set; }
        public bool IsVerified { get; set; }
        public string U8Database { get; set; }
        public List<GeneralInventoryCountLineDto> Lines { get; set; }
    }

    public sealed class GeneralInventoryCountLineDto
    {
        public int AutoId { get; set; }
        public int? LineNo { get; set; }
        public string InventoryCode { get; set; }
        public string InventoryName { get; set; }
        public string Specification { get; set; }
        public string UnitName { get; set; }
        public string BatchCode { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public decimal BookQuantity { get; set; }
        public decimal ActualQuantity { get; set; }
        public decimal DifferenceQuantity { get; set; }
        public string AuxiliaryUnitCode { get; set; }
        public decimal? ExchangeRate { get; set; }
        public decimal? BookNumber { get; set; }
        public decimal? ActualNumber { get; set; }
        public decimal? DifferenceNumber { get; set; }
        public decimal AdjustInQuantity { get; set; }
        public decimal AdjustOutQuantity { get; set; }
        public string Reason { get; set; }
        public string Remark { get; set; }
        public string LineVersion { get; set; }
    }

    public sealed class InventoryCountQuantityUpdateRequest
    {
        [Required]
        public List<InventoryCountQuantityUpdateLineRequest> Lines { get; set; }
    }

    public sealed class InventoryCountQuantityUpdateLineRequest
    {
        [Range(1, int.MaxValue)]
        public int LineNo { get; set; }

        [Required]
        public decimal? ActualQuantity { get; set; }

        public decimal? ExpectedActualQuantity { get; set; }

        [StringLength(100)]
        public string ExpectedLineVersion { get; set; }

        public bool UpdateBatchCode { get; set; }
        [StringLength(60)] public string BatchCode { get; set; }
        public bool UpdatePosition { get; set; }
        [StringLength(100)] public string PositionName { get; set; }
        public bool UpdateReason { get; set; }
        [StringLength(120)] public string Reason { get; set; }
        public bool UpdateRemark { get; set; }
        [StringLength(255)] public string Remark { get; set; }
    }

    public sealed class InventoryCountQuantityUpdateDto
    {
        public string VoucherCode { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsVerified { get; set; }
        public string U8Database { get; set; }
        public List<InventoryCountQuantityUpdatedLineDto> Lines { get; set; }
    }

    public sealed class InventoryCountQuantityUpdatedLineDto
    {
        public int LineNo { get; set; }
        public string InventoryCode { get; set; }
        public string InventoryName { get; set; }
        public string BatchCode { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public decimal BookQuantity { get; set; }
        public decimal PreviousActualQuantity { get; set; }
        public decimal ActualQuantity { get; set; }
        public decimal DifferenceQuantity { get; set; }
        public decimal? PreviousActualNumber { get; set; }
        public decimal? ActualNumber { get; set; }
        public decimal? DifferenceNumber { get; set; }
        public string PreviousBatchCode { get; set; }
        public string PreviousPositionName { get; set; }
        public string PreviousReason { get; set; }
        public string Reason { get; set; }
        public string PreviousRemark { get; set; }
        public string Remark { get; set; }
    }

    public sealed class InventoryCountApiResponse<T>
    {
        public bool Success { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string RequestId { get; set; }
        public T Data { get; set; }
    }
}
