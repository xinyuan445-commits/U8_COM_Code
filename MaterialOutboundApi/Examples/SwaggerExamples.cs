using System;
using System.Collections.Generic;
using MaterialOutboundApi.Models;
using Swashbuckle.Examples;

namespace MaterialOutboundApi.Examples
{
    public sealed class PreviewRequestExample : IExamplesProvider
    {
        public object GetExamples() => new PreviewRequest
        {
            WorkOrderCode = "SCDD26072885",
            SelectedMoDIds = new List<int> { 12345 },
            VoucherDate = "2026-07-29"
        };
    }

    public sealed class PreviewResponseExample : IExamplesProvider
    {
        public object GetExamples() => ApiResponse<PreviewDto>.Ok(
            new PreviewDto
            {
                PreviewToken = "72b853ff7a3e4de5952d9ca22bf990a2",
                ExpiresAt = new DateTime(2026, 7, 29, 20, 30, 0),
                WorkOrderCode = "SCDD26072885",
                VoucherDate = new DateTime(2026, 7, 29),
                DepartmentCode = "024",
                WarehouseCode = "KML_PP",
                PositionCode = "PP2",
                PositionName = "PP2",
                Lines = new List<PreviewLineDto>
                {
                    new PreviewLineDto
                    {
                        AllocateId = 67890,
                        MoDId = 12345,
                        WorkOrderLineNumber = 1,
                        ProductCode = "11103-OSB-ENF-4922-CKB1360-CMH",
                        MaterialCode = "MATERIAL-001",
                        RequiresBatch = true,
                        PlannedQuantity = 1,
                        IssuedQuantity = 0,
                        RemainingQuantity = 1,
                        SuggestedQuantity = 1
                    }
                }
            },
            "预览生成成功");
    }

    public sealed class CreateRequestExample : IExamplesProvider
    {
        public object GetExamples() => new CreateMaterialOutboundRequest
        {
            PreviewToken = "72b853ff7a3e4de5952d9ca22bf990a2",
            VoucherCode = null,
            Lines = new List<CreateMaterialOutboundLineRequest>
            {
                new CreateMaterialOutboundLineRequest
                {
                    AllocateId = 67890,
                    Quantity = 1,
                    BatchCode = "20260514GXS"
                }
            }
        };
    }

    public sealed class CreateResponseExample : IExamplesProvider
    {
        public object GetExamples() =>
            ApiResponse<MaterialOutboundCreatedDto>.Ok(
                new MaterialOutboundCreatedDto
                {
                    VoucherId = "1000123456",
                    VoucherCode = "0000001234",
                    CreatedAt = new DateTime(2026, 7, 29, 20, 20, 0),
                    Replayed = false
                },
                "材料出库单新增成功");
    }

    public sealed class OperationRequestExample : IExamplesProvider
    {
        public object GetExamples() => new OperationRequest
        {
            OperationDate = "2026-07-29"
        };
    }

    public sealed class VerifyResponseExample : IExamplesProvider
    {
        public object GetExamples() => ApiResponse<OperationResultDto>.Ok(
            new OperationResultDto
            {
                VoucherCode = "0000001234",
                Action = "verify",
                Replayed = false
            },
            "审核成功");
    }

    public sealed class UnverifyResponseExample : IExamplesProvider
    {
        public object GetExamples() => ApiResponse<OperationResultDto>.Ok(
            new OperationResultDto
            {
                VoucherCode = "0000001234",
                Action = "unverify",
                Replayed = false
            },
            "弃审成功");
    }

    public sealed class WorkOrderLinesResponseExample : IExamplesProvider
    {
        public object GetExamples() =>
            ApiResponse<List<WorkOrderLineDto>>.Ok(
                new List<WorkOrderLineDto>
                {
                    new WorkOrderLineDto
                    {
                        MoId = 10001,
                        WorkOrderCode = "SCDD26072885",
                        MoDId = 12345,
                        LineNumber = 1,
                        ProductCode =
                            "11103-OSB-ENF-4922-CKB1360-CMH",
                        OrderQuantity = 1,
                        DepartmentCode = "024",
                        Status = 3,
                        MaterialCount = 1
                    }
                });
    }

    public sealed class VoucherResponseExample : IExamplesProvider
    {
        public object GetExamples() => ApiResponse<MaterialOutboundDto>.Ok(
            new MaterialOutboundDto
            {
                Id = 1000123456,
                VoucherCode = "0000001234",
                VoucherDate = new DateTime(2026, 7, 29),
                WorkOrderCode = "SCDD26072885",
                WarehouseCode = "KML_PP",
                DepartmentCode = "024",
                Maker = "yuanxin",
                Auditor = "yuanxin",
                AuditDate = new DateTime(2026, 7, 29, 20, 25, 0),
                IsVerified = true,
                Lines = new List<MaterialOutboundLineDto>
                {
                    new MaterialOutboundLineDto
                    {
                        AutoId = 1000123457,
                        LineNumber = 1,
                        MaterialCode = "MATERIAL-001",
                        Quantity = 1,
                        BatchCode = "20260514GXS",
                        PositionCode = "PP2",
                        AllocateId = 67890
                    }
                }
            });
    }

    public sealed class HealthResponseExample : IExamplesProvider
    {
        public object GetExamples() => ApiResponse<HealthDto>.Ok(
            new HealthDto
            {
                Status = "running",
                Configured = true,
                ServerTime = new DateTime(2026, 7, 30, 10, 0, 0),
                Version = "1.1.0.0"
            });
    }
}
