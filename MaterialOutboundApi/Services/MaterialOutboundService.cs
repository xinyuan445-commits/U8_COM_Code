using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;
using MaterialOutboundConsole.Models;
using MaterialOutboundConsole.Services;
using MSXML2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MaterialOutboundApi.Services
{
    internal sealed class MaterialOutboundService
    {
        private const string VoucherType = "11";
        private const string VoucherCardNumber = "0412";
        private readonly SettingsStore _settings;
        private readonly StateStore _state;
        private readonly U8OperationQueue _u8Queue;
        private readonly string _applicationDirectory;
        private readonly SemaphoreSlim _idempotencyGate =
            new SemaphoreSlim(1, 1);

        public MaterialOutboundService(
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

        public List<WorkOrderLineDto> SearchWorkOrderLines(
            string keyword,
            int? sortSeq,
            bool includeInactive = false)
        {
            EnsureConfigured();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                throw BadRequest("WORK_ORDER_REQUIRED", "工单号不能为空。");
            }
            if (sortSeq.HasValue && sortSeq.Value <= 0)
            {
                throw BadRequest(
                    "WORK_ORDER_LINE_INVALID",
                    "生产订单行号必须大于零。");
            }
            if (includeInactive && !sortSeq.HasValue)
            {
                throw BadRequest("WORK_ORDER_LINE_REQUIRED", "查询历史生产数量需要指定工单行号。");
            }
            var rows = Repository().SearchWorkOrderLines(
                keyword.Trim(),
                sortSeq,
                includeInactive);
            if (rows.Count == 0)
            {
                throw NotFound(
                    "WORK_ORDER_NOT_ELIGIBLE",
                    (includeInactive
                        ? "未找到生产记录："
                        : "未找到已审核、已释放且包含材料的生产记录：") + keyword);
            }
            return rows;
        }

        public TraceBackLabelDto GetTraceBackLabel(string traceBackCode)
        {
            return GetTraceBackLabels(new[] { traceBackCode }).Single();
        }

        public List<TraceBackLabelDto> GetTraceBackLabels(
            IEnumerable<string> traceBackCodes)
        {
            EnsureConfigured();
            List<string> normalizedCodes = (traceBackCodes ??
                    Enumerable.Empty<string>())
                .Select(NormalizeTraceBackCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedCodes.Count == 0)
            {
                throw BadRequest(
                    "TRACEBACK_CODES_REQUIRED",
                    "标签追溯码列表不能为空。");
            }
            if (normalizedCodes.Count > 500)
            {
                throw BadRequest(
                    "TRACEBACK_CODES_TOO_MANY",
                    "一次最多查询 500 张标签。");
            }

            Dictionary<string, TraceBackLabelRow> rows = Repository()
                .GetTraceBackLabels(normalizedCodes)
                .GroupBy(
                    row => (row.TraceBackCode ?? string.Empty).Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            var result = new List<TraceBackLabelDto>(normalizedCodes.Count);
            foreach (string normalized in normalizedCodes)
            {
                if (!rows.TryGetValue(normalized, out TraceBackLabelRow row))
                {
                    throw NotFound(
                        "TRACEBACK_LABEL_NOT_FOUND",
                        "TraceBack 表中不存在该标签：" + normalized);
                }
                result.Add(BuildTraceBackLabel(normalized, row));
            }
            return result;
        }

        private static TraceBackLabelDto BuildTraceBackLabel(
            string normalized,
            TraceBackLabelRow row)
        {
            if (string.IsNullOrWhiteSpace(row.GoodsJson))
            {
                throw NotFound(
                    "TRACEBACK_GOODS_NOT_FOUND",
                    "标签存在，但 TraceBackGoods 中没有货物信息：" +
                    normalized);
            }

            JObject goods;
            JObject order = null;
            try
            {
                goods = JObject.Parse(row.GoodsJson);
                if (!string.IsNullOrWhiteSpace(row.OrderJson))
                {
                    order = JObject.Parse(row.OrderJson);
                }
            }
            catch (JsonException exception)
            {
                throw new ApiException(
                    HttpStatusCode.Conflict,
                    "TRACEBACK_JSON_INVALID",
                    "TraceBack 货物信息不是有效 JSON：" + normalized,
                    exception);
            }

            string[] parts = normalized.Split('|');
            string materialCode = JsonText(goods, "code");
            string batchCode = JsonText(goods, "Attribute", "batchNumber");
            string quantityText = JsonText(goods, "quantity");
            string sn = row.Sn?.Trim();
            if (parts.Length == 5)
            {
                materialCode = FirstValue(materialCode, parts[1]);
                batchCode = FirstValue(batchCode, parts[2]);
                // 完整追溯码已在 TraceBackOnlyCode 精确命中；优先使用码内数量，
                // 兼容旧 Goods.json 中带单位或使用非标准格式的 quantity。
                quantityText = FirstValue(parts[3], quantityText, row.QuantityText);
                sn = FirstValue(sn, parts[4]);
            }
            else
            {
                quantityText = FirstValue(quantityText, row.QuantityText);
            }

            if (!decimal.TryParse(
                    quantityText,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out decimal quantity) ||
                quantity <= 0)
            {
                throw new ApiException(
                    HttpStatusCode.Conflict,
                    "TRACEBACK_QUANTITY_INVALID",
                    "TraceBack 标签数量无效：" + normalized);
            }
            if (!TryTraceBackQuantity(
                    row.QuantityText,
                    out decimal remainingQuantity) ||
                remainingQuantity < 0)
            {
                throw new ApiException(
                    HttpStatusCode.Conflict,
                    "TRACEBACK_REMAINING_INVALID",
                    "TraceBackOnlyCode.sum 剩余数量无效：" + normalized);
            }

            return new TraceBackLabelDto
            {
                TraceBackOnlyCodeId = row.TraceBackOnlyCodeId,
                TraceBackCode = row.TraceBackCode?.Trim(),
                Sn = sn,
                PdaOrderUuid = row.PdaOrderUuid?.Trim(),
                PdaGoodsUuid = row.PdaGoodsUuid?.Trim(),
                MaterialCode = materialCode,
                MaterialName = JsonText(goods, "name"),
                BatchCode = batchCode,
                Quantity = quantity,
                RemainingQuantity = remainingQuantity,
                WarehouseCode = JsonText(goods, "warehouseCode"),
                WarehouseName = JsonText(goods, "inventory", "warehouse"),
                PositionCode = JsonText(
                    goods,
                    "inventory",
                    "storageLocationCode"),
                PositionName = JsonText(
                    goods,
                    "inventory",
                    "storageLocation"),
                OrderNumber = JsonText(order, "documentNumber"),
                OrderType = JsonText(order, "orderType")
            };
        }

        private static string NormalizeTraceBackCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string source = value.Normalize(NormalizationForm.FormKC);
            var builder = new StringBuilder(source.Length);
            foreach (char character in source)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (char.IsControl(character) || category == UnicodeCategory.Format)
                {
                    continue;
                }
                builder.Append(character == '¦' ? '|' : character);
            }
            string normalized = builder.ToString().Trim();
            Match marker = Regex.Match(normalized, @"\d{3}\|");
            if (marker.Success && marker.Index > 0)
            {
                normalized = normalized.Substring(marker.Index);
            }
            string[] parts = normalized.Split('|')
                .Select(part => part.Trim())
                .ToArray();
            if (parts.Length >= 5 && Regex.IsMatch(parts[0], @"^\d{3}$"))
            {
                return string.Join("|", parts.Take(5));
            }
            return normalized;
        }

        public PreviewDto Preview(PreviewRequest request)
        {
            EnsureConfigured();
            DateTime date = ParseDate(request?.VoucherDate, "voucherDate");
            ValidateYear(date);
            if (request?.SelectedMoDIds == null ||
                request.SelectedMoDIds.Count == 0)
            {
                throw BadRequest(
                    "WORK_ORDER_LINES_REQUIRED",
                    "至少选择一条工单记录。");
            }

            PreviewDto preview = BuildPreview(
                request.WorkOrderCode,
                request.SelectedMoDIds.Distinct().ToList(),
                date);
            preview.PreviewToken = Guid.NewGuid().ToString("N");
            preview.ExpiresAt = DateTime.Now.AddMinutes(15);
            _state.SavePreview(
                preview,
                request.SelectedMoDIds.Distinct().ToList());
            return preview;
        }

        public async Task<MaterialOutboundCreatedDto> Create(
            CreateMaterialOutboundRequest request,
            string idempotencyKey)
        {
            EnsureConfigured();
            ValidateIdempotencyKey(idempotencyKey);
            if (request == null || string.IsNullOrWhiteSpace(request.PreviewToken))
            {
                throw BadRequest("PREVIEW_TOKEN_REQUIRED", "previewToken 不能为空。");
            }
            await _idempotencyGate.WaitAsync();
            try
            {
                string hash = Hash(JsonConvert.SerializeObject(new
                {
                    request.VoucherCode,
                    request.Lines
                }));
                string recordId = "create:" + idempotencyKey;
                IdempotencyRecord existing = _state.GetIdempotency(recordId);
                MaterialOutboundCreatedDto replay =
                    TryReplayCreate(existing, hash);
                if (replay != null)
                {
                    return replay;
                }
                if (existing != null &&
                    (!string.IsNullOrWhiteSpace(existing.VoucherId) ||
                     !string.IsNullOrWhiteSpace(existing.VoucherCode)))
                {
                    MaterialOutboundCreatedDto resumed = await _u8Queue.Execute(
                        () => ResumeCreatedVoucherOnSta(existing));
                    if (resumed != null)
                    {
                        existing.Status = "Completed";
                        existing.VoucherId = resumed.VoucherId;
                        existing.VoucherCode = resumed.VoucherCode;
                        existing.ResultJson = JsonConvert.SerializeObject(resumed);
                        _state.UpsertIdempotency(existing);
                        return resumed;
                    }
                    // 保存过候选单号但 U8 中没有实际单据时，清除后重新取号。
                    existing.VoucherCode = null;
                    existing.VoucherId = null;
                    _state.UpsertIdempotency(existing);
                }
                PreviewRecord previewRecord =
                    _state.GetPreview(request.PreviewToken.Trim());
                if (previewRecord == null)
                {
                    throw new ApiException(
                        HttpStatusCode.Conflict,
                        "PREVIEW_EXPIRED",
                        "预览不存在或已经过期，请重新预览。");
                }

                var pending = existing ?? new IdempotencyRecord
                {
                    Id = recordId,
                    PayloadHash = hash,
                    Status = "Pending"
                };
                pending.PayloadHash = hash;
                pending.Status = "Pending";
                _state.UpsertIdempotency(pending);

                try
                {
                    string requestId = RequestContext.Id;
                    MaterialOutboundCreatedDto result = await _u8Queue.Execute(() =>
                        CreateOnSta(
                            request,
                            previewRecord,
                            pending,
                            requestId));
                    pending.Status = "Completed";
                    pending.VoucherCode = result.VoucherCode;
                    pending.VoucherId = result.VoucherId;
                    pending.ResultJson = JsonConvert.SerializeObject(result);
                    _state.UpsertIdempotency(pending);
                    return result;
                }
                catch
                {
                    pending.Status = "Failed";
                    _state.UpsertIdempotency(pending);
                    throw;
                }
            }
            finally
            {
                _idempotencyGate.Release();
            }
        }

        public MaterialOutboundDto Query(string voucherCode)
        {
            EnsureConfigured();
            MaterialOutboundDto value =
                Repository().GetMaterialOutbound(RequiredCode(voucherCode));
            if (value == null)
            {
                throw NotFound(
                    "VOUCHER_NOT_FOUND",
                    "未找到材料出库单：" + voucherCode);
            }
            return value;
        }

        public Task<OperationResultDto> Verify(
            string voucherCode,
            OperationRequest request,
            string idempotencyKey)
        {
            return ExecuteAction(
                voucherCode,
                request,
                idempotencyKey,
                "verify",
                (USERPCO.VoucherCO voucher, object id, ref string error) =>
                    voucher.Verify(VoucherType, id, ref error));
        }

        public Task<OperationResultDto> Unverify(
            string voucherCode,
            OperationRequest request,
            string idempotencyKey)
        {
            return ExecuteAction(
                voucherCode,
                request,
                idempotencyKey,
                "unverify",
                (USERPCO.VoucherCO voucher, object id, ref string error) =>
                    voucher.UnVerify(VoucherType, id, ref error));
        }

        public void TestSql(ApiSettings settings)
        {
            new ApiRepository(settings.BuildConnectionString()).TestConnection();
        }

        public List<string> GetAccountDatabases(ApiSettings settings)
        {
            if (settings == null ||
                string.IsNullOrWhiteSpace(settings.SqlServer) ||
                string.IsNullOrWhiteSpace(settings.SqlUser) ||
                string.IsNullOrWhiteSpace(settings.SqlPassword))
            {
                throw BadRequest(
                    "SQL_LOGIN_REQUIRED",
                    "请先填写 SQL Server、用户名和密码。");
            }
            return new ApiRepository(
                    settings.BuildConnectionString("master"))
                .GetAccountDatabases();
        }

        public Task<bool> TestU8(ApiSettings settings, DateTime testDate)
        {
            return _u8Queue.Execute(() =>
            {
                using (var session = OpenSession(settings, testDate))
                {
                    return session.Voucher != null;
                }
            });
        }

        private MaterialOutboundCreatedDto CreateOnSta(
            CreateMaterialOutboundRequest request,
            PreviewRecord previewRecord,
            IdempotencyRecord idempotency,
            string requestId)
        {
            Stopwatch total = Stopwatch.StartNew();
            ApiSettings settings = RequiredSettings();
            MaterialOutboundDto existingVoucher = FindCreatedVoucher(idempotency);
            if (existingVoucher != null)
            {
                using (U8CoSession existingSession = OpenSession(
                           settings,
                           previewRecord.VoucherDate))
                {
                    EnsureVoucherVerified(
                        existingSession,
                        existingVoucher.Id,
                        existingVoucher.VoucherCode,
                        existingVoucher.IsVerified);
                }
                idempotency.VoucherId = existingVoucher.Id.ToString(
                    CultureInfo.InvariantCulture);
                idempotency.VoucherCode = existingVoucher.VoucherCode;
                _state.UpsertIdempotency(idempotency);
                return new MaterialOutboundCreatedDto
                {
                    VoucherId = idempotency.VoucherId,
                    VoucherCode = existingVoucher.VoucherCode,
                    CreatedAt = idempotency.UpdatedAt,
                    IsVerified = true,
                    Replayed = true
                };
            }
            PreviewDto current = BuildPreview(
                previewRecord.WorkOrderCode,
                previewRecord.SelectedMoDIds,
                previewRecord.VoucherDate);
            var allocations = Repository().GetAllocations(
                previewRecord.WorkOrderCode,
                previewRecord.SelectedMoDIds);
            ValidateCreateLines(request.Lines, current);

            WarehouseRow warehouse = Repository().GetWarehouse(
                settings.WarehouseCode);
            if (warehouse == null)
            {
                throw BadRequest(
                    "WAREHOUSE_NOT_FOUND",
                    "U8 中不存在仓库：" + settings.WarehouseCode);
            }
            PositionRow position = null;
            if (warehouse.UsesPosition)
            {
                position = Repository().GetPosition(
                    settings.WarehouseCode,
                    settings.PositionCode);
                if (position == null)
                {
                    throw BadRequest(
                        "POSITION_NOT_FOUND",
                        "仓库中不存在货位：" + settings.PositionCode);
                }
            }

            var departments = allocations
                .Select(x => x.DepartmentCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (departments.Count > 1)
            {
                throw BadRequest(
                    "MULTIPLE_DEPARTMENTS",
                    "所选工单记录属于多个部门，请拆单。");
            }
            string department = departments.Count == 1
                ? departments[0]
                : settings.DefaultDepartmentCode;
            if (string.IsNullOrWhiteSpace(department))
            {
                throw BadRequest(
                    "DEPARTMENT_REQUIRED",
                    "工单部门为空且没有配置默认部门。");
            }

            AllocationRow first = allocations.First();
            var head = new MaterialOutboundHead
            {
                ddate = previewRecord.VoucherDate,
                ccode = request.VoucherCode?.Trim(),
                cpspcode = allocations
                    .Select(x => x.ProductCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == 1
                        ? first.ProductCode
                        : null,
                cmpocode = first.MoCode,
                iproorderid = first.MoId,
                cmaker = settings.U8UserId,
                cwhcode = settings.WarehouseCode,
                cdepcode = department,
                crdcode = settings.RdCode,
                vt_id = settings.VoucherTemplateId
            };

            var allocationById = allocations.ToDictionary(x => x.AllocateId);
            ValidateAvailableStock(
                request.Lines,
                allocationById,
                settings.WarehouseCode);
            List<InboundUnitCostKey> inboundCostKeys = request.Lines
                .Select(input =>
                {
                    AllocationRow row = allocationById[input.AllocateId];
                    return new InboundUnitCostKey
                    {
                        MaterialCode = row.MaterialCode,
                        BatchCode = row.RequiresBatch
                            ? (input.BatchCode ?? string.Empty).Trim()
                            : string.Empty
                    };
                })
                .GroupBy(
                    key => InboundCostLookupKey(
                        key.MaterialCode,
                        key.BatchCode),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            Dictionary<string, decimal> inboundUnitCosts = Repository()
                .GetLatestInboundUnitCosts(inboundCostKeys)
                .ToDictionary(
                    row => InboundCostLookupKey(
                        row.MaterialCode,
                        row.BatchCode),
                    row => row.UnitCost,
                    StringComparer.OrdinalIgnoreCase);
            var body = request.Lines
                .Select((input, index) =>
                {
                    AllocationRow row = allocationById[input.AllocateId];
                    string batchCode = row.RequiresBatch
                        ? (input.BatchCode ?? string.Empty).Trim()
                        : string.Empty;
                    decimal unitCost;
                    if (!inboundUnitCosts.TryGetValue(
                            InboundCostLookupKey(row.MaterialCode, batchCode),
                            out unitCost))
                    {
                        unitCost = 0m;
                    }
                    return new MaterialOutboundBody
                    {
                        irowno = index + 1,
                        editprop = "A",
                        invcode = row.ProductCode,
                        cinvcode = row.MaterialCode,
                        iquantity = input.Quantity,
                        inquantity = input.Quantity,
                        iunitcost = unitCost,
                        iprice = input.Quantity * unitCost,
                        cbatch = row.RequiresBatch
                            ? batchCode
                            : null,
                        impoids = row.AllocateId,
                        cmocode = row.MoCode,
                        imoseq = row.SortSeq,
                        iopseq = row.OpSeq,
                        cmworkcentercode =
                            string.IsNullOrWhiteSpace(settings.WorkCenterCode)
                                ? null
                                : settings.WorkCenterCode,
                        cposition = position?.Code,
                        cposname = position == null
                            ? null
                            : settings.PositionCode,
                        iposflag = position == null ? (int?)null : 1,
                        cbmemo = string.IsNullOrWhiteSpace(input.Remark)
                            ? null
                            : input.Remark.Trim()
                    };
                }).ToList();

            var builder = new VoucherXmlBuilder(_applicationDirectory);
            VoucherXml xml = builder.Build(head, body);
            long prepareMs = total.ElapsedMilliseconds;
            long openSessionStarted = total.ElapsedMilliseconds;
            using (U8CoSession session = OpenSession(
                       settings,
                       previewRecord.VoucherDate))
            {
                long openSessionMs =
                    total.ElapsedMilliseconds - openSessionStarted;
                IXMLDOMDocument2 bodyDom = CreateDocument(xml.Body, "表体");
                IXMLDOMDocument2 positionDom = new DOMDocument26Class();
                string error = string.Empty;
                string voucherId = string.Empty;
                string voucherCode = request.VoucherCode?.Trim();
                IXMLDOMDocument2 headDom;
                long voucherCodeMs = 0;

                if (string.IsNullOrWhiteSpace(voucherCode))
                {
                    long voucherCodeStarted = total.ElapsedMilliseconds;
                    voucherCode = GetNewVoucherCode(session, xml.Head);
                    voucherCodeMs =
                        total.ElapsedMilliseconds - voucherCodeStarted;
                    headDom = CreateDocument(xml.Head, "表头");
                    var row = headDom.selectSingleNode("//z:row")
                              as IXMLDOMElement;
                    if (row == null)
                    {
                        throw BadRequest(
                            "INVALID_U8_XML",
                            "表头 XML 中找不到 z:row 节点。");
                    }
                    row.setAttribute("ccode", voucherCode);
                    // U8 CO 自动取号可能修改传入的 DOM；使用全新 DOM 提交。
                    headDom = CreateDocument(headDom.xml, "表头");
                }
                else
                {
                    headDom = CreateDocument(xml.Head, "表头");
                }

                idempotency.VoucherCode = voucherCode;
                _state.UpsertIdempotency(idempotency);

                long insertStarted = total.ElapsedMilliseconds;
                ADODB.Connection connection = null;
                bool succeeded = session.Voucher.Insert(
                    VoucherType,
                    headDom,
                    bodyDom,
                    positionDom,
                    ref error,
                    ref connection,
                    ref voucherId);
                long insertMs = total.ElapsedMilliseconds - insertStarted;
                return CompleteCreatedVoucher(
                    session,
                    idempotency,
                    voucherId,
                    voucherCode,
                    succeeded,
                    error,
                    requestId,
                    request.Lines.Count,
                    total,
                    prepareMs,
                    openSessionMs,
                    voucherCodeMs,
                    insertMs);
            }
        }

        private MaterialOutboundCreatedDto CompleteCreatedVoucher(
            U8CoSession session,
            IdempotencyRecord idempotency,
            string voucherId,
            string voucherCode,
            bool succeeded,
            string error,
            string requestId,
            int lineCount,
            Stopwatch total,
            long prepareMs,
            long openSessionMs,
            long voucherCodeMs,
            long insertMs)
        {
            if (!succeeded)
            {
                string detail = string.IsNullOrWhiteSpace(error)
                    ? "U8 未返回错误文本；尝试单号=" + voucherCode
                    : error.Trim();
                throw new ApiException(
                    (HttpStatusCode)422,
                    "U8_INSERT_FAILED",
                    "U8 新增材料出库单失败：" + detail);
            }
            idempotency.VoucherId = voucherId;
            _state.UpsertIdempotency(idempotency);
            if (!int.TryParse(
                voucherId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int createdVoucherId))
            {
                throw new ApiException(
                    HttpStatusCode.BadGateway,
                    "U8_VOUCHER_ID_INVALID",
                    "U8 已新增材料出库单，但没有返回有效的单据 ID。");
            }
            long lookupStarted = total.ElapsedMilliseconds;
            voucherCode = GetActualVoucherCodeById(createdVoucherId);
            long lookupMs = total.ElapsedMilliseconds - lookupStarted;
            idempotency.VoucherCode = voucherCode;
            idempotency.VoucherId = voucherId;
            _state.UpsertIdempotency(idempotency);
            long verifyStarted = total.ElapsedMilliseconds;
            EnsureVoucherVerified(
                session,
                createdVoucherId,
                voucherCode,
                false);
            long verifyMs = total.ElapsedMilliseconds - verifyStarted;
            ApiPerformanceLog.Write(
                requestId,
                "material-outbound-create",
                total.ElapsedMilliseconds,
                "lines=" + lineCount +
                " voucherCode=" + voucherCode +
                " prepareMs=" + prepareMs +
                " openSessionMs=" + openSessionMs +
                " voucherCodeMs=" + voucherCodeMs +
                " insertMs=" + insertMs +
                " lookupMs=" + lookupMs +
                " verifyMs=" + verifyMs);
            return new MaterialOutboundCreatedDto
            {
                VoucherId = voucherId,
                VoucherCode = voucherCode,
                CreatedAt = DateTime.Now,
                IsVerified = true
            };
        }

        private void ValidateAvailableStock(
            IEnumerable<CreateMaterialOutboundLineRequest> lines,
            IDictionary<int, AllocationRow> allocationById,
            string warehouseCode)
        {
            var requirements = lines
                .Select(line => new
                {
                    Allocation = allocationById[line.AllocateId],
                    BatchCode = allocationById[line.AllocateId].RequiresBatch
                        ? (line.BatchCode ?? string.Empty).Trim()
                        : string.Empty,
                    line.Quantity
                })
                .GroupBy(
                    x => new
                    {
                        MaterialCode = (x.Allocation.MaterialCode ?? string.Empty)
                            .ToUpperInvariant(),
                        BatchCode = (x.BatchCode ?? string.Empty)
                            .ToUpperInvariant()
                    })
                .Select(group => new
                {
                    group.Key.MaterialCode,
                    group.Key.BatchCode,
                    RequiredQuantity = group.Sum(x => x.Quantity)
                });

            ApiRepository repository = Repository();
            foreach (var requirement in requirements)
            {
                decimal availableQuantity = repository.GetAvailableStock(
                    warehouseCode,
                    requirement.MaterialCode,
                    requirement.BatchCode);
                if (availableQuantity + 0.000001m >= requirement.RequiredQuantity)
                {
                    continue;
                }

                string batchText = string.IsNullOrWhiteSpace(requirement.BatchCode)
                    ? "无批号"
                    : requirement.BatchCode;
                throw new ApiException(
                    (HttpStatusCode)422,
                    "U8_STOCK_INSUFFICIENT",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "U8 实际库存不足：存货 {0}，批号 {1}，实际库存 {2:0.######}，本次要出库 {3:0.######}。",
                        requirement.MaterialCode,
                        batchText,
                        availableQuantity,
                        requirement.RequiredQuantity));
            }
        }

        private static string GetNewVoucherCode(
            U8CoSession session,
            string headXml)
        {
            const int attemptsPerType = 2;
            string[] numberTypes = { VoucherType, VoucherCardNumber };
            string lastError = string.Empty;
            bool lastValidate = false;
            bool lastPhase = false;

            foreach (string numberType in numberTypes)
            {
                for (int attempt = 1; attempt <= attemptsPerType; attempt++)
                {
                    IXMLDOMDocument2 numberHead =
                        CreateDocument(headXml, "自动取号表头");
                    string error = string.Empty;
                    bool validate = false;
                    bool phase = false;
                    string code = null;
                    try
                    {
                        code = session.Voucher.GetNewVouchCode(
                            numberType,
                            numberHead,
                            ref error,
                            ref validate,
                            ref phase);
                    }
                    catch (COMException exception)
                    {
                        error = "COM HRESULT=0x" +
                                exception.ErrorCode.ToString("X8") +
                                "，" + exception.Message;
                    }

                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        return code.Trim();
                    }

                    lastError = "numberType=" + numberType + "，" + error;
                    lastValidate = validate;
                    lastPhase = phase;
                    if (attempt < attemptsPerType)
                    {
                        Thread.Sleep(300);
                    }
                }
            }

            string detail = string.IsNullOrWhiteSpace(lastError)
                ? "U8 未返回错误文本"
                : lastError.Trim();
            throw new ApiException(
                HttpStatusCode.ServiceUnavailable,
                "U8_VOUCHER_NUMBER_FAILED",
                "U8 自动生成材料出库单号失败：" + detail +
                "；validate=" + lastValidate +
                "，phase=" + lastPhase +
                "，numberTypes=11/0412" +
                "，attemptsPerType=" + attemptsPerType + "。请稍后重试。");
        }

        private MaterialOutboundDto FindCreatedVoucher(
            IdempotencyRecord idempotency)
        {
            if (idempotency == null)
            {
                return null;
            }
            if (int.TryParse(
                    idempotency.VoucherId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int voucherId))
            {
                string actualCode = Repository().GetVoucherCodeById(voucherId);
                if (!string.IsNullOrWhiteSpace(actualCode))
                {
                    MaterialOutboundDto byId = Repository()
                        .GetMaterialOutbound(actualCode);
                    if (byId != null)
                    {
                        return byId;
                    }
                }
            }
            return string.IsNullOrWhiteSpace(idempotency.VoucherCode)
                ? null
                : Repository().GetMaterialOutbound(idempotency.VoucherCode);
        }

        private MaterialOutboundCreatedDto ResumeCreatedVoucherOnSta(
            IdempotencyRecord idempotency)
        {
            MaterialOutboundDto voucher = FindCreatedVoucher(idempotency);
            if (voucher == null)
            {
                return null;
            }
            using (U8CoSession session = OpenSession(
                       RequiredSettings(),
                       voucher.VoucherDate))
            {
                EnsureVoucherVerified(
                    session,
                    voucher.Id,
                    voucher.VoucherCode,
                    voucher.IsVerified);
            }
            return new MaterialOutboundCreatedDto
            {
                VoucherId = voucher.Id.ToString(CultureInfo.InvariantCulture),
                VoucherCode = voucher.VoucherCode,
                CreatedAt = idempotency.UpdatedAt,
                IsVerified = true,
                Replayed = true
            };
        }

        private static void EnsureVoucherVerified(
            U8CoSession session,
            int voucherId,
            string voucherCode,
            bool alreadyVerified)
        {
            if (alreadyVerified)
            {
                return;
            }
            string error = string.Empty;
            object idValue = voucherId;
            if (!session.Voucher.Verify(VoucherType, idValue, ref error))
            {
                throw new ApiException(
                    (HttpStatusCode)422,
                    "U8_AUTO_VERIFY_FAILED",
                    "U8 材料出库单已生成（" + voucherCode +
                    "），但自动审核失败：" + error);
            }
        }

        private string GetActualVoucherCodeById(int voucherId)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string actualVoucherCode = Repository()
                    .GetVoucherCodeById(voucherId);
                if (!string.IsNullOrWhiteSpace(actualVoucherCode))
                {
                    return actualVoucherCode.Trim();
                }
                Thread.Sleep(100);
            }
            throw new ApiException(
                HttpStatusCode.BadGateway,
                "U8_VOUCHER_CODE_LOOKUP_FAILED",
                "U8 已新增材料出库单，但无法根据 ID 查询实际单据号。");
        }

        private async Task<OperationResultDto> ExecuteAction(
            string voucherCode,
            OperationRequest request,
            string idempotencyKey,
            string actionName,
            VoucherAction action)
        {
            EnsureConfigured();
            voucherCode = RequiredCode(voucherCode);
            ValidateIdempotencyKey(idempotencyKey);
            DateTime date = ParseDate(request?.OperationDate, "operationDate");
            ValidateYear(date);
            await _idempotencyGate.WaitAsync();
            try
            {
                string payloadHash = Hash(
                    voucherCode + "|" + date.ToString("yyyy-MM-dd"));
                string id = actionName + ":" + idempotencyKey;
                IdempotencyRecord existing = _state.GetIdempotency(id);
                if (existing != null)
                {
                    if (existing.PayloadHash != payloadHash)
                    {
                        throw new ApiException(
                            HttpStatusCode.Conflict,
                            "IDEMPOTENCY_KEY_REUSED",
                            "该 Idempotency-Key 已用于不同请求。");
                    }
                    if (existing.Status == "Completed")
                    {
                        return new OperationResultDto
                        {
                            VoucherCode = voucherCode,
                            Action = actionName,
                            Replayed = true
                        };
                    }
                }

                var record = existing ?? new IdempotencyRecord
                {
                    Id = id,
                    PayloadHash = payloadHash
                };
                record.Status = "Pending";
                _state.UpsertIdempotency(record);
                try
                {
                    OperationResultDto result = await _u8Queue.Execute(() =>
                    {
                        int? voucherId = Repository().GetVoucherId(voucherCode);
                        if (!voucherId.HasValue)
                        {
                            throw NotFound(
                                "VOUCHER_NOT_FOUND",
                                "未找到材料出库单：" + voucherCode);
                        }
                        using (U8CoSession session = OpenSession(
                                   RequiredSettings(),
                                   date))
                        {
                            string error = string.Empty;
                            object idValue = voucherId.Value;
                            if (!action(session.Voucher, idValue, ref error))
                            {
                                throw new ApiException(
                                    (HttpStatusCode)422,
                                    "U8_" + actionName.ToUpperInvariant() + "_FAILED",
                                    "U8 操作失败：" + error);
                            }
                        }
                        return new OperationResultDto
                        {
                            VoucherCode = voucherCode,
                            Action = actionName
                        };
                    });
                    record.Status = "Completed";
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

        private PreviewDto BuildPreview(
            string workOrderCode,
            List<int> selectedMoDIds,
            DateTime date)
        {
            if (string.IsNullOrWhiteSpace(workOrderCode))
            {
                throw BadRequest("WORK_ORDER_REQUIRED", "工单号不能为空。");
            }
            ApiSettings settings = RequiredSettings();
            var repository = Repository();
            List<AllocationRow> allocations = repository.GetAllocations(
                workOrderCode.Trim(),
                selectedMoDIds);
            if (allocations.Count == 0)
            {
                throw NotFound(
                    "MATERIALS_NOT_FOUND",
                    "工单不存在或所选记录没有子件用料。");
            }
            List<int> missingLineIds = selectedMoDIds
                .Except(allocations.Select(x => x.MoDId))
                .ToList();
            if (missingLineIds.Count > 0)
            {
                throw BadRequest(
                    "WORK_ORDER_LINE_NOT_FOUND",
                    "以下 moDId 不属于该工单或没有子件：" +
                    string.Join(",", missingLineIds));
            }
            var departments = allocations
                .Select(x => x.DepartmentCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string department = departments.Count == 1
                ? departments[0]
                : null;
            if (departments.Count == 0 &&
                !string.IsNullOrWhiteSpace(settings.DefaultDepartmentCode))
            {
                department = settings.DefaultDepartmentCode;
            }
            if (departments.Count == 0 && string.IsNullOrWhiteSpace(department))
            {
                throw BadRequest(
                    "DEPARTMENT_REQUIRED",
                    "工单部门为空且没有配置默认部门。");
            }

            WarehouseRow warehouse = repository.GetWarehouse(
                settings.WarehouseCode);
            if (warehouse == null)
            {
                throw BadRequest(
                    "WAREHOUSE_NOT_FOUND",
                    "U8 中不存在仓库：" + settings.WarehouseCode);
            }
            PositionRow position = null;
            if (warehouse.UsesPosition)
            {
                position = repository.GetPosition(
                    settings.WarehouseCode,
                    settings.PositionCode);
                if (position == null)
                {
                    throw BadRequest(
                        "POSITION_NOT_FOUND",
                        "仓库中不存在货位：" + settings.PositionCode);
                }
            }

            return new PreviewDto
            {
                WorkOrderCode = workOrderCode.Trim(),
                VoucherDate = date,
                DepartmentCode = department,
                WarehouseCode = warehouse.Code,
                PositionCode = position?.Code,
                PositionName = position == null
                    ? null
                    : settings.PositionCode,
                Lines = allocations.Select(x =>
                {
                    decimal planned = Decimal.Round(
                        x.PlannedQuantity,
                        6,
                        MidpointRounding.AwayFromZero);
                    decimal remaining = Math.Max(0, planned - x.IssuedQuantity);
                    return new PreviewLineDto
                    {
                        AllocateId = x.AllocateId,
                        MoDId = x.MoDId,
                        WorkOrderLineNumber = x.SortSeq,
                        ProductCode = x.ProductCode,
                        ProductName = x.ProductName,
                        MaterialCode = x.MaterialCode,
                        MaterialName = x.MaterialName,
                        DepartmentCode = x.DepartmentCode,
                        OpSeq = x.OpSeq,
                        RequiresBatch = x.RequiresBatch,
                        PlannedQuantity = planned,
                        IssuedQuantity = x.IssuedQuantity,
                        RemainingQuantity = remaining,
                        SuggestedQuantity = remaining
                    };
                }).ToList()
            };
        }

        private static void ValidateCreateLines(
            List<CreateMaterialOutboundLineRequest> inputs,
            PreviewDto preview)
        {
            if (inputs == null || inputs.Count == 0)
            {
                throw BadRequest(
                    "MATERIAL_LINES_REQUIRED",
                    "至少提交一条材料明细。");
            }
            var previewById = preview.Lines.ToDictionary(x => x.AllocateId);
            foreach (CreateMaterialOutboundLineRequest input in inputs)
            {
                if (!previewById.TryGetValue(
                        input.AllocateId,
                        out PreviewLineDto line))
                {
                    throw BadRequest(
                        "ALLOCATE_ID_NOT_IN_PREVIEW",
                        "allocateId 不属于当前预览：" + input.AllocateId);
                }
                if (input.Quantity <= 0)
                {
                    throw new ApiException(
                        HttpStatusCode.Conflict,
                        "QUANTITY_EXCEEDS_REMAINING",
                        "材料 " + line.MaterialCode +
                        " 的数量必须大于零。");
                }
                if (line.RequiresBatch &&
                    string.IsNullOrWhiteSpace(input.BatchCode))
                {
                    throw BadRequest(
                        "BATCH_REQUIRED",
                        "批次管理材料 " + line.MaterialCode + " 必须填写批号。");
                }
            }
        }

        private MaterialOutboundCreatedDto TryReplayCreate(
            IdempotencyRecord existing,
            string payloadHash)
        {
            if (existing == null)
            {
                return null;
            }
            if (existing.PayloadHash != payloadHash)
            {
                throw new ApiException(
                    HttpStatusCode.Conflict,
                    "IDEMPOTENCY_KEY_REUSED",
                    "该 Idempotency-Key 已用于不同请求。");
            }
            if (existing.Status == "Completed" &&
                !string.IsNullOrWhiteSpace(existing.ResultJson))
            {
                var result =
                    JsonConvert.DeserializeObject<MaterialOutboundCreatedDto>(
                        existing.ResultJson);
                int completedVoucherId;
                if (result == null ||
                    !int.TryParse(
                        result.VoucherId,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out completedVoucherId))
                {
                    throw new ApiException(
                        HttpStatusCode.BadGateway,
                        "U8_VOUCHER_ID_INVALID",
                        "U8 已返回成功，但没有保存有效的材料出库单 ID。");
                }
                result.VoucherCode =
                    GetActualVoucherCodeById(completedVoucherId);
                MaterialOutboundDto completed = Repository()
                    .GetMaterialOutbound(result.VoucherCode);
                if (completed == null || !completed.IsVerified)
                {
                    return null;
                }
                result.IsVerified = true;
                result.Replayed = true;
                existing.VoucherId = result.VoucherId;
                existing.VoucherCode = result.VoucherCode;
                existing.ResultJson = JsonConvert.SerializeObject(result);
                _state.UpsertIdempotency(existing);
                return result;
            }
            if (!string.IsNullOrWhiteSpace(existing.VoucherCode))
            {
                MaterialOutboundDto found = Repository()
                    .GetMaterialOutbound(existing.VoucherCode);
                if (found == null &&
                    int.TryParse(
                        existing.VoucherId,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int existingVoucherId))
                {
                    string actualVoucherCode = Repository()
                        .GetVoucherCodeById(existingVoucherId);
                    if (!string.IsNullOrWhiteSpace(actualVoucherCode))
                    {
                        found = Repository()
                            .GetMaterialOutbound(actualVoucherCode);
                        existing.VoucherCode = actualVoucherCode;
                    }
                }
                if (found != null)
                {
                    if (!found.IsVerified)
                    {
                        return null;
                    }
                    var result = new MaterialOutboundCreatedDto
                    {
                        VoucherId = found.Id.ToString(CultureInfo.InvariantCulture),
                        VoucherCode = found.VoucherCode,
                        CreatedAt = existing.UpdatedAt,
                        IsVerified = true,
                        Replayed = true
                    };
                    existing.Status = "Completed";
                    existing.ResultJson = JsonConvert.SerializeObject(result);
                    _state.UpsertIdempotency(existing);
                    return result;
                }
            }
            return null;
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
                string message = exception.Message ?? string.Empty;
                string code = message.IndexOf(
                                  "已登录",
                                  StringComparison.OrdinalIgnoreCase) >= 0 ||
                              message.IndexOf(
                                  "占用",
                                  StringComparison.OrdinalIgnoreCase) >= 0
                    ? "U8_ACCOUNT_IN_USE"
                    : "U8_LOGIN_FAILED";
                throw new ApiException(
                    HttpStatusCode.ServiceUnavailable,
                    code,
                    "U8 登录失败，请确认 yx 未在其他位置登录且账号配置正确。",
                    exception);
            }
        }

        private static string InboundCostLookupKey(
            string materialCode,
            string batchCode) =>
            (materialCode ?? string.Empty).Trim() + "\u001f" +
            (batchCode ?? string.Empty).Trim();

        private ApiRepository Repository() =>
            new ApiRepository(RequiredSettings().BuildConnectionString());

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

        private void ValidateYear(DateTime date)
        {
            if (!int.TryParse(RequiredSettings().U8Year, out int year) ||
                date.Year != year)
            {
                throw BadRequest(
                    "VOUCHER_DATE_YEAR_MISMATCH",
                    "单据日期年份必须与 U8 登录年度一致。");
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

        private static string RequiredCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw BadRequest("VOUCHER_CODE_REQUIRED", "单据号不能为空。");
            }
            return value.Trim();
        }

        internal static void ValidateIdempotencyKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length < 8 ||
                value.Length > 64)
            {
                throw BadRequest(
                    "IDEMPOTENCY_KEY_REQUIRED",
                    "Idempotency-Key 必须为 8 到 64 个字符。");
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

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
            }
        }

        private static string JsonText(JObject source, params string[] path)
        {
            JToken current = source;
            foreach (string name in path)
            {
                if (!(current is JObject value))
                {
                    return string.Empty;
                }
                current = value.GetValue(
                    name,
                    StringComparison.OrdinalIgnoreCase);
                if (current == null || current.Type == JTokenType.Null)
                {
                    return string.Empty;
                }
            }
            return current.Type == JTokenType.String
                ? current.Value<string>()?.Trim()
                : current.ToString(Formatting.None).Trim('"').Trim();
        }

        private static string FirstValue(params string[] values) =>
            values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim()
            ?? string.Empty;

        private static bool TryTraceBackQuantity(
            string value,
            out decimal quantity)
        {
            quantity = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            string normalized = value.Trim();
            if (normalized.Contains(",") && !normalized.Contains("."))
            {
                normalized = normalized.Replace(',', '.');
            }
            Match match = Regex.Match(
                normalized,
                @"[-+]?\d+(?:\.\d+)?");
            return match.Success &&
                decimal.TryParse(
                    match.Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out quantity);
        }

        private static ApiException BadRequest(string code, string message) =>
            new ApiException(HttpStatusCode.BadRequest, code, message);

        private static ApiException NotFound(string code, string message) =>
            new ApiException(HttpStatusCode.NotFound, code, message);

        private delegate bool VoucherAction(
            USERPCO.VoucherCO voucher,
            object id,
            ref string error);
    }
}
