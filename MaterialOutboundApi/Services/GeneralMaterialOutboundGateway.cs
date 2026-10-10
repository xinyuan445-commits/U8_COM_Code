using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeneralMaterialOutbound;
using GeneralMaterialOutbound.Models;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;
using MaterialOutboundConsole.Models;
using MaterialOutboundConsole.Services;
using MSXML2;
using Newtonsoft.Json;

namespace MaterialOutboundApi.Services
{
    internal sealed class GeneralMaterialOutboundGateway
        : IGeneralMaterialOutboundGateway
    {
        private const string VoucherType = "11";
        private const string VoucherCardNumber = "0412";
        private const int MaximumLineCount = 500;
        private readonly SettingsStore _settings;
        private readonly StateStore _state;
        private readonly U8OperationQueue _u8Queue;
        private readonly string _applicationDirectory;
        private readonly SemaphoreSlim _idempotencyGate =
            new SemaphoreSlim(1, 1);

        public GeneralMaterialOutboundGateway(
            SettingsStore settings,
            StateStore state,
            U8OperationQueue u8Queue,
            string applicationDirectory)
        {
            _settings = settings;
            _state = state;
            _u8Queue = u8Queue;
            _applicationDirectory = applicationDirectory;
        }

        public GeneralMaterialOutboundValidationDto Validate(
            GeneralMaterialOutboundRequest request)
        {
            return Prepare(request).Result;
        }

        public async Task<GeneralMaterialOutboundCreatedDto> Create(
            GeneralMaterialOutboundRequest request,
            string idempotencyKey)
        {
            ValidateIdempotencyKey(idempotencyKey);
            PreparedOrder prepared = Prepare(request);
            string payloadJson = JsonConvert.SerializeObject(request);
            string payloadHash = Hash(
                prepared.Settings.SqlDatabase + "|" + payloadJson);
            string recordId = "general-create:" +
                              prepared.Settings.SqlDatabase + ":" +
                              idempotencyKey.Trim();

            await _idempotencyGate.WaitAsync();
            try
            {
                IdempotencyRecord record = _state.GetIdempotency(recordId);
                if (record != null && record.PayloadHash != payloadHash)
                {
                    throw Conflict(
                        "IDEMPOTENCY_KEY_REUSED",
                        "Idempotency-Key 已用于不同的请求内容。");
                }

                GeneralMaterialOutboundCreatedDto replay =
                    await TryResume(record, prepared);
                if (replay != null) return replay;

                record = record ?? new IdempotencyRecord { Id = recordId };
                record.PayloadHash = payloadHash;
                record.PayloadJson = payloadJson;
                record.Status = "Pending";
                _state.UpsertIdempotency(record);

                try
                {
                    GeneralMaterialOutboundCreatedDto result =
                        await _u8Queue.Execute(() =>
                            CreateOnSta(prepared, record));
                    record.Status = "Completed";
                    record.VoucherId = result.VoucherId;
                    record.VoucherCode = result.VoucherCode;
                    record.ResultJson = JsonConvert.SerializeObject(result);
                    _state.UpsertIdempotency(record);
                    return result;
                }
                catch
                {
                    record.Status = "Failed";
                    _state.UpsertIdempotency(record);
                    throw;
                }
            }
            finally
            {
                _idempotencyGate.Release();
            }
        }

        public GeneralMaterialOutboundDto Get(string voucherCode)
        {
            if (string.IsNullOrWhiteSpace(voucherCode))
            {
                throw BadRequest(
                    "VOUCHER_CODE_REQUIRED",
                    "材料出库单号不能为空。");
            }
            ApiSettings settings = RequiredSettings();
            MaterialOutboundDto source = new ApiRepository(
                    settings.BuildConnectionString())
                .GetMaterialOutbound(voucherCode.Trim());
            if (source == null)
            {
                throw new ApiException(
                    HttpStatusCode.NotFound,
                    "VOUCHER_NOT_FOUND",
                    "未找到材料出库单：" + voucherCode.Trim());
            }
            return Map(source, settings.SqlDatabase);
        }

        private PreparedOrder Prepare(GeneralMaterialOutboundRequest request)
        {
            EnsureConfigured();
            if (request == null)
            {
                throw BadRequest("REQUEST_REQUIRED", "请求体不能为空。");
            }
            string issueMode = (request.IssueMode ?? string.Empty).Trim();
            if (!string.Equals(
                    issueMode,
                    GeneralIssueModes.Direct,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    issueMode,
                    GeneralIssueModes.ProductionOrder,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw BadRequest(
                    "ISSUE_MODE_INVALID",
                    "issueMode 只允许 direct 或 productionOrder。");
            }
            bool productionMode = string.Equals(
                issueMode,
                GeneralIssueModes.ProductionOrder,
                StringComparison.OrdinalIgnoreCase);
            DateTime voucherDate = ParseDate(request.VoucherDate);
            ApiSettings settings = RequiredSettings();
            ValidateYear(voucherDate, settings);
            ValidateText(request.ExternalRequestNo, "externalRequestNo");
            ValidateText(request.WarehouseCode, "warehouseCode");
            ValidateText(request.DepartmentCode, "departmentCode");
            ValidateText(request.RdCode, "rdCode");
            if (request.Lines == null || request.Lines.Count == 0)
            {
                throw BadRequest(
                    "MATERIAL_LINES_REQUIRED",
                    "至少提交一条材料明细。");
            }
            if (request.Lines.Count > MaximumLineCount)
            {
                throw BadRequest(
                    "MATERIAL_LINES_TOO_MANY",
                    "单次最多提交 " + MaximumLineCount + " 条材料明细。");
            }

            var apiRepository = new ApiRepository(
                settings.BuildConnectionString());
            var repository = new GeneralMaterialOutboundRepository(
                settings.BuildConnectionString());
            WarehouseRow warehouse = apiRepository.GetWarehouse(
                request.WarehouseCode.Trim());
            if (warehouse == null)
            {
                throw BadRequest(
                    "WAREHOUSE_NOT_FOUND",
                    "U8 中不存在仓库：" + request.WarehouseCode.Trim());
            }
            if (repository.GetDepartment(request.DepartmentCode.Trim()) == null)
            {
                throw BadRequest(
                    "DEPARTMENT_NOT_FOUND",
                    "U8 中不存在部门：" + request.DepartmentCode.Trim());
            }
            if (repository.GetRdStyle(request.RdCode.Trim()) == null)
            {
                throw BadRequest(
                    "RD_CODE_NOT_FOUND",
                    "U8 中不存在出库类别：" + request.RdCode.Trim());
            }

            List<AllocationRow> allocations = new List<AllocationRow>();
            Dictionary<long, AllocationRow> allocationById =
                new Dictionary<long, AllocationRow>();
            if (productionMode)
            {
                ValidateText(
                    request.ProductionOrderCode,
                    "productionOrderCode");
                var detailIds = request.Lines.Select(line =>
                {
                    if (!line.ProductionOrderDetailId.HasValue ||
                        line.ProductionOrderDetailId.Value <= 0)
                    {
                        throw BadRequest(
                            "PRODUCTION_ORDER_DETAIL_REQUIRED",
                            "工单领用的每条明细都必须提供 productionOrderDetailId（moDId）。");
                    }
                    if (!line.AllocationId.HasValue || line.AllocationId.Value <= 0)
                    {
                        throw BadRequest(
                            "ALLOCATION_ID_REQUIRED",
                            "工单领用的每条明细都必须提供 allocationId。");
                    }
                    return line.ProductionOrderDetailId.Value;
                }).Distinct().ToList();
                allocations = apiRepository.GetAllocations(
                    request.ProductionOrderCode.Trim(),
                    detailIds);
                allocationById = allocations.ToDictionary(
                    row => (long)row.AllocateId);
                foreach (GeneralMaterialOutboundLineRequest line in request.Lines)
                {
                    if (!allocationById.TryGetValue(
                            line.AllocationId.Value,
                            out AllocationRow allocation) ||
                        allocation.MoDId != line.ProductionOrderDetailId.Value)
                    {
                        throw BadRequest(
                            "ALLOCATION_NOT_FOUND",
                            "allocationId " + line.AllocationId.Value +
                            " 不属于指定工单及 productionOrderDetailId。");
                    }
                }

                var departments = allocations
                    .Select(row => row.DepartmentCode)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (departments.Count > 1)
                {
                    throw BadRequest(
                        "MULTIPLE_DEPARTMENTS",
                        "所选工单材料属于多个部门，请拆单。");
                }
                if (departments.Count == 1 && !string.Equals(
                        departments[0],
                        request.DepartmentCode.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw BadRequest(
                        "DEPARTMENT_MISMATCH",
                        "传入部门与生产工单部门不一致。");
                }
                var moIds = allocations.Select(row => row.MoId).Distinct().ToList();
                if (moIds.Count != 1)
                {
                    throw BadRequest(
                        "MULTIPLE_PRODUCTION_ORDERS",
                        "一次请求只能关联一个生产订单主记录。");
                }
                if (request.ProductionOrderId.HasValue &&
                    request.ProductionOrderId.Value != moIds[0])
                {
                    throw BadRequest(
                        "PRODUCTION_ORDER_ID_MISMATCH",
                        "productionOrderId 与 U8 工单记录不一致。");
                }
            }

            var prepared = new PreparedOrder
            {
                Request = request,
                Settings = settings,
                VoucherDate = voucherDate,
                IssueMode = productionMode
                    ? GeneralIssueModes.ProductionOrder
                    : GeneralIssueModes.Direct,
                Warehouse = warehouse,
                Lines = new List<PreparedLine>()
            };

            for (int index = 0; index < request.Lines.Count; index++)
            {
                GeneralMaterialOutboundLineRequest input = request.Lines[index];
                if (input == null)
                {
                    throw BadRequest(
                        "MATERIAL_LINE_REQUIRED",
                        "材料明细不能为 null。");
                }
                if (input.Quantity <= 0)
                {
                    throw BadRequest(
                        "QUANTITY_INVALID",
                        "第 " + (index + 1) + " 行数量必须大于零。");
                }
                AllocationRow allocation = productionMode
                    ? allocationById[input.AllocationId.Value]
                    : null;
                string materialCode = productionMode
                    ? allocation.MaterialCode
                    : Trim(input.MaterialCode);
                if (string.IsNullOrWhiteSpace(materialCode))
                {
                    throw BadRequest(
                        "MATERIAL_CODE_REQUIRED",
                        "第 " + (index + 1) + " 行 materialCode 不能为空。");
                }
                if (productionMode &&
                    !string.IsNullOrWhiteSpace(input.MaterialCode) &&
                    !string.Equals(
                        input.MaterialCode.Trim(),
                        materialCode,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw BadRequest(
                        "MATERIAL_CODE_MISMATCH",
                        "第 " + (index + 1) + " 行材料编码与工单子件不一致。");
                }
                GeneralInventoryRow inventory = repository.GetInventory(
                    materialCode);
                if (inventory == null)
                {
                    throw BadRequest(
                        "MATERIAL_NOT_FOUND",
                        "U8 中不存在存货：" + materialCode);
                }
                string batchCode = Trim(input.BatchCode);
                if (inventory.RequiresBatch && string.IsNullOrWhiteSpace(batchCode))
                {
                    throw BadRequest(
                        "BATCH_REQUIRED",
                        "批次管理材料 " + materialCode + " 必须填写 batchCode。");
                }
                string positionCode = Trim(input.PositionCode);
                PositionRow position = null;
                if (warehouse.UsesPosition)
                {
                    if (string.IsNullOrWhiteSpace(positionCode))
                    {
                        throw BadRequest(
                            "POSITION_REQUIRED",
                            "仓库 " + warehouse.Code + " 启用了货位管理，第 " +
                            (index + 1) + " 行必须填写 positionCode。");
                    }
                    position = apiRepository.GetPosition(
                        warehouse.Code,
                        positionCode);
                    if (position == null)
                    {
                        throw BadRequest(
                            "POSITION_NOT_FOUND",
                            "仓库 " + warehouse.Code + " 中不存在货位：" +
                            positionCode);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(positionCode))
                {
                    throw BadRequest(
                        "POSITION_NOT_ENABLED",
                        "仓库 " + warehouse.Code + " 未启用货位管理，不能传 positionCode。");
                }

                decimal unitCost = input.UnitCost ??
                    repository.GetLatestInboundUnitCost(materialCode, batchCode);
                if (!input.UnitCost.HasValue && unitCost == 0)
                {
                    unitCost = settings.DefaultUnitCost;
                }
                decimal price = input.Price ?? input.Quantity * unitCost;
                prepared.Lines.Add(new PreparedLine
                {
                    Input = input,
                    LineNo = input.LineNo ?? index + 1,
                    MaterialCode = materialCode,
                    MaterialName = inventory.Name,
                    RequiresBatch = inventory.RequiresBatch,
                    BatchCode = batchCode,
                    Position = position,
                    UnitCost = unitCost,
                    Price = price,
                    Allocation = allocation,
                    ProductCode = productionMode
                        ? allocation.ProductCode
                        : First(input.ProductCode, request.ProductCode),
                    ProductionOrderCode = productionMode
                        ? allocation.MoCode
                        : First(input.ProductionOrderCode,
                            request.ProductionOrderCode),
                    ProductionOrderId = productionMode
                        ? allocation.MoId
                        : request.ProductionOrderId,
                    ProductionOrderSequence = productionMode
                        ? allocation.SortSeq
                        : input.ProductionOrderSequence,
                    OperationSequence = productionMode
                        ? allocation.OpSeq
                        : Trim(input.OperationSequence)
                });
            }

            List<int> duplicateLineNos = prepared.Lines
                .GroupBy(line => line.LineNo)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateLineNos.Count > 0)
            {
                throw BadRequest(
                    "LINE_NO_DUPLICATED",
                    "lineNo 不能重复：" + string.Join(",", duplicateLineNos));
            }
            ValidateStock(prepared, repository);
            prepared.Result = BuildValidationResult(prepared);
            return prepared;
        }

        private static void ValidateStock(
            PreparedOrder prepared,
            GeneralMaterialOutboundRepository repository)
        {
            var groups = prepared.Lines.GroupBy(line => string.Join(
                "\u001f",
                line.MaterialCode,
                line.BatchCode ?? string.Empty,
                line.Position?.Code ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                PreparedLine first = group.First();
                decimal required = group.Sum(line => line.Input.Quantity);
                decimal available = repository.GetAvailableStock(
                    prepared.Warehouse.Code,
                    first.MaterialCode,
                    first.BatchCode);
                foreach (PreparedLine line in group)
                {
                    line.AvailableQuantity = available;
                }
                if (available + 0.000001m < required)
                {
                    throw new ApiException(
                        (HttpStatusCode)422,
                        "U8_STOCK_INSUFFICIENT",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "U8 实际库存不足：存货 {0}，批号 {1}，货位 {2}，实际库存 {3:0.######}，本次要出库 {4:0.######}。",
                            first.MaterialCode,
                            string.IsNullOrWhiteSpace(first.BatchCode)
                                ? "无批号"
                                : first.BatchCode,
                            first.Position?.Code ?? "无货位",
                            available,
                            required));
                }
            }
        }

        private static GeneralMaterialOutboundValidationDto BuildValidationResult(
            PreparedOrder prepared)
        {
            List<string> warnings = prepared.Lines
                .Where(line => line.UnitCost == 0)
                .Select(line => "材料 " + line.MaterialCode +
                                " 未找到入库单价，将按 0 单价提交。")
                .Distinct()
                .ToList();
            return new GeneralMaterialOutboundValidationDto
            {
                Valid = true,
                U8Database = prepared.Settings.SqlDatabase,
                IssueMode = prepared.IssueMode,
                VoucherDate = prepared.VoucherDate,
                WarehouseCode = prepared.Warehouse.Code,
                WarehouseName = prepared.Warehouse.Name,
                DepartmentCode = prepared.Request.DepartmentCode.Trim(),
                RdCode = prepared.Request.RdCode.Trim(),
                ProductionOrderCode = prepared.Lines
                    .Select(line => line.ProductionOrderCode)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                ProductionOrderId = prepared.Lines
                    .Select(line => line.ProductionOrderId)
                    .FirstOrDefault(value => value.HasValue),
                ProductCode = prepared.Lines
                    .Select(line => line.ProductCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == 1
                        ? prepared.Lines[0].ProductCode
                        : null,
                Lines = prepared.Lines.Select(line =>
                    new GeneralMaterialOutboundValidationLineDto
                    {
                        LineNo = line.LineNo,
                        MaterialCode = line.MaterialCode,
                        MaterialName = line.MaterialName,
                        Quantity = line.Input.Quantity,
                        BatchCode = line.BatchCode,
                        RequiresBatch = line.RequiresBatch,
                        PositionCode = line.Position?.Code,
                        PositionName = line.Position?.Name,
                        AvailableQuantity = line.AvailableQuantity,
                        UnitCost = line.UnitCost,
                        Price = line.Price,
                        ProductionOrderDetailId =
                            line.Input.ProductionOrderDetailId,
                        AllocationId = line.Input.AllocationId,
                        ProductionOrderCode = line.ProductionOrderCode,
                        ProductionOrderSequence =
                            line.ProductionOrderSequence,
                        ProductCode = line.ProductCode,
                        OperationSequence = line.OperationSequence
                    }).ToList(),
                Warnings = warnings
            };
        }

        private async Task<GeneralMaterialOutboundCreatedDto> TryResume(
            IdempotencyRecord record,
            PreparedOrder prepared)
        {
            if (record == null) return null;
            MaterialOutboundDto voucher = FindVoucher(record, prepared.Settings);
            if (voucher != null)
            {
                bool autoVerify = prepared.Request.AutoVerify ?? true;
                if (autoVerify && !voucher.IsVerified)
                {
                    await _u8Queue.Execute(() =>
                    {
                        using (U8CoSession session = OpenSession(
                                   prepared.Settings,
                                   voucher.VoucherDate))
                        {
                            VerifyVoucher(
                                session,
                                voucher.Id,
                                voucher.VoucherCode);
                        }
                        return true;
                    });
                    voucher.IsVerified = true;
                }
                var resumed = Result(
                    prepared.Request.ExternalRequestNo,
                    voucher.Id.ToString(CultureInfo.InvariantCulture),
                    voucher.VoucherCode,
                    record.UpdatedAt,
                    voucher.IsVerified,
                    true,
                    prepared.Settings.SqlDatabase);
                record.Status = "Completed";
                record.VoucherId = resumed.VoucherId;
                record.VoucherCode = resumed.VoucherCode;
                record.ResultJson = JsonConvert.SerializeObject(resumed);
                _state.UpsertIdempotency(record);
                return resumed;
            }
            if (record.Status == "Completed" &&
                !string.IsNullOrWhiteSpace(record.ResultJson))
            {
                throw new ApiException(
                    HttpStatusCode.BadGateway,
                    "U8_VOUCHER_RESULT_MISSING",
                    "幂等记录显示已成功，但 U8 中未找到对应材料出库单，请人工核对。");
            }
            record.VoucherId = null;
            record.VoucherCode = null;
            return null;
        }

        private GeneralMaterialOutboundCreatedDto CreateOnSta(
            PreparedOrder prepared,
            IdempotencyRecord record)
        {
            GeneralVoucherXml xml = new GeneralVoucherXmlBuilder(
                _applicationDirectory).Build(
                BuildHead(prepared),
                prepared.Lines.Select(BuildBody).ToList());
            using (U8CoSession session = OpenSession(
                       prepared.Settings,
                       prepared.VoucherDate))
            {
                IXMLDOMDocument2 body = CreateDocument(xml.Body, "表体");
                IXMLDOMDocument2 position = new DOMDocument26Class();
                string error = string.Empty;
                string voucherId = string.Empty;
                string voucherCode = Trim(prepared.Request.VoucherCode);
                IXMLDOMDocument2 head;
                if (string.IsNullOrWhiteSpace(voucherCode))
                {
                    voucherCode = GetNewVoucherCode(session, xml.Head);
                    head = CreateDocument(xml.Head, "表头");
                    var row = head.selectSingleNode("//z:row") as IXMLDOMElement;
                    if (row == null)
                    {
                        throw BadRequest(
                            "INVALID_U8_XML",
                            "表头 XML 中找不到 z:row 节点。");
                    }
                    row.setAttribute("ccode", voucherCode);
                    head = CreateDocument(head.xml, "表头");
                }
                else
                {
                    if (new ApiRepository(
                            prepared.Settings.BuildConnectionString())
                        .GetVoucherId(voucherCode).HasValue)
                    {
                        throw Conflict(
                            "VOUCHER_CODE_EXISTS",
                            "材料出库单号已经存在：" + voucherCode);
                    }
                    head = CreateDocument(xml.Head, "表头");
                }

                record.VoucherCode = voucherCode;
                _state.UpsertIdempotency(record);
                ADODB.Connection connection = null;
                bool inserted = session.Voucher.Insert(
                    VoucherType,
                    head,
                    body,
                    position,
                    ref error,
                    ref connection,
                    ref voucherId);
                if (!inserted)
                {
                    throw new ApiException(
                        (HttpStatusCode)422,
                        "U8_INSERT_FAILED",
                        "U8 新增材料出库单失败：" +
                        (string.IsNullOrWhiteSpace(error)
                            ? "U8 未返回错误文本；尝试单号=" + voucherCode
                            : error.Trim()));
                }
                if (!int.TryParse(
                        voucherId,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int createdId))
                {
                    throw new ApiException(
                        HttpStatusCode.BadGateway,
                        "U8_VOUCHER_ID_INVALID",
                        "U8 已新增材料出库单，但没有返回有效的单据 ID。");
                }
                record.VoucherId = voucherId;
                _state.UpsertIdempotency(record);
                voucherCode = GetActualVoucherCodeById(
                    createdId,
                    prepared.Settings);
                record.VoucherCode = voucherCode;
                _state.UpsertIdempotency(record);
                bool autoVerify = prepared.Request.AutoVerify ?? true;
                if (autoVerify)
                {
                    VerifyVoucher(session, createdId, voucherCode);
                }
                return Result(
                    prepared.Request.ExternalRequestNo,
                    voucherId,
                    voucherCode,
                    DateTime.Now,
                    autoVerify,
                    false,
                    prepared.Settings.SqlDatabase);
            }
        }

        private static GeneralVoucherHead BuildHead(PreparedOrder prepared)
        {
            GeneralMaterialOutboundRequest request = prepared.Request;
            GeneralHeadDefinedFields defined = request.DefinedFields ??
                                               new GeneralHeadDefinedFields();
            List<string> products = prepared.Lines
                .Select(line => line.ProductCode)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            PreparedLine first = prepared.Lines.First();
            return new GeneralVoucherHead
            {
                ddate = prepared.VoucherDate,
                ccode = Trim(request.VoucherCode),
                cwhcode = prepared.Warehouse.Code,
                crdcode = request.RdCode.Trim(),
                cdepcode = request.DepartmentCode.Trim(),
                cpersoncode = Trim(request.PersonCode),
                cprobatch = Trim(request.ProductionBatch),
                cmemo = Trim(request.Memo),
                cmaker = First(request.Maker, prepared.Settings.Maker,
                    prepared.Settings.U8UserId),
                csource = First(
                    request.Source,
                    prepared.IssueMode == GeneralIssueModes.ProductionOrder
                        ? "生产订单"
                        : "库存"),
                cbustype = First(request.BusinessType, "领料"),
                cbuscode = Trim(request.BusinessCode),
                vt_id = request.VoucherTemplateId ??
                        prepared.Settings.VoucherTemplateId,
                cpspcode = products.Count == 1 ? products[0] : null,
                cmpocode = first.ProductionOrderCode,
                iproorderid = first.ProductionOrderId,
                cdefine1 = Trim(defined.Define1),
                cdefine2 = Trim(defined.Define2),
                cdefine3 = Trim(defined.Define3),
                cdefine4 = defined.Define4,
                cdefine5 = defined.Define5,
                cdefine6 = defined.Define6,
                cdefine7 = defined.Define7,
                cdefine8 = Trim(defined.Define8),
                cdefine9 = Trim(defined.Define9),
                cdefine10 = Trim(defined.Define10),
                cdefine11 = Trim(defined.Define11),
                cdefine12 = Trim(defined.Define12),
                cdefine13 = Trim(defined.Define13),
                cdefine14 = Trim(defined.Define14),
                cdefine15 = defined.Define15,
                cdefine16 = defined.Define16
            };
        }

        private static GeneralVoucherBody BuildBody(PreparedLine line)
        {
            GeneralMaterialOutboundLineRequest input = line.Input;
            GeneralBodyFreeFields free = input.FreeFields ??
                                         new GeneralBodyFreeFields();
            GeneralBodyDefinedFields defined = input.DefinedFields ??
                                               new GeneralBodyDefinedFields();
            GeneralBatchPropertyFields batch = input.BatchProperties ??
                                               new GeneralBatchPropertyFields();
            return new GeneralVoucherBody
            {
                cinvcode = line.MaterialCode,
                iquantity = input.Quantity,
                inquantity = input.InQuantity ?? input.Quantity,
                inum = input.Number,
                innum = input.InNumber,
                iunitcost = line.UnitCost,
                iprice = line.Price,
                iinvexchrate = input.ExchangeRate,
                cbatch = line.RequiresBatch ? line.BatchCode : null,
                cposition = line.Position?.Code,
                cposname = line.Position?.Name,
                iposflag = line.Position == null ? (int?)null : 1,
                invcode = line.ProductCode,
                impoids = line.Input.AllocationId,
                cmocode = line.ProductionOrderCode,
                imoseq = line.ProductionOrderSequence,
                iopseq = line.OperationSequence,
                copdesc = Trim(input.OperationDescription),
                cmworkcentercode = Trim(input.WorkCenterCode),
                isotype = input.SalesOrderType,
                iordertype = input.OrderType,
                iorderdid = input.OrderDetailId,
                iordercode = Trim(input.OrderCode),
                iorderseq = input.OrderSequence,
                ipesodid = input.PeSoDetailId,
                ipesotype = input.PeSoType,
                cpesocode = Trim(input.PeSoCode),
                ipesoseq = input.PeSoSequence,
                cbmemo = Trim(input.Remark),
                irowno = line.LineNo,
                cfree1 = Trim(free.Free1),
                cfree2 = Trim(free.Free2),
                cfree3 = Trim(free.Free3),
                cfree4 = Trim(free.Free4),
                cfree5 = Trim(free.Free5),
                cfree6 = Trim(free.Free6),
                cfree7 = Trim(free.Free7),
                cfree8 = Trim(free.Free8),
                cfree9 = Trim(free.Free9),
                cfree10 = Trim(free.Free10),
                cdefine22 = Trim(defined.Define22),
                cdefine23 = Trim(defined.Define23),
                cdefine24 = Trim(defined.Define24),
                cdefine25 = Trim(defined.Define25),
                cdefine26 = defined.Define26,
                cdefine27 = defined.Define27,
                cdefine28 = Trim(defined.Define28),
                cdefine29 = Trim(defined.Define29),
                cdefine30 = Trim(defined.Define30),
                cdefine31 = Trim(defined.Define31),
                cdefine32 = Trim(defined.Define32),
                cdefine33 = Trim(defined.Define33),
                cdefine34 = defined.Define34,
                cdefine35 = defined.Define35,
                cdefine36 = defined.Define36,
                cdefine37 = defined.Define37,
                cbatchproperty1 = batch.Property1,
                cbatchproperty2 = batch.Property2,
                cbatchproperty3 = batch.Property3,
                cbatchproperty4 = batch.Property4,
                cbatchproperty5 = batch.Property5,
                cbatchproperty6 = Trim(batch.Property6),
                cbatchproperty7 = Trim(batch.Property7),
                cbatchproperty8 = Trim(batch.Property8),
                cbatchproperty9 = Trim(batch.Property9),
                cbatchproperty10 = batch.Property10
            };
        }

        private MaterialOutboundDto FindVoucher(
            IdempotencyRecord record,
            ApiSettings settings)
        {
            var repository = new ApiRepository(settings.BuildConnectionString());
            if (int.TryParse(
                    record.VoucherId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int voucherId))
            {
                string actualCode = repository.GetVoucherCodeById(voucherId);
                if (!string.IsNullOrWhiteSpace(actualCode))
                {
                    return repository.GetMaterialOutbound(actualCode);
                }
            }
            return string.IsNullOrWhiteSpace(record.VoucherCode)
                ? null
                : repository.GetMaterialOutbound(record.VoucherCode);
        }

        private static GeneralMaterialOutboundDto Map(
            MaterialOutboundDto source,
            string database)
        {
            return new GeneralMaterialOutboundDto
            {
                Id = source.Id,
                VoucherCode = source.VoucherCode,
                VoucherDate = source.VoucherDate,
                WorkOrderCode = source.WorkOrderCode,
                WarehouseCode = source.WarehouseCode,
                DepartmentCode = source.DepartmentCode,
                Maker = source.Maker,
                Auditor = source.Auditor,
                AuditDate = source.AuditDate,
                IsVerified = source.IsVerified,
                U8Database = database,
                Lines = source.Lines.Select(line =>
                    new GeneralMaterialOutboundLineDto
                    {
                        AutoId = line.AutoId,
                        LineNumber = line.LineNumber,
                        MaterialCode = line.MaterialCode,
                        Quantity = line.Quantity,
                        UnitCost = line.UnitCost,
                        Price = line.Price,
                        BatchCode = line.BatchCode,
                        PositionCode = line.PositionCode,
                        AllocationId = line.AllocateId
                    }).ToList()
            };
        }

        private U8CoSession OpenSession(ApiSettings settings, DateTime date)
        {
            try
            {
                return new U8CoSession(new U8Account
                {
                    SubId = settings.U8SubId,
                    AccId = settings.U8AccId,
                    Year = settings.U8Year,
                    UserId = settings.U8UserId,
                    Password = settings.U8Password,
                    Date = date.ToString("yyyy-MM-dd"),
                    Server = settings.U8Server,
                    Serial = settings.U8Serial ?? string.Empty
                });
            }
            catch (Exception exception)
            {
                throw new ApiException(
                    HttpStatusCode.ServiceUnavailable,
                    "U8_LOGIN_FAILED",
                    "U8 登录失败，请确认账号配置正确且未被其他位置占用。",
                    exception);
            }
        }

        private static string GetNewVoucherCode(
            U8CoSession session,
            string headXml)
        {
            string[] numberTypes = { VoucherType, VoucherCardNumber };
            string lastError = string.Empty;
            foreach (string numberType in numberTypes)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    IXMLDOMDocument2 head = CreateDocument(
                        headXml,
                        "自动取号表头");
                    string error = string.Empty;
                    bool validate = false;
                    bool phase = false;
                    try
                    {
                        string code = session.Voucher.GetNewVouchCode(
                            numberType,
                            head,
                            ref error,
                            ref validate,
                            ref phase);
                        if (!string.IsNullOrWhiteSpace(code)) return code.Trim();
                    }
                    catch (COMException exception)
                    {
                        error = "COM HRESULT=0x" +
                                exception.ErrorCode.ToString("X8") + "，" +
                                exception.Message;
                    }
                    lastError = error;
                    if (attempt == 0) Thread.Sleep(300);
                }
            }
            throw new ApiException(
                HttpStatusCode.ServiceUnavailable,
                "U8_VOUCHER_NUMBER_FAILED",
                "U8 自动生成材料出库单号失败：" +
                (string.IsNullOrWhiteSpace(lastError)
                    ? "U8 未返回错误文本"
                    : lastError));
        }

        private static void VerifyVoucher(
            U8CoSession session,
            int voucherId,
            string voucherCode)
        {
            string error = string.Empty;
            object id = voucherId;
            if (!session.Voucher.Verify(VoucherType, id, ref error))
            {
                throw new ApiException(
                    (HttpStatusCode)422,
                    "U8_AUTO_VERIFY_FAILED",
                    "U8 材料出库单已生成（" + voucherCode +
                    "），但自动审核失败：" + error);
            }
        }

        private static IXMLDOMDocument2 CreateDocument(
            string xml,
            string label)
        {
            IXMLDOMDocument2 document = new DOMDocument26Class();
            if (!document.loadXML(xml))
            {
                throw BadRequest(
                    "INVALID_U8_XML",
                    label + " XML 无效：" + document.parseError.reason);
            }
            return document;
        }

        private static string GetActualVoucherCodeById(
            int voucherId,
            ApiSettings settings)
        {
            var repository = new ApiRepository(settings.BuildConnectionString());
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string code = repository.GetVoucherCodeById(voucherId);
                if (!string.IsNullOrWhiteSpace(code)) return code.Trim();
                Thread.Sleep(100);
            }
            throw new ApiException(
                HttpStatusCode.BadGateway,
                "U8_VOUCHER_CODE_LOOKUP_FAILED",
                "U8 已新增材料出库单，但无法根据 ID 查询实际单据号。");
        }

        private ApiSettings RequiredSettings()
        {
            EnsureConfigured();
            return _settings.Current.ForDatabase(U8DatabaseContext.Current);
        }

        private void EnsureConfigured()
        {
            if (!_settings.IsConfigured)
            {
                throw new ApiException(
                    HttpStatusCode.ServiceUnavailable,
                    "NOT_CONFIGURED",
                    "请先在服务器本机配置数据库和 U8 登录信息。");
            }
        }

        private static DateTime ParseDate(string value)
        {
            if (!DateTime.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime date))
            {
                throw BadRequest(
                    "INVALID_DATE",
                    "voucherDate 必须使用 yyyy-MM-dd 格式。");
            }
            return date;
        }

        private static void ValidateYear(DateTime date, ApiSettings settings)
        {
            if (!int.TryParse(settings.U8Year, out int year) || date.Year != year)
            {
                throw BadRequest(
                    "VOUCHER_DATE_YEAR_MISMATCH",
                    "单据日期年份必须与 U8 登录年度一致。");
            }
        }

        private static void ValidateIdempotencyKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length < 8 || value.Length > 64)
            {
                throw BadRequest(
                    "IDEMPOTENCY_KEY_REQUIRED",
                    "Idempotency-Key 必须为 8 到 64 个字符。");
            }
        }

        private static void ValidateText(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw BadRequest(
                    "FIELD_REQUIRED",
                    field + " 不能为空。");
            }
        }

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
            }
        }

        private static string Trim(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string First(params string[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?.Trim();

        private static ApiException BadRequest(string code, string message) =>
            new ApiException(HttpStatusCode.BadRequest, code, message);

        private static ApiException Conflict(string code, string message) =>
            new ApiException(HttpStatusCode.Conflict, code, message);

        private static GeneralMaterialOutboundCreatedDto Result(
            string externalRequestNo,
            string voucherId,
            string voucherCode,
            DateTime createdAt,
            bool isVerified,
            bool replayed,
            string database)
        {
            return new GeneralMaterialOutboundCreatedDto
            {
                ExternalRequestNo = externalRequestNo,
                VoucherId = voucherId,
                VoucherCode = voucherCode,
                CreatedAt = createdAt,
                IsVerified = isVerified,
                Replayed = replayed,
                U8Database = database
            };
        }

        private sealed class PreparedOrder
        {
            public GeneralMaterialOutboundRequest Request { get; set; }
            public ApiSettings Settings { get; set; }
            public DateTime VoucherDate { get; set; }
            public string IssueMode { get; set; }
            public WarehouseRow Warehouse { get; set; }
            public List<PreparedLine> Lines { get; set; }
            public GeneralMaterialOutboundValidationDto Result { get; set; }
        }

        private sealed class PreparedLine
        {
            public GeneralMaterialOutboundLineRequest Input { get; set; }
            public int LineNo { get; set; }
            public string MaterialCode { get; set; }
            public string MaterialName { get; set; }
            public bool RequiresBatch { get; set; }
            public string BatchCode { get; set; }
            public PositionRow Position { get; set; }
            public decimal AvailableQuantity { get; set; }
            public decimal UnitCost { get; set; }
            public decimal Price { get; set; }
            public AllocationRow Allocation { get; set; }
            public string ProductCode { get; set; }
            public string ProductionOrderCode { get; set; }
            public int? ProductionOrderId { get; set; }
            public int? ProductionOrderSequence { get; set; }
            public string OperationSequence { get; set; }
        }
    }
}
