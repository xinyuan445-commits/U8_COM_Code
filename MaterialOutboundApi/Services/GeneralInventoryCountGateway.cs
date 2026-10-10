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
using GeneralInventoryCount;
using GeneralInventoryCount.Models;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;
using MaterialOutboundConsole.Models;
using MaterialOutboundConsole.Services;
using MSXML2;
using Newtonsoft.Json;

namespace MaterialOutboundApi.Services
{
    internal sealed class GeneralInventoryCountGateway
        : IGeneralInventoryCountGateway
    {
        private const string VoucherType = "18";
        private const string VoucherCardNumber = "0307";
        private const int MaximumLineCount = 1000;
        private readonly SettingsStore _settings;
        private readonly StateStore _state;
        private readonly U8OperationQueue _u8Queue;
        private readonly SemaphoreSlim _idempotencyGate =
            new SemaphoreSlim(1, 1);

        public GeneralInventoryCountGateway(
            SettingsStore settings,
            StateStore state,
            U8OperationQueue u8Queue)
        {
            _settings = settings;
            _state = state;
            _u8Queue = u8Queue;
        }

        public GeneralInventoryCountValidationDto Validate(
            GeneralInventoryCountRequest request)
        {
            return Prepare(request).Validation;
        }

        public async Task<GeneralInventoryCountCreatedDto> Create(
            GeneralInventoryCountRequest request,
            string idempotencyKey)
        {
            ValidateIdempotencyKey(idempotencyKey);
            PreparedCount prepared = Prepare(request);
            string payloadJson = JsonConvert.SerializeObject(request);
            string payloadHash = Hash(
                prepared.Settings.SqlDatabase + "|" + payloadJson);
            string recordId = "inventory-count-create:" +
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

                GeneralInventoryCountCreatedDto replay = TryReplay(
                    record,
                    prepared);
                if (replay != null) return replay;

                record = record ?? new IdempotencyRecord { Id = recordId };
                record.PayloadHash = payloadHash;
                record.PayloadJson = payloadJson;
                record.Status = "Pending";
                _state.UpsertIdempotency(record);

                try
                {
                    GeneralInventoryCountCreatedDto result =
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

        public GeneralInventoryCountDto Get(string voucherCode)
        {
            if (string.IsNullOrWhiteSpace(voucherCode))
            {
                throw BadRequest(
                    "VOUCHER_CODE_REQUIRED",
                    "盘点单号不能为空。");
            }
            ApiSettings settings = RequiredSettings();
            var repository = new GeneralInventoryCountRepository(
                settings.BuildConnectionString());
            InventoryCountVoucherRow voucher = repository.GetVoucher(
                voucherCode.Trim());
            if (voucher == null)
            {
                throw new ApiException(
                    HttpStatusCode.NotFound,
                    "VOUCHER_NOT_FOUND",
                    "未找到盘点单：" + voucherCode.Trim());
            }
            return Map(
                voucher,
                repository.GetVoucherLines(voucher.Id),
                settings.SqlDatabase);
        }

        public async Task<InventoryCountQuantityUpdateDto>
            UpdateActualQuantities(
                string voucherCode,
                InventoryCountQuantityUpdateRequest request)
        {
            PreparedQuantityUpdate prepared = PrepareQuantityUpdate(
                voucherCode,
                request);
            return await _u8Queue.Execute(() =>
                UpdateActualQuantitiesOnSta(prepared));
        }

        public Task<InventoryCountQuantityUpdateDto> UpdateLines(
            string voucherCode, InventoryCountQuantityUpdateRequest request)
        {
            if (request?.Lines == null || request.Lines.Any(line =>
                    line == null || string.IsNullOrWhiteSpace(line.ExpectedLineVersion)))
                throw BadRequest("LINE_VERSION_REQUIRED", "请先查询盘点单，并提交每行的 expectedLineVersion。");
            return UpdateActualQuantities(voucherCode, request);
        }

        private PreparedQuantityUpdate PrepareQuantityUpdate(
            string voucherCode,
            InventoryCountQuantityUpdateRequest request)
        {
            if (string.IsNullOrWhiteSpace(voucherCode))
            {
                throw BadRequest(
                    "VOUCHER_CODE_REQUIRED",
                    "盘点单号不能为空。");
            }
            if (request?.Lines == null || request.Lines.Count == 0)
            {
                throw BadRequest(
                    "UPDATE_LINES_REQUIRED",
                    "至少提交一条需要修改的盘点行。");
            }
            if (request.Lines.Count > MaximumLineCount)
            {
                throw BadRequest(
                    "UPDATE_LINES_TOO_MANY",
                    "单次最多修改 " + MaximumLineCount + " 条盘点明细。");
            }

            if (request.Lines.Any(line => line == null))
                throw BadRequest("INVALID_UPDATE_LINE", "盘点明细不能为空。");
            List<int> duplicateLineNumbers = request.Lines
                .GroupBy(line => line.LineNo)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateLineNumbers.Count > 0)
            {
                throw BadRequest(
                    "DUPLICATE_LINE_NUMBER",
                    "行号重复：" + string.Join(",", duplicateLineNumbers));
            }
            foreach (InventoryCountQuantityUpdateLineRequest line in
                     request.Lines)
            {
                if (line.LineNo <= 0)
                {
                    throw BadRequest(
                        "INVALID_LINE_NUMBER",
                        "行号必须大于 0。");
                }
                if (!line.ActualQuantity.HasValue ||
                    line.ActualQuantity.Value < 0)
                {
                    throw BadRequest(
                        "INVALID_ACTUAL_QUANTITY",
                        "第 " + line.LineNo +
                        " 行盘点数量不能为空且不能小于 0。");
                }
                if (line.ExpectedActualQuantity.HasValue &&
                    line.ExpectedActualQuantity.Value < 0)
                {
                    throw BadRequest(
                        "INVALID_EXPECTED_QUANTITY",
                        "第 " + line.LineNo +
                        " 行原盘点数量不能小于 0。");
                }
                if ((line.UpdateBatchCode || line.UpdatePosition ||
                     line.UpdateReason || line.UpdateRemark) &&
                    string.IsNullOrWhiteSpace(line.ExpectedLineVersion))
                    throw BadRequest("LINE_VERSION_REQUIRED", "修改明细字段必须提交 expectedLineVersion。");
                if ((!line.UpdateBatchCode && line.BatchCode != null) ||
                    (!line.UpdatePosition && line.PositionName != null) ||
                    (!line.UpdateReason && line.Reason != null) ||
                    (!line.UpdateRemark && line.Remark != null))
                    throw BadRequest("UPDATE_FLAG_REQUIRED", "提交字段值时必须同时指定对应的 update 标记。");
            }

            ApiSettings settings = RequiredSettings();
            var repository = new GeneralInventoryCountRepository(
                settings.BuildConnectionString());
            string code = voucherCode.Trim();
            InventoryCountVoucherRow voucher = repository.GetVoucher(code);
            if (voucher == null)
            {
                throw new ApiException(
                    HttpStatusCode.NotFound,
                    "VOUCHER_NOT_FOUND",
                    "未找到盘点单：" + code);
            }
            if (!string.IsNullOrWhiteSpace(voucher.Auditor) || voucher.AuditDate.HasValue)
            {
                throw Conflict(
                    "VOUCHER_ALREADY_VERIFIED",
                    "盘点单已经审核，不能修改：" + code);
            }

            List<InventoryCountVoucherLineRow> currentLines =
                repository.GetVoucherLines(voucher.Id);
            Dictionary<int, InventoryCountVoucherLineRow> byLineNumber = IndexLines(currentLines);
            var changes = new List<PreparedQuantityChange>();
            foreach (InventoryCountQuantityUpdateLineRequest input in
                     request.Lines)
            {
                if (!byLineNumber.TryGetValue(
                        input.LineNo,
                        out InventoryCountVoucherLineRow current))
                {
                    throw BadRequest(
                        "VOUCHER_LINE_NOT_FOUND",
                        "盘点单 " + code + " 不存在行号 " + input.LineNo + "。");
                }
                if (input.ExpectedActualQuantity.HasValue &&
                    input.ExpectedActualQuantity.Value !=
                    current.ActualQuantity)
                {
                    throw Conflict(
                        "ACTUAL_QUANTITY_CHANGED",
                        "第 " + input.LineNo +
                        " 行盘点数量已经变化，请重新查询后再修改。");
                }
                if (!string.IsNullOrWhiteSpace(input.ExpectedLineVersion) &&
                    input.ExpectedLineVersion != current.LineVersion)
                    throw Conflict("VOUCHER_LINE_CHANGED", "第 " + input.LineNo + " 行已变化，请重新查询。");
                if (current.AdjustInQuantity != 0 || current.AdjustOutQuantity != 0)
                    throw Conflict("COUNT_LINE_ALREADY_ADJUSTED", "第 " + input.LineNo + " 行已有库存调整，不能修改。");

                string positionCode = current.PositionCode;
                if (input.UpdateBatchCode)
                {
                    input.BatchCode = (input.BatchCode ?? "").Trim();
                    var inventory = repository.GetInventory(current.InventoryCode);
                    if (inventory == null)
                        throw BadRequest("INVENTORY_NOT_FOUND", "存货档案已不存在：" + current.InventoryCode);
                    if (inventory.RequiresBatch && input.BatchCode == "")
                        throw BadRequest("BATCH_REQUIRED", "该存货启用批次管理，批号不能为空。");
                    if (!inventory.RequiresBatch && input.BatchCode != "")
                        throw BadRequest("BATCH_NOT_ENABLED", "该存货未启用批次管理，不能填写批号。");
                }
                if (input.UpdatePosition)
                {
                    var warehouse = new ApiRepository(settings.BuildConnectionString())
                        .GetWarehouse(voucher.WarehouseCode);
                    if (warehouse == null || !warehouse.UsesPosition)
                        throw BadRequest("WAREHOUSE_POSITION_NOT_ENABLED", "该仓库未启用货位管理，不能修改货位。");
                    input.PositionName = (input.PositionName ?? "").Trim();
                    if (input.PositionName == "")
                        throw BadRequest("POSITION_REQUIRED", "货位名称不能为空。");
                    var positions = repository.GetPositionsByName(voucher.WarehouseCode, input.PositionName);
                    if (positions.Count != 1)
                        throw BadRequest("POSITION_NAME_NOT_UNIQUE", "货位名称不存在或重名，请核对：" + input.PositionName);
                    if (positions[0].Name.StartsWith("停用", StringComparison.Ordinal))
                        throw BadRequest("POSITION_DISABLED", "不能选择停用货位。");
                    positionCode = positions[0].Code;
                }
                if (input.UpdateReason) input.Reason = (input.Reason ?? "").Trim();
                if (input.UpdateRemark) input.Remark = (input.Remark ?? "").Trim();
                changes.Add(new PreparedQuantityChange
                {
                    Input = input,
                    Current = current,
                    PositionCode = positionCode,
                    PreviousActualQuantity = current.ActualQuantity,
                    PreviousActualNumber = current.ActualNumber
                });
            }

            return new PreparedQuantityUpdate
            {
                Settings = settings,
                Voucher = voucher,
                Changes = changes
            };
        }

        private InventoryCountQuantityUpdateDto UpdateActualQuantitiesOnSta(
            PreparedQuantityUpdate prepared)
        {
            var repository = new GeneralInventoryCountRepository(
                prepared.Settings.BuildConnectionString());
            InventoryCountVoucherRow currentVoucher = repository.GetVoucher(
                prepared.Voucher.VoucherCode);
            if (currentVoucher == null)
            {
                throw new ApiException(
                    HttpStatusCode.NotFound,
                    "VOUCHER_NOT_FOUND",
                    "盘点单在修改前已不存在：" +
                    prepared.Voucher.VoucherCode);
            }
            if (currentVoucher.Id != prepared.Voucher.Id ||
                currentVoucher.WarehouseCode != prepared.Voucher.WarehouseCode ||
                currentVoucher.VoucherDate != prepared.Voucher.VoucherDate)
                throw Conflict("VOUCHER_CHANGED", "盘点单已变化，请重新查询。");
            if (!string.IsNullOrWhiteSpace(currentVoucher.Auditor) || currentVoucher.AuditDate.HasValue)
            {
                throw Conflict(
                    "VOUCHER_ALREADY_VERIFIED",
                    "盘点单在修改前已被审核，请重新查询。");
            }

            Dictionary<int, InventoryCountVoucherLineRow> currentByLine =
                IndexLines(repository.GetVoucherLines(currentVoucher.Id));
            foreach (PreparedQuantityChange change in prepared.Changes)
            {
                if (!currentByLine.TryGetValue(
                        change.Input.LineNo,
                        out InventoryCountVoucherLineRow current) ||
                    current.LineVersion != change.Current.LineVersion)
                {
                    throw Conflict(
                        "VOUCHER_LINE_CHANGED",
                        "第 " + change.Input.LineNo +
                        " 行已变化，请重新查询后再修改。");
                }
            }

            using (U8CoSession session = OpenSession(
                       prepared.Settings,
                       currentVoucher.VoucherDate))
            {
                string loadError = string.Empty;
                IXMLDOMDocument2 head = new DOMDocument26Class();
                IXMLDOMDocument2 body = new DOMDocument26Class();
                IXMLDOMDocument2 position = new DOMDocument26Class();
                if (!session.Voucher.Load(
                        VoucherType,
                        "ID=" + currentVoucher.Id,
                        ref head,
                        ref body,
                        ref position,
                        ref loadError))
                {
                    throw new ApiException(
                        HttpStatusCode.BadGateway,
                        "U8_VOUCHER_LOAD_FAILED",
                        "读取 U8 盘点单失败：" + loadError);
                }

                var edited = InventoryCountLineEditor.Apply(
                    head.xml, body.xml, position?.xml, prepared.Changes);
                if (!head.loadXML(edited.Head) || !body.loadXML(edited.Body))
                    throw new ApiException(HttpStatusCode.BadGateway,
                        "U8_UPDATE_XML_INVALID", "盘点修改 XML 加载失败。");
                // Recheck row timestamps after COM Load; preserve the native
                // cbufts in the XML so U8 also checks its own final write race.
                var latest = IndexLines(repository.GetVoucherLines(currentVoucher.Id));
                foreach (var change in prepared.Changes)
                    if (!latest.TryGetValue(change.Input.LineNo, out var row) ||
                        row.LineVersion != change.Current.LineVersion)
                        throw Conflict("VOUCHER_LINE_CHANGED", "明细已变化，请重新查询。");

                string updateError = string.Empty;
                if (!session.Voucher.Update(
                        VoucherType,
                        head,
                        body,
                        position,
                        ref updateError))
                {
                    throw new ApiException(
                        (HttpStatusCode)422,
                        "U8_UPDATE_FAILED",
                        "U8 修改盘点明细失败：" +
                        (string.IsNullOrWhiteSpace(updateError)
                            ? "U8 未返回错误文本"
                            : updateError.Trim()));
                }
            }

            List<InventoryCountVoucherLineRow> updatedLines =
                repository.GetVoucherLines(currentVoucher.Id);
            Dictionary<int, InventoryCountVoucherLineRow> updatedByLine = IndexLines(updatedLines);
            foreach (var change in prepared.Changes)
            {
                var input = change.Input;
                if (!updatedByLine.TryGetValue(input.LineNo, out var updated) ||
                    updated.AutoId != change.Current.AutoId ||
                    updated.ActualQuantity != input.ActualQuantity.Value ||
                    (input.UpdateBatchCode && (updated.BatchCode ?? "") != (input.BatchCode ?? "")) ||
                    (input.UpdatePosition && updated.PositionCode != change.PositionCode) ||
                    (input.UpdateReason && (updated.Reason ?? "") != (input.Reason ?? "")) ||
                    (input.UpdateRemark && (updated.Remark ?? "") != (input.Remark ?? "")))
                    throw new ApiException(HttpStatusCode.BadGateway,
                        "U8_UPDATE_RESULT_UNCONFIRMED", "U8 已返回保存成功，但回读与请求不一致，请在 U8 中核对，勿直接重试。");
            }
            return new InventoryCountQuantityUpdateDto
            {
                VoucherCode = currentVoucher.VoucherCode,
                UpdatedAt = DateTime.Now,
                IsVerified = false,
                U8Database = prepared.Settings.SqlDatabase,
                Lines = prepared.Changes.Select(change =>
                {
                    InventoryCountVoucherLineRow updated =
                        updatedByLine[change.Input.LineNo];
                    return new InventoryCountQuantityUpdatedLineDto
                    {
                        LineNo = change.Input.LineNo,
                        InventoryCode = updated.InventoryCode,
                        InventoryName = updated.InventoryName,
                        BatchCode = updated.BatchCode ?? "",
                        PositionCode = updated.PositionCode ?? "",
                        PositionName = updated.PositionName ?? "",
                        BookQuantity = updated.BookQuantity,
                        PreviousActualQuantity =
                            change.PreviousActualQuantity,
                        ActualQuantity = updated.ActualQuantity,
                        DifferenceQuantity = updated.ActualQuantity -
                                             updated.BookQuantity,
                        PreviousActualNumber = change.PreviousActualNumber,
                        ActualNumber = updated.ActualNumber,
                        DifferenceNumber = DifferenceNumber(updated),
                        PreviousBatchCode = change.Current.BatchCode,
                        PreviousPositionName = change.Current.PositionName,
                        PreviousReason = change.Current.Reason,
                        Reason = updated.Reason ?? "",
                        PreviousRemark = change.Current.Remark,
                        Remark = updated.Remark ?? ""
                    };
                }).ToList()
            };
        }

        private PreparedCount Prepare(GeneralInventoryCountRequest request)
        {
            EnsureConfigured();
            if (request == null)
            {
                throw BadRequest("REQUEST_REQUIRED", "请求体不能为空。");
            }
            DateTime voucherDate = ParseDate(
                request.VoucherDate,
                "voucherDate");
            DateTime countDate = string.IsNullOrWhiteSpace(request.CountDate)
                ? voucherDate
                : ParseDate(request.CountDate, "countDate");
            ApiSettings settings = RequiredSettings();
            ValidateYear(voucherDate, settings);
            ValidateYear(countDate, settings);
            ValidateText(request.ExternalRequestNo, "externalRequestNo");
            ValidateText(request.WarehouseCode, "warehouseCode");
            if (request.Lines == null || request.Lines.Count == 0)
            {
                throw BadRequest(
                    "COUNT_LINES_REQUIRED",
                    "至少提交一条盘点明细。");
            }
            if (request.Lines.Count > MaximumLineCount)
            {
                throw BadRequest(
                    "COUNT_LINES_TOO_MANY",
                    "单次最多提交 " + MaximumLineCount + " 条盘点明细。");
            }

            var apiRepository = new ApiRepository(
                settings.BuildConnectionString());
            var repository = new GeneralInventoryCountRepository(
                settings.BuildConnectionString());
            WarehouseRow warehouse = apiRepository.GetWarehouse(
                request.WarehouseCode.Trim());
            if (warehouse == null)
            {
                throw BadRequest(
                    "WAREHOUSE_NOT_FOUND",
                    "U8 中不存在仓库：" + request.WarehouseCode.Trim());
            }

            string departmentCode = Trim(request.DepartmentCode);
            if (departmentCode != null &&
                repository.GetDepartment(departmentCode) == null)
            {
                throw BadRequest(
                    "DEPARTMENT_NOT_FOUND",
                    "U8 中不存在部门：" + departmentCode);
            }
            string inboundRdCode = First(request.InboundRdCode, "107");
            string outboundRdCode = First(request.OutboundRdCode, "207");
            if (repository.GetRdStyle(inboundRdCode) == null)
            {
                throw BadRequest(
                    "INBOUND_RD_CODE_NOT_FOUND",
                    "U8 中不存在盘盈入库类别：" + inboundRdCode);
            }
            if (repository.GetRdStyle(outboundRdCode) == null)
            {
                throw BadRequest(
                    "OUTBOUND_RD_CODE_NOT_FOUND",
                    "U8 中不存在盘亏出库类别：" + outboundRdCode);
            }

            string headerPosition = Trim(request.PositionCode);
            if (headerPosition != null &&
                apiRepository.GetPosition(
                    warehouse.Code,
                    headerPosition) == null)
            {
                throw BadRequest(
                    "POSITION_NOT_FOUND",
                    "U8 中不存在货位：" + headerPosition);
            }

            var warnings = new List<string>();
            var lines = new List<PreparedLine>();
            var duplicateKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < request.Lines.Count; index++)
            {
                GeneralInventoryCountLineRequest input = request.Lines[index];
                if (input == null)
                {
                    throw BadRequest(
                        "COUNT_LINE_REQUIRED",
                        "第 " + (index + 1) + " 行不能为空。");
                }
                ValidateText(input.InventoryCode, "inventoryCode");
                if (!input.ActualQuantity.HasValue ||
                    input.ActualQuantity.Value < 0)
                {
                    throw BadRequest(
                        "ACTUAL_QUANTITY_INVALID",
                        "第 " + (index + 1) + " 行 actualQuantity 必须大于或等于 0。");
                }
                string inventoryCode = input.InventoryCode.Trim();
                InventoryCountInventoryRow inventory =
                    repository.GetInventory(inventoryCode);
                if (inventory == null)
                {
                    throw BadRequest(
                        "INVENTORY_NOT_FOUND",
                        "U8 中不存在存货：" + inventoryCode);
                }
                string batchCode = Trim(input.BatchCode);
                if (inventory.RequiresBatch && batchCode == null)
                {
                    throw BadRequest(
                        "BATCH_REQUIRED",
                        "存货 " + inventoryCode + " 启用了批次管理，必须提供 batchCode。");
                }
                InventoryCountFreeFields free = input.FreeFields ??
                                                new InventoryCountFreeFields();
                ValidateRequiredFreeField(inventory.RequiresFree1, free.Free1, 1, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree2, free.Free2, 2, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree3, free.Free3, 3, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree4, free.Free4, 4, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree5, free.Free5, 5, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree6, free.Free6, 6, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree7, free.Free7, 7, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree8, free.Free8, 8, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree9, free.Free9, 9, inventoryCode);
                ValidateRequiredFreeField(inventory.RequiresFree10, free.Free10, 10, inventoryCode);
                string positionCode = First(
                    input.PositionCode,
                    headerPosition);
                PositionRow position = null;
                if (warehouse.UsesPosition && positionCode == null)
                {
                    throw BadRequest(
                        "POSITION_REQUIRED",
                        "仓库 " + warehouse.Code + " 启用了货位管理，第 " +
                        (index + 1) + " 行必须提供 positionCode。");
                }
                if (positionCode != null)
                {
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

                string duplicateKey = string.Join("|", new[]
                {
                    inventoryCode,
                    batchCode ?? string.Empty,
                    positionCode ?? string.Empty,
                    Trim(free.Free1) ?? string.Empty,
                    Trim(free.Free2) ?? string.Empty,
                    Trim(free.Free3) ?? string.Empty,
                    Trim(free.Free4) ?? string.Empty,
                    Trim(free.Free5) ?? string.Empty,
                    Trim(free.Free6) ?? string.Empty,
                    Trim(free.Free7) ?? string.Empty,
                    Trim(free.Free8) ?? string.Empty,
                    Trim(free.Free9) ?? string.Empty,
                    Trim(free.Free10) ?? string.Empty
                });
                if (!duplicateKeys.Add(duplicateKey))
                {
                    throw BadRequest(
                        "DUPLICATE_COUNT_LINE",
                        "第 " + (index + 1) + " 行与前面的存货、批号、货位及自由项重复。");
                }

                InventoryCountBalanceRow currentBalance = repository.GetCurrentBalance(
                    warehouse.Code,
                    inventoryCode,
                    batchCode,
                    positionCode,
                    free);
                decimal currentBook = currentBalance.Quantity;
                decimal book = input.BookQuantity ?? currentBook;
                if (input.BookQuantity.HasValue &&
                    Math.Abs(input.BookQuantity.Value - currentBook) > 0.000001m)
                {
                    warnings.Add(
                        "第 " + (index + 1) + " 行传入账面数量 " +
                        input.BookQuantity.Value.ToString(
                            CultureInfo.InvariantCulture) +
                        "，当前 U8 结存为 " + currentBook.ToString(
                            CultureInfo.InvariantCulture) + "。");
                }
                string auxiliaryUnitCode = First(
                    input.AuxiliaryUnitCode,
                    inventory.AuxiliaryUnitCode);
                decimal? exchangeRate = input.ExchangeRate ??
                                        inventory.ExchangeRate;
                if (inventory.RequiresAuxiliaryUnit)
                {
                    if (auxiliaryUnitCode == null ||
                        !auxiliaryUnitCode.Equals(
                            inventory.AuxiliaryUnitCode,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw BadRequest(
                            "AUXILIARY_UNIT_INVALID",
                            "存货 " + inventoryCode + " 的辅计量单位必须为 " +
                            inventory.AuxiliaryUnitCode + "（" +
                            inventory.AuxiliaryUnitName + "）。");
                    }
                    if (!exchangeRate.HasValue || exchangeRate.Value <= 0)
                    {
                        throw BadRequest(
                            "EXCHANGE_RATE_REQUIRED",
                            "存货 " + inventoryCode +
                            " 启用了辅计量单位，但没有有效的换算率。");
                    }
                }
                decimal? bookNumber = input.BookNumber;
                decimal? actualNumber = input.ActualNumber;
                if (inventory.RequiresAuxiliaryUnit)
                {
                    decimal rate = exchangeRate.Value;
                    bookNumber = input.BookNumber ??
                        (input.BookQuantity.HasValue
                            ? book / rate
                            : currentBalance.Number);
                    actualNumber = input.ActualNumber ??
                                   input.ActualQuantity.Value / rate;
                }
                int? massDate = input.MassDate ?? inventory.MassDate;
                string massUnit = First(input.MassUnit, inventory.MassUnit);
                DateTime? manufactureDate = input.ManufactureDate;
                DateTime? invalidDate = input.InvalidDate;
                int expirationDateCalculation =
                    input.ExpirationDateCalculation ??
                    (inventory.RequiresExpirationDate ? 2 : 0);
                DateTime? expirationDate = input.ExpirationDate;
                string expirationDateText = Trim(input.ExpirationDateText);
                if (inventory.RequiresExpirationDate)
                {
                    expirationDate = input.ExpirationDate ??
                        new DateTime(voucherDate.Year, 12, 31);
                    invalidDate = input.InvalidDate ??
                                  expirationDate.Value.AddDays(1);
                    if (!manufactureDate.HasValue &&
                        massDate.HasValue && massDate.Value > 0)
                    {
                        manufactureDate = expirationDate.Value.AddDays(
                            1 - massDate.Value);
                    }
                    expirationDateText = expirationDate.Value.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                }
                decimal unitPrice = input.PlanUnitPrice ??
                    new GeneralMaterialOutboundRepository(
                        settings.BuildConnectionString())
                    .GetLatestInboundUnitCost(inventoryCode, batchCode);
                lines.Add(new PreparedLine
                {
                    Input = input,
                    LineNo = input.LineNo ?? index + 1,
                    Inventory = inventory,
                    BatchCode = batchCode,
                    Position = position,
                    CurrentBookQuantity = currentBook,
                    BookQuantity = book,
                    ActualQuantity = input.ActualQuantity.Value,
                    AuxiliaryUnitCode = auxiliaryUnitCode,
                    ExchangeRate = exchangeRate,
                    CurrentBookNumber = inventory.RequiresAuxiliaryUnit
                        ? currentBalance.Number
                        : (decimal?)null,
                    BookNumber = bookNumber,
                    ActualNumber = actualNumber,
                    MassDate = massDate,
                    MassUnit = massUnit,
                    ManufactureDate = manufactureDate,
                    InvalidDate = invalidDate,
                    ExpirationDateCalculation = expirationDateCalculation,
                    ExpirationDate = expirationDate,
                    ExpirationDateText = expirationDateText,
                    UnitPrice = unitPrice
                });
            }

            int? templateVoucherId = repository.GetTemplateVoucherId();
            if (!templateVoucherId.HasValue)
            {
                throw new ApiException(
                    HttpStatusCode.ServiceUnavailable,
                    "INVENTORY_COUNT_TEMPLATE_NOT_FOUND",
                    "当前账套没有可用于读取 U8 XML 结构的历史盘点单。");
            }
            string requestedCode = Trim(request.VoucherCode);
            if (requestedCode != null &&
                repository.VoucherCodeExists(requestedCode))
            {
                throw Conflict(
                    "VOUCHER_CODE_EXISTS",
                    "盘点单号已经存在：" + requestedCode);
            }

            return new PreparedCount
            {
                Request = request,
                Settings = settings,
                VoucherDate = voucherDate,
                CountDate = countDate,
                Warehouse = warehouse,
                DepartmentCode = departmentCode,
                InboundRdCode = inboundRdCode,
                OutboundRdCode = outboundRdCode,
                HeaderPositionCode = headerPosition,
                Lines = lines,
                TemplateVoucherId = templateVoucherId.Value,
                Validation = new GeneralInventoryCountValidationDto
                {
                    Valid = true,
                    U8Database = settings.SqlDatabase,
                    VoucherDate = voucherDate,
                    CountDate = countDate,
                    WarehouseCode = warehouse.Code,
                    WarehouseName = warehouse.Name,
                    UsesPosition = warehouse.UsesPosition,
                    InboundRdCode = inboundRdCode,
                    OutboundRdCode = outboundRdCode,
                    Warnings = warnings,
                    Lines = lines.Select(line =>
                        new GeneralInventoryCountValidationLineDto
                        {
                            LineNo = line.LineNo,
                            InventoryCode = line.Inventory.Code,
                            InventoryName = line.Inventory.Name,
                            RequiresBatch = line.Inventory.RequiresBatch,
                            BatchCode = line.BatchCode,
                            PositionCode = line.Position?.Code,
                            PositionName = line.Position?.Name,
                            RequiresExpirationDate =
                                line.Inventory.RequiresExpirationDate,
                            MassDate = line.MassDate,
                            MassUnit = line.MassUnit,
                            ManufactureDate = line.ManufactureDate,
                            InvalidDate = line.InvalidDate,
                            ExpirationDateCalculation =
                                line.ExpirationDateCalculation,
                            ExpirationDate = line.ExpirationDate,
                            CurrentBookQuantity = line.CurrentBookQuantity,
                            BookQuantity = line.BookQuantity,
                            ActualQuantity = line.ActualQuantity,
                            DifferenceQuantity = line.ActualQuantity -
                                                 line.BookQuantity,
                            AuxiliaryUnitCode = line.AuxiliaryUnitCode,
                            ExchangeRate = line.ExchangeRate,
                            CurrentBookNumber = line.CurrentBookNumber,
                            BookNumber = line.BookNumber,
                            ActualNumber = line.ActualNumber
                        }).ToList()
                }
            };
        }

        private GeneralInventoryCountCreatedDto TryReplay(
            IdempotencyRecord record,
            PreparedCount prepared)
        {
            if (record == null) return null;
            if (!string.IsNullOrWhiteSpace(record.ResultJson))
            {
                GeneralInventoryCountCreatedDto saved =
                    JsonConvert.DeserializeObject<GeneralInventoryCountCreatedDto>(
                        record.ResultJson);
                if (saved != null)
                {
                    saved.Replayed = true;
                    return saved;
                }
            }
            if (string.IsNullOrWhiteSpace(record.VoucherCode)) return null;
            var repository = new GeneralInventoryCountRepository(
                prepared.Settings.BuildConnectionString());
            InventoryCountVoucherRow voucher = repository.GetVoucher(
                record.VoucherCode);
            if (voucher == null) return null;
            return Result(
                prepared.Request.ExternalRequestNo,
                voucher.Id.ToString(CultureInfo.InvariantCulture),
                voucher.VoucherCode,
                DateTime.Now,
                !string.IsNullOrWhiteSpace(voucher.Auditor),
                true,
                prepared.Settings.SqlDatabase);
        }

        private GeneralInventoryCountCreatedDto CreateOnSta(
            PreparedCount prepared,
            IdempotencyRecord record)
        {
            using (U8CoSession session = OpenSession(
                       prepared.Settings,
                       prepared.VoucherDate))
            {
                string loadError = string.Empty;
                IXMLDOMDocument2 templateHead = new DOMDocument26Class();
                IXMLDOMDocument2 templateBody = new DOMDocument26Class();
                IXMLDOMDocument2 templatePosition = new DOMDocument26Class();
                if (!session.Voucher.Load(
                        VoucherType,
                        "ID=" + prepared.TemplateVoucherId,
                        ref templateHead,
                        ref templateBody,
                        ref templatePosition,
                        ref loadError))
                {
                    throw new ApiException(
                        HttpStatusCode.BadGateway,
                        "U8_TEMPLATE_LOAD_FAILED",
                        "读取 U8 盘点单 XML 模板失败：" + loadError);
                }

                string voucherCode = First(
                    record.VoucherCode,
                    prepared.Request.VoucherCode);
                InventoryCountVoucherXml provisional =
                    InventoryCountXmlBuilder.Build(
                        templateHead.xml,
                        templateBody.xml,
                        BuildHead(prepared, voucherCode),
                        prepared.Lines.Select(line =>
                            BuildBody(line, voucherCode)).ToList());
                if (voucherCode == null)
                {
                    voucherCode = GetNewVoucherCode(
                        session,
                        provisional.Head);
                }

                var repository = new GeneralInventoryCountRepository(
                    prepared.Settings.BuildConnectionString());
                if (repository.VoucherCodeExists(voucherCode))
                {
                    throw Conflict(
                        "VOUCHER_CODE_EXISTS",
                        "盘点单号已经存在：" + voucherCode);
                }
                record.VoucherCode = voucherCode;
                _state.UpsertIdempotency(record);

                InventoryCountVoucherXml xml = InventoryCountXmlBuilder.Build(
                    templateHead.xml,
                    templateBody.xml,
                    BuildHead(prepared, voucherCode),
                    prepared.Lines.Select(line =>
                        BuildBody(line, voucherCode)).ToList());
                IXMLDOMDocument2 head = CreateDocument(xml.Head, "表头");
                IXMLDOMDocument2 body = CreateDocument(xml.Body, "表体");
                IXMLDOMDocument2 position = new DOMDocument26Class();
                string error = string.Empty;
                string voucherId = string.Empty;
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
                        "U8 新增盘点单失败：" +
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
                        "U8 已新增盘点单，但没有返回有效的单据 ID。");
                }
                record.VoucherId = voucherId;
                _state.UpsertIdempotency(record);
                voucherCode = repository.GetVoucherCodeById(createdId) ??
                              voucherCode;
                record.VoucherCode = voucherCode;
                _state.UpsertIdempotency(record);

                bool autoVerify = prepared.Request.AutoVerify ?? false;
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

        private static InventoryCountVoucherHead BuildHead(
            PreparedCount prepared,
            string voucherCode)
        {
            GeneralInventoryCountRequest request = prepared.Request;
            InventoryCountHeadDefinedFields fields = request.DefinedFields ??
                                                     new InventoryCountHeadDefinedFields();
            return new InventoryCountVoucherHead
            {
                dcvdate = prepared.VoucherDate,
                ccvcode = voucherCode,
                dacdate = prepared.CountDate,
                vt_id = request.VoucherTemplateId ?? 29,
                cwhcode = prepared.Warehouse.Code,
                ccvbatch = Trim(request.CountBatch),
                cdepcode = prepared.DepartmentCode,
                cirdcode = prepared.InboundRdCode,
                cordcode = prepared.OutboundRdCode,
                csource = First(request.Source, "1"),
                cpersoncode = Trim(request.PersonCode),
                ccvmemo = Trim(request.Memo),
                cposition = prepared.HeaderPositionCode,
                cmaker = First(
                    request.Maker,
                    prepared.Settings.Maker,
                    prepared.Settings.U8UserId),
                ccvtype = First(request.CountType, "普通仓库盘点"),
                ccvperiod = Trim(request.AccountingPeriod),
                bposcheck = prepared.Warehouse.UsesPosition ? 1 : 0,
                cdefine1 = Trim(fields.Define1),
                cdefine2 = Trim(fields.Define2),
                cdefine3 = Trim(fields.Define3),
                cdefine4 = fields.Define4,
                cdefine5 = fields.Define5,
                cdefine6 = fields.Define6,
                cdefine7 = fields.Define7,
                cdefine8 = Trim(fields.Define8),
                cdefine9 = Trim(fields.Define9),
                cdefine10 = Trim(fields.Define10),
                cdefine11 = Trim(fields.Define11),
                cdefine12 = Trim(fields.Define12),
                cdefine13 = Trim(fields.Define13),
                cdefine14 = Trim(fields.Define14),
                cdefine15 = fields.Define15,
                cdefine16 = fields.Define16
            };
        }

        private static InventoryCountVoucherBody BuildBody(
            PreparedLine line,
            string voucherCode)
        {
            GeneralInventoryCountLineRequest input = line.Input;
            InventoryCountFreeFields free = input.FreeFields ??
                                                new InventoryCountFreeFields();
            InventoryCountBodyDefinedFields fields = input.DefinedFields ??
                                                      new InventoryCountBodyDefinedFields();
            InventoryCountBatchPropertyFields batch = input.BatchProperties ??
                                                       new InventoryCountBatchPropertyFields();
            decimal difference = line.ActualQuantity - line.BookQuantity;
            return new InventoryCountVoucherBody
            {
                cinvcode = line.Inventory.Code,
                cposition = line.Position?.Code,
                ccvbatch = line.BatchCode,
                iadinquantity = input.AdjustInQuantity ?? 0,
                iadinnum = input.AdjustInNumber ?? 0,
                iadoutquantity = input.AdjustOutQuantity ?? 0,
                iadoutnum = input.AdjustOutNumber ?? 0,
                iallowwaste = input.AllowedWasteRate,
                iactualwaste = input.ActualWasteRate,
                cassunit = line.AuxiliaryUnitCode,
                iinvexchrate = line.ExchangeRate,
                icvnum = line.BookNumber,
                icvquantity = line.BookQuantity,
                icvcnum = line.ActualNumber,
                icvcquantity = line.ActualQuantity,
                ykjs = line.ActualNumber.HasValue && line.BookNumber.HasValue
                    ? line.ActualNumber.Value - line.BookNumber.Value
                    : (decimal?)null,
                yksl = difference,
                icvjustnum = line.BookNumber,
                icvjustquantity = line.BookQuantity,
                ccvreason = Trim(input.Reason),
                ccvcode = voucherCode,
                rdsid = input.RecordDetailId,
                ijhdj = line.UnitPrice,
                ijhje = input.BookAmount ?? line.UnitPrice * line.BookQuantity,
                isjdj = input.ActualAmount ?? line.UnitPrice * line.ActualQuantity,
                isjje = input.DifferenceAmount ?? line.UnitPrice * difference,
                citemcode = Trim(input.ItemCode),
                citem_class = Trim(input.ItemClassCode),
                cbvencode = Trim(input.VendorCode),
                cinvouchcode = Trim(input.InVoucherCode),
                cinvouchtype = Trim(input.InVoucherType),
                imassdate = line.MassDate,
                cmassunit = line.MassUnit,
                ddisdate = line.InvalidDate,
                dmadedate = line.ManufactureDate,
                isodid = input.SalesOrderDetailId,
                isotype = input.SalesOrderType ?? 0,
                iexpiratdatecalcu = line.ExpirationDateCalculation,
                cexpirationdate = line.ExpirationDateText,
                dexpirationdate = line.ExpirationDate,
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
                cdefine22 = Trim(fields.Define22),
                cdefine23 = Trim(fields.Define23),
                cdefine24 = Trim(fields.Define24),
                cdefine25 = Trim(fields.Define25),
                cdefine26 = fields.Define26,
                cdefine27 = fields.Define27,
                cdefine28 = Trim(fields.Define28),
                cdefine29 = Trim(fields.Define29),
                cdefine30 = Trim(fields.Define30),
                cdefine31 = Trim(fields.Define31),
                cdefine32 = Trim(fields.Define32),
                cdefine33 = Trim(fields.Define33),
                cdefine34 = fields.Define34,
                cdefine35 = fields.Define35,
                cdefine36 = fields.Define36,
                cdefine37 = fields.Define37,
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

        private static string GetNewVoucherCode(
            U8CoSession session,
            string headXml)
        {
            string lastError = string.Empty;
            foreach (string numberType in new[]
                     {
                         VoucherType,
                         VoucherCardNumber
                     })
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
                "U8 自动生成盘点单号失败：" +
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
                    "U8 盘点单已生成（" + voucherCode +
                    "），但自动审核失败：" + error);
            }
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

        private static int? GetIntAttribute(
            IXMLDOMElement row,
            string name)
        {
            string value = Convert.ToString(
                row.getAttribute(name),
                CultureInfo.InvariantCulture);
            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int result)
                ? result
                : (int?)null;
        }

        private static decimal? GetDecimalAttribute(
            IXMLDOMElement row,
            string name)
        {
            string value = Convert.ToString(
                row.getAttribute(name),
                CultureInfo.InvariantCulture);
            return decimal.TryParse(
                value,
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out decimal result)
                ? result
                : (decimal?)null;
        }

        private static void SetDecimalAttribute(
            IXMLDOMElement row,
            string name,
            decimal value)
        {
            row.setAttribute(
                name,
                value.ToString(CultureInfo.InvariantCulture));
        }

        private static decimal? DifferenceNumber(
            InventoryCountVoucherLineRow line)
        {
            return line.ActualNumber.HasValue && line.BookNumber.HasValue
                ? line.ActualNumber.Value - line.BookNumber.Value
                : (decimal?)null;
        }

        private static GeneralInventoryCountDto Map(
            InventoryCountVoucherRow voucher,
            List<InventoryCountVoucherLineRow> lines,
            string database)
        {
            return new GeneralInventoryCountDto
            {
                Id = voucher.Id,
                VoucherCode = voucher.VoucherCode,
                VoucherDate = voucher.VoucherDate,
                CountDate = voucher.CountDate,
                WarehouseCode = voucher.WarehouseCode,
                DepartmentCode = voucher.DepartmentCode,
                InboundRdCode = voucher.InboundRdCode,
                OutboundRdCode = voucher.OutboundRdCode,
                Maker = voucher.Maker,
                Auditor = voucher.Auditor,
                AuditDate = voucher.AuditDate,
                CountType = voucher.CountType,
                Memo = voucher.Memo,
                IsVerified = !string.IsNullOrWhiteSpace(voucher.Auditor),
                U8Database = database,
                Lines = lines.Select(line => new GeneralInventoryCountLineDto
                {
                    AutoId = line.AutoId,
                    LineNo = line.LineNo,
                    InventoryCode = line.InventoryCode,
                    InventoryName = line.InventoryName,
                    Specification = line.Specification,
                    UnitName = line.UnitName,
                    BatchCode = line.BatchCode ?? "",
                    PositionCode = line.PositionCode ?? "",
                    PositionName = line.PositionName ?? "",
                    BookQuantity = line.BookQuantity,
                    ActualQuantity = line.ActualQuantity,
                    DifferenceQuantity = line.ActualQuantity -
                                         line.BookQuantity,
                    AuxiliaryUnitCode = line.AuxiliaryUnitCode,
                    ExchangeRate = line.ExchangeRate,
                    BookNumber = line.BookNumber,
                    ActualNumber = line.ActualNumber,
                    DifferenceNumber = DifferenceNumber(line),
                    AdjustInQuantity = line.AdjustInQuantity,
                    AdjustOutQuantity = line.AdjustOutQuantity,
                    Reason = line.Reason ?? "",
                    Remark = line.Remark ?? "",
                    LineVersion = line.LineVersion
                }).ToList()
            };
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

        private static DateTime ParseDate(string value, string field)
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
                    field + " 必须使用 yyyy-MM-dd 格式。");
            }
            return date;
        }

        private static void ValidateYear(DateTime date, ApiSettings settings)
        {
            if (!int.TryParse(settings.U8Year, out int year) || date.Year != year)
            {
                throw BadRequest(
                    "VOUCHER_DATE_YEAR_MISMATCH",
                    "单据日期和盘点日期年份必须与 U8 登录年度一致。");
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
                throw BadRequest("FIELD_REQUIRED", field + " 不能为空。");
            }
        }

        private static void ValidateRequiredFreeField(
            bool required,
            string value,
            int number,
            string inventoryCode)
        {
            if (required && string.IsNullOrWhiteSpace(value))
            {
                throw BadRequest(
                    "INVENTORY_FREE_FIELD_REQUIRED",
                    "存货 " + inventoryCode + " 启用了自由项" + number +
                    "管理，必须提供 freeFields.free" + number + "。");
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

        private static GeneralInventoryCountCreatedDto Result(
            string externalRequestNo,
            string voucherId,
            string voucherCode,
            DateTime createdAt,
            bool isVerified,
            bool replayed,
            string database)
        {
            return new GeneralInventoryCountCreatedDto
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

        private sealed class PreparedCount
        {
            public GeneralInventoryCountRequest Request { get; set; }
            public ApiSettings Settings { get; set; }
            public DateTime VoucherDate { get; set; }
            public DateTime CountDate { get; set; }
            public WarehouseRow Warehouse { get; set; }
            public string DepartmentCode { get; set; }
            public string InboundRdCode { get; set; }
            public string OutboundRdCode { get; set; }
            public string HeaderPositionCode { get; set; }
            public int TemplateVoucherId { get; set; }
            public List<PreparedLine> Lines { get; set; }
            public GeneralInventoryCountValidationDto Validation { get; set; }
        }

        private sealed class PreparedQuantityUpdate
        {
            public ApiSettings Settings { get; set; }
            public InventoryCountVoucherRow Voucher { get; set; }
            public List<PreparedQuantityChange> Changes { get; set; }
        }

        private static Dictionary<int, InventoryCountVoucherLineRow> IndexLines(
            List<InventoryCountVoucherLineRow> lines)
        {
            var groups = lines.Where(line => line.LineNo.HasValue)
                .GroupBy(line => line.LineNo.Value).ToList();
            if (groups.Any(group => group.Count() != 1))
                throw Conflict("AMBIGUOUS_LINE_NUMBER", "盘点单存在重复行号，请在 U8 中核对。");
            return groups.ToDictionary(group => group.Key, group => group.Single());
        }

        private sealed class PreparedQuantityChange : InventoryCountLineEdit
        {
            public decimal PreviousActualQuantity { get; set; }
            public decimal? PreviousActualNumber { get; set; }
        }

        private sealed class PreparedLine
        {
            public GeneralInventoryCountLineRequest Input { get; set; }
            public int LineNo { get; set; }
            public InventoryCountInventoryRow Inventory { get; set; }
            public string BatchCode { get; set; }
            public PositionRow Position { get; set; }
            public decimal CurrentBookQuantity { get; set; }
            public decimal BookQuantity { get; set; }
            public decimal ActualQuantity { get; set; }
            public string AuxiliaryUnitCode { get; set; }
            public decimal? ExchangeRate { get; set; }
            public decimal? CurrentBookNumber { get; set; }
            public decimal? BookNumber { get; set; }
            public decimal? ActualNumber { get; set; }
            public int? MassDate { get; set; }
            public string MassUnit { get; set; }
            public DateTime? ManufactureDate { get; set; }
            public DateTime? InvalidDate { get; set; }
            public int ExpirationDateCalculation { get; set; }
            public DateTime? ExpirationDate { get; set; }
            public string ExpirationDateText { get; set; }
            public decimal UnitPrice { get; set; }
        }
    }
}
