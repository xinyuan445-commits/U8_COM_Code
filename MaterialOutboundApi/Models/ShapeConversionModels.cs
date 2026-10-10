using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MaterialOutboundApi.Models
{
    public sealed class ShapeConversionLineRequest
    {
        [Required] public string WarehouseCode { get; set; }
        [Required] public string InventoryCode { get; set; }
        [Required] public string BatchCode { get; set; }
        [Required] public string PositionCode { get; set; }
        [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
        public decimal Quantity { get; set; }
    }

    public sealed class ShapeConversionPreviewRequest
    {
        [Required] public ShapeConversionLineRequest Source { get; set; }
        [Required] public ShapeConversionLineRequest Target { get; set; }
        [StringLength(255)] public string Remark { get; set; }
        public bool? AutoVerify { get; set; }
    }

    public sealed class ShapeConversionPairRequest
    {
        [Required] public ShapeConversionLineRequest Source { get; set; }
        [Required] public ShapeConversionLineRequest Target { get; set; }
    }

    public sealed class ShapeConversionImportPreviewRequest
    {
        [Required] public List<ShapeConversionPairRequest> Pairs { get; set; }
        [StringLength(255)] public string Remark { get; set; }
        public bool? AutoVerify { get; set; }
    }

    public sealed class CreateShapeConversionRequest
    {
        [Required] public string PreviewToken { get; set; }
    }

    public sealed class ShapeConversionResolvedLineDto
    {
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public string InventoryCode { get; set; }
        public string InventoryName { get; set; }
        public string Specification { get; set; }
        public string BatchCode { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public string UnitCode { get; set; }
        public string UnitName { get; set; }
        public decimal Quantity { get; set; }
        public decimal CurrentStock { get; set; }
        public bool RequiresBatch { get; set; }
        public bool IsCosting { get; set; }
        public int ExpirationDateCalculation { get; set; }
        public int? ShelfLife { get; set; }
        public string ShelfLifeUnit { get; set; }
        public string Define1 { get; set; }
        public string Define4 { get; set; }
        public string Define5 { get; set; }
        public string Define6 { get; set; }
    }

    public sealed class ShapeConversionResolvedPairDto
    {
        public int GroupNumber { get; set; }
        public ShapeConversionResolvedLineDto Source { get; set; }
        public ShapeConversionResolvedLineDto Target { get; set; }
    }

    public sealed class ShapeConversionPreviewDto
    {
        public string PreviewToken { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime VoucherDate { get; set; }
        public string DepartmentCode { get; set; }
        public string DepartmentName { get; set; }
        public string InboundRdCode { get; set; }
        public string InboundRdName { get; set; }
        public string OutboundRdCode { get; set; }
        public string OutboundRdName { get; set; }
        public int VoucherTemplateId { get; set; }
        public string Remark { get; set; }
        public bool? AutoVerify { get; set; }
        public ShapeConversionResolvedLineDto Source { get; set; }
        public ShapeConversionResolvedLineDto Target { get; set; }
        public List<ShapeConversionResolvedPairDto> Pairs { get; set; }
    }

    public sealed class ShapeVoucherStatusDto
    {
        public string VoucherType { get; set; }
        public string Name { get; set; }
        public long Id { get; set; }
        public string Code { get; set; }
        public bool IsVerified { get; set; }
    }

    public sealed class ShapeConversionCreatedDto
    {
        public string U8Database { get; set; }
        public bool? AutoVerify { get; set; }
        public DateTime VoucherDate { get; set; }
        public ShapeVoucherStatusDto ShapeConversion { get; set; }
        public ShapeVoucherStatusDto OutboundVoucher { get; set; }
        public ShapeVoucherStatusDto InboundVoucher { get; set; }
        public string TargetInventoryName { get; set; }
        public bool Replayed { get; set; }
    }

    internal sealed class ShapeConversionPreviewRecord
    {
        [LiteDB.BsonId]
        public string Token { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Database { get; set; }
        public string Json { get; set; }
    }
}
