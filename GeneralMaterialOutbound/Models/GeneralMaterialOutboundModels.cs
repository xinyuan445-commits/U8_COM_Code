using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace GeneralMaterialOutbound.Models
{
    public static class GeneralIssueModes
    {
        public const string Direct = "direct";
        public const string ProductionOrder = "productionOrder";
    }

    public sealed class GeneralMaterialOutboundRequest
    {
        [Required, StringLength(64, MinimumLength = 1)]
        public string ExternalRequestNo { get; set; }

        [Required, StringLength(32)]
        public string IssueMode { get; set; }

        [Required, StringLength(10)]
        public string VoucherDate { get; set; }

        [StringLength(30)]
        public string VoucherCode { get; set; }

        [Required, StringLength(10)]
        public string WarehouseCode { get; set; }

        [Required, StringLength(12)]
        public string DepartmentCode { get; set; }

        [Required, StringLength(5)]
        public string RdCode { get; set; }

        [StringLength(20)]
        public string PersonCode { get; set; }

        [StringLength(20)]
        public string Maker { get; set; }

        [StringLength(60)]
        public string ProductionBatch { get; set; }

        [StringLength(50)]
        public string Source { get; set; }

        [StringLength(12)]
        public string BusinessType { get; set; }

        [StringLength(30)]
        public string BusinessCode { get; set; }

        [StringLength(255)]
        public string Memo { get; set; }

        [StringLength(60)]
        public string ProductCode { get; set; }

        [StringLength(30)]
        public string ProductionOrderCode { get; set; }

        public int? ProductionOrderId { get; set; }

        [Range(1, int.MaxValue)]
        public int? VoucherTemplateId { get; set; }

        public bool? AutoVerify { get; set; }

        public GeneralHeadDefinedFields DefinedFields { get; set; }

        [Required]
        public List<GeneralMaterialOutboundLineRequest> Lines { get; set; }
    }

    public sealed class GeneralHeadDefinedFields
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

    public sealed class GeneralMaterialOutboundLineRequest
    {
        [Range(1, int.MaxValue)]
        public int? LineNo { get; set; }

        [StringLength(60)]
        public string MaterialCode { get; set; }

        [Range(typeof(decimal), "0.000001", "999999999999")]
        public decimal Quantity { get; set; }

        public decimal? Number { get; set; }
        public decimal? InQuantity { get; set; }
        public decimal? InNumber { get; set; }
        public decimal? UnitCost { get; set; }
        public decimal? Price { get; set; }
        public decimal? ExchangeRate { get; set; }

        [StringLength(60)]
        public string BatchCode { get; set; }

        [StringLength(10)]
        public string PositionCode { get; set; }

        [StringLength(60)]
        public string ProductCode { get; set; }

        public int? ProductionOrderDetailId { get; set; }
        public long? AllocationId { get; set; }

        [StringLength(30)]
        public string ProductionOrderCode { get; set; }

        public int? ProductionOrderSequence { get; set; }

        [StringLength(50)]
        public string OperationSequence { get; set; }

        [StringLength(255)]
        public string OperationDescription { get; set; }

        [StringLength(50)]
        public string WorkCenterCode { get; set; }

        public int? SalesOrderType { get; set; }
        public int? OrderType { get; set; }
        public int? OrderDetailId { get; set; }

        [StringLength(30)]
        public string OrderCode { get; set; }

        public int? OrderSequence { get; set; }
        public long? PeSoDetailId { get; set; }
        public int? PeSoType { get; set; }

        [StringLength(40)]
        public string PeSoCode { get; set; }

        public int? PeSoSequence { get; set; }

        [StringLength(255)]
        public string Remark { get; set; }

        public GeneralBodyFreeFields FreeFields { get; set; }
        public GeneralBodyDefinedFields DefinedFields { get; set; }
        public GeneralBatchPropertyFields BatchProperties { get; set; }
    }

    public sealed class GeneralBodyFreeFields
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

    public sealed class GeneralBodyDefinedFields
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

    public sealed class GeneralBatchPropertyFields
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

    public sealed class GeneralMaterialOutboundValidationDto
    {
        public bool Valid { get; set; }
        public string U8Database { get; set; }
        public string IssueMode { get; set; }
        public DateTime VoucherDate { get; set; }
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public string DepartmentCode { get; set; }
        public string RdCode { get; set; }
        public string ProductionOrderCode { get; set; }
        public int? ProductionOrderId { get; set; }
        public string ProductCode { get; set; }
        public List<GeneralMaterialOutboundValidationLineDto> Lines { get; set; }
        public List<string> Warnings { get; set; }
    }

    public sealed class GeneralMaterialOutboundValidationLineDto
    {
        public int LineNo { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public decimal Quantity { get; set; }
        public string BatchCode { get; set; }
        public bool RequiresBatch { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public decimal AvailableQuantity { get; set; }
        public decimal UnitCost { get; set; }
        public decimal Price { get; set; }
        public int? ProductionOrderDetailId { get; set; }
        public long? AllocationId { get; set; }
        public string ProductionOrderCode { get; set; }
        public int? ProductionOrderSequence { get; set; }
        public string ProductCode { get; set; }
        public string OperationSequence { get; set; }
    }

    public sealed class GeneralMaterialOutboundCreatedDto
    {
        public string ExternalRequestNo { get; set; }
        public string VoucherId { get; set; }
        public string VoucherCode { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsVerified { get; set; }
        public bool Replayed { get; set; }
        public string U8Database { get; set; }
    }

    public sealed class GeneralMaterialOutboundDto
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
        public string U8Database { get; set; }
        public List<GeneralMaterialOutboundLineDto> Lines { get; set; }
    }

    public sealed class GeneralMaterialOutboundLineDto
    {
        public int AutoId { get; set; }
        public int? LineNumber { get; set; }
        public string MaterialCode { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public decimal Price { get; set; }
        public string BatchCode { get; set; }
        public string PositionCode { get; set; }
        public long? AllocationId { get; set; }
    }

    public sealed class GeneralApiResponse<T>
    {
        public bool Success { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string RequestId { get; set; }
        public T Data { get; set; }
    }
}
