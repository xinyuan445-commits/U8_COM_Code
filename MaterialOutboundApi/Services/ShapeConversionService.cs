using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ADODB;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;
using MaterialOutboundConsole.Models;
using MaterialOutboundConsole.Services;
using MSXML2;
using Newtonsoft.Json;

namespace MaterialOutboundApi.Services
{
    internal sealed class ShapeConversionService
    {
        private const string VoucherType = "15";
        private const string DefaultRemark = "拼皮线重新分色";
        private readonly SettingsStore _settings;
        private readonly StateStore _state;
        private readonly U8OperationQueue _queue;
        private readonly string _applicationDirectory;
        private readonly SemaphoreSlim _idempotencyGate = new SemaphoreSlim(1, 1);

        public ShapeConversionService(SettingsStore settings, StateStore state, U8OperationQueue queue, string applicationDirectory)
        {
            _settings = settings;
            _state = state;
            _queue = queue;
            _applicationDirectory = applicationDirectory;
        }

        public ShapeConversionPreviewDto Preview(ShapeConversionPreviewRequest request)
        {
            if (request?.Source == null || request.Target == null) throw BadRequest("SHAPE_LINES_REQUIRED", "转换前和转换后明细不能为空。");
            if (request.Source.Quantity <= 0 || request.Source.Quantity != request.Target.Quantity) throw BadRequest("SHAPE_QUANTITY_INVALID", "转换前后数量必须相等且大于零。");
            ApiSettings settings = RequiredSettings();
            DateTime date = DateTime.Today;
            ValidateYear(settings, date);
            ShapeConversionProfile profile = RequiredProfile(settings);
            ShapeConversionPreviewDto preview;
            try
            {
                preview = Repository(settings).Resolve(request, profile, date);
            }
            catch (InvalidOperationException exception)
            {
                throw new ApiException(HttpStatusCode.Conflict, "SHAPE_REFERENCE_INVALID", exception.Message, exception);
            }
            preview.Remark = NormalizeRemark(request.Remark);
            preview.AutoVerify = request.AutoVerify ?? true;
            if (preview.Source.CurrentStock < preview.Source.Quantity) throw new ApiException(HttpStatusCode.Conflict, "SHAPE_SOURCE_STOCK_INSUFFICIENT", "U8 现存量不足，无法转换。");
            preview.PreviewToken = Guid.NewGuid().ToString("N");
            preview.ExpiresAt = DateTime.Now.AddMinutes(15);
            _state.SaveShapePreview(new ShapeConversionPreviewRecord
            {
                Token = preview.PreviewToken,
                ExpiresAt = preview.ExpiresAt,
                Database = settings.SqlDatabase,
                Json = JsonConvert.SerializeObject(preview)
            });
            return preview;
        }

        public ShapeConversionPreviewDto PreviewImport(ShapeConversionImportPreviewRequest request)
        {
            if (request?.Pairs == null || request.Pairs.Count == 0) throw BadRequest("SHAPE_IMPORT_PAIRS_REQUIRED", "Excel 整单导入至少需要一组转换明细。");
            if (request.Pairs.Count > 5000) throw BadRequest("SHAPE_IMPORT_TOO_LARGE", "Excel 整单导入不能超过 5000 组。");
            for (int index = 0; index < request.Pairs.Count; index++) ValidateImportPair(request.Pairs[index], index + 1);
            ApiSettings settings = RequiredSettings();
            DateTime date = DateTime.Today;
            ValidateYear(settings, date);
            ShapeConversionPreviewDto preview;
            try
            {
                preview = Repository(settings).ResolveImport(request, RequiredProfile(settings), date);
            }
            catch (InvalidOperationException exception)
            {
                throw new ApiException(HttpStatusCode.Conflict, "SHAPE_REFERENCE_INVALID", exception.Message, exception);
            }
            preview.Remark = NormalizeRemark(request.Remark);
            preview.AutoVerify = request.AutoVerify ?? false;
            EnsureSourceStock(preview, "U8 现存量不足，无法导入整张形态转换单。");
            preview.PreviewToken = Guid.NewGuid().ToString("N");
            preview.ExpiresAt = DateTime.Now.AddMinutes(15);
            _state.SaveShapePreview(new ShapeConversionPreviewRecord
            {
                Token = preview.PreviewToken,
                ExpiresAt = preview.ExpiresAt,
                Database = settings.SqlDatabase,
                Json = JsonConvert.SerializeObject(preview)
            });
            return preview;
        }

        public void ValidateProfiles(ApiSettings settings)
        {
            foreach (string database in new[] { "UFDATA_008_2021", "UFDATA_998_2021" })
            {
                ApiSettings account = settings.ForDatabase(database);
                try
                {
                    Repository(account).ValidateProfile(account.ShapeProfileForDatabase(database));
                }
                catch (Exception exception)
                {
                    throw new ApiException(
                        HttpStatusCode.BadRequest,
                        "SHAPE_PROFILE_INVALID",
                        database + " 形态转换参数校验失败：" + exception.Message,
                        exception);
                }
            }
        }

        public async Task<ShapeConversionCreatedDto> Create(CreateShapeConversionRequest request, string idempotencyKey)
        {
            ValidateIdempotencyKey(idempotencyKey);
            ApiSettings settings = RequiredSettings();
            string recordId = "shape:create:" + settings.SqlDatabase + ":" + idempotencyKey;
            string previewToken = request?.PreviewToken ?? string.Empty;
            string payloadHash = Hash(settings.SqlDatabase + "|" + previewToken);
            await _idempotencyGate.WaitAsync();
            try
            {
                IdempotencyRecord record = _state.GetIdempotency(recordId);
                if (record != null && record.PayloadHash != payloadHash) throw new ApiException(HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED", "该 Idempotency-Key 已用于不同的形态转换请求。");
                ShapeConversionPreviewRecord saved = _state.GetShapePreview(previewToken);
                string previewJson = saved?.Json ?? record?.PayloadJson;
                if (string.IsNullOrWhiteSpace(previewJson)) throw BadRequest("SHAPE_PREVIEW_EXPIRED", "预览已过期，且该请求尚无可恢复的 U8 状态，请重新扫描并预览。");
                if (saved != null && !string.Equals(saved.Database, settings.SqlDatabase, StringComparison.OrdinalIgnoreCase)) throw new ApiException(HttpStatusCode.Conflict, "SHAPE_PREVIEW_DATABASE_MISMATCH", "预览账套与当前账套不一致。");
                var original = JsonConvert.DeserializeObject<ShapeConversionPreviewDto>(previewJson);
                if (original == null) throw BadRequest("SHAPE_PREVIEW_INVALID", "预览数据无效。");
                if (record == null)
                {
                    if (original.VoucherDate.Date != DateTime.Today) throw new ApiException(HttpStatusCode.Conflict, "SHAPE_PREVIEW_DATE_CHANGED", "IIS 服务器日期已变化，请重新预览。");
                    record = new IdempotencyRecord { Id = recordId, PayloadHash = payloadHash, PayloadJson = previewJson, Status = "Processing" };
                    _state.UpsertIdempotency(record);
                }
                else if (string.IsNullOrWhiteSpace(record.PayloadJson))
                {
                    record.PayloadJson = previewJson;
                    _state.UpsertIdempotency(record);
                }
                if (record.Status == "Completed" && !string.IsNullOrWhiteSpace(record.ResultJson))
                {
                    ShapeConversionCreatedDto replay = JsonConvert.DeserializeObject<ShapeConversionCreatedDto>(record.ResultJson);
                    if (replay != null) { replay.Replayed = true; return replay; }
                }

                bool replaying = !string.IsNullOrWhiteSpace(record.VoucherCode) || !string.IsNullOrWhiteSpace(record.VoucherId);
                if (!replaying && original.VoucherDate.Date != DateTime.Today) throw new ApiException(HttpStatusCode.Conflict, "SHAPE_PREVIEW_DATE_CHANGED", "IIS 服务器日期已变化，请重新预览。");
                ShapeConversionPreviewDto current = Revalidate(settings, original, replaying ? original.VoucherDate : DateTime.Today, !replaying);
                ShapeConversionCreatedDto result = await _queue.Execute(() => ResumeOrCreate(settings, current, record));
                result.U8Database = settings.SqlDatabase;
                result.AutoVerify = AutoVerifyEnabled(current);
                result.Replayed = replaying;
                record.Status = IsComplete(result, AutoVerifyEnabled(current)) ? "Completed" : "Partial";
                record.ResultJson = JsonConvert.SerializeObject(result);
                _state.UpsertIdempotency(record);
                return result;
            }
            finally
            {
                _idempotencyGate.Release();
            }
        }

        public ShapeConversionCreatedDto Query(string voucherCode)
        {
            if (string.IsNullOrWhiteSpace(voucherCode)) throw BadRequest("VOUCHER_CODE_REQUIRED", "形态转换单号不能为空。");
            ApiSettings settings = RequiredSettings();
            ShapeVoucherStatusDto shape = Repository(settings).GetShapeVoucher(voucherCode.Trim());
            if (shape == null) throw new ApiException(HttpStatusCode.NotFound, "SHAPE_CONVERSION_NOT_FOUND", "未找到形态转换单。");
            List<ShapeVoucherStatusDto> generated = Repository(settings).GetGeneratedVouchers(shape.Code);
            ShapeConversionCreatedDto result = Result(DateTime.Today, shape, generated, null, true);
            result.U8Database = settings.SqlDatabase;
            return result;
        }

        private ShapeConversionPreviewDto Revalidate(ApiSettings settings, ShapeConversionPreviewDto original, DateTime date, bool requireSourceStock)
        {
            ShapeConversionPreviewDto current;
            if (original.Pairs != null && original.Pairs.Count > 0)
            {
                var request = new ShapeConversionImportPreviewRequest
                {
                    Pairs = original.Pairs.Select(x => new ShapeConversionPairRequest
                    {
                        Source = ToRequest(x.Source),
                        Target = ToRequest(x.Target)
                    }).ToList(),
                    Remark = original.Remark,
                    AutoVerify = original.AutoVerify
                };
                current = Repository(settings).ResolveImport(request, RequiredProfile(settings), date);
            }
            else
            {
                var request = new ShapeConversionPreviewRequest
                {
                    Source = ToRequest(original.Source),
                    Target = ToRequest(original.Target),
                    Remark = original.Remark,
                    AutoVerify = original.AutoVerify
                };
                current = Repository(settings).Resolve(request, RequiredProfile(settings), date);
            }
            current.Remark = NormalizeRemark(original.Remark);
            current.AutoVerify = AutoVerifyEnabled(original);
            if (requireSourceStock) EnsureSourceStock(current, "创建前复核发现 U8 现存量不足。");
            return current;
        }

        private ShapeConversionCreatedDto ResumeOrCreate(ApiSettings settings, ShapeConversionPreviewDto preview, IdempotencyRecord record)
        {
            ShapeConversionRepository repository = Repository(settings);
            long existingId;
            long? id = long.TryParse(record.VoucherId, out existingId) ? existingId : (long?)null;
            ShapeVoucherStatusDto shape = repository.GetShapeVoucher(record.VoucherCode, id);
            if (shape == null)
            {
                shape = InsertShapeVoucher(settings, preview, record);
            }
            record.VoucherId = shape.Id.ToString(CultureInfo.InvariantCulture);
            record.VoucherCode = shape.Code;
            _state.UpsertIdempotency(record);

            if (!AutoVerifyEnabled(preview))
            {
                return Result(
                    preview.VoucherDate,
                    repository.GetShapeVoucher(shape.Code, shape.Id) ?? shape,
                    repository.GetGeneratedVouchers(shape.Code),
                    TargetInventoryName(preview),
                    false);
            }

            using (U8CoSession session = OpenSession(settings, preview.VoucherDate))
            {
                if (!shape.IsVerified)
                {
                    shape.IsVerified = TryVerify(session, VoucherType, shape.Id, out _);
                }
                List<ShapeVoucherStatusDto> generated = WaitForGenerated(repository, shape.Code);
                ShapeVoucherStatusDto outbound = generated.FirstOrDefault(x => x.VoucherType == "09");
                if (shape.IsVerified && outbound != null && !outbound.IsVerified) outbound.IsVerified = TryVerify(session, "09", outbound.Id, out _);
                ShapeVoucherStatusDto inbound = generated.FirstOrDefault(x => x.VoucherType == "08");
                if (shape.IsVerified && outbound?.IsVerified == true && inbound != null && !inbound.IsVerified) inbound.IsVerified = TryVerify(session, "08", inbound.Id, out _);
                generated = repository.GetGeneratedVouchers(shape.Code);
                return Result(preview.VoucherDate, repository.GetShapeVoucher(shape.Code, shape.Id) ?? shape, generated, TargetInventoryName(preview), false);
            }
        }

        private ShapeVoucherStatusDto InsertShapeVoucher(ApiSettings settings, ShapeConversionPreviewDto preview, IdempotencyRecord record)
        {
            VoucherXml xml = BuildXml(settings, preview);
            using (U8CoSession session = OpenSession(settings, preview.VoucherDate))
            {
                IXMLDOMDocument2 numberHead = CreateDocument(xml.Head, "表头");
                string error = string.Empty;
                bool validate = false, phase = false;
                string code = session.Voucher.GetNewVouchCode(VoucherType, numberHead, ref error, ref validate, ref phase);
                if (string.IsNullOrWhiteSpace(code)) throw new ApiException(HttpStatusCode.BadGateway, "U8_SHAPE_NUMBER_FAILED", "U8 自动取得形态转换单号失败：" + error);
                record.VoucherCode = code;
                _state.UpsertIdempotency(record);

                IXMLDOMDocument2 head = CreateDocument(xml.Head, "表头");
                IXMLDOMDocument2 body = CreateDocument(xml.Body, "表体");
                SetCode(head, code); SetCode(body, code);
                // MSXML 2.6/U8 CO 在自动取号后必须重新构建 DOM。
                head = CreateDocument(head.xml, "表头");
                body = CreateDocument(body.xml, "表体");
                IXMLDOMDocument2 position = new DOMDocument26Class();
                Connection connection = null;
                string voucherId = string.Empty;
                bool inserted = session.Voucher.Insert(VoucherType, head, body, position, ref error, ref connection, ref voucherId);
                if (!inserted) throw new ApiException(HttpStatusCode.BadGateway, "U8_SHAPE_INSERT_FAILED", "U8 新增形态转换单失败：" + error);
                if (!long.TryParse(voucherId, out long id)) throw new ApiException(HttpStatusCode.BadGateway, "U8_SHAPE_ID_INVALID", "形态转换单已新增，但 U8 未返回有效 ID。单号：" + code);
                record.VoucherId = voucherId;
                _state.UpsertIdempotency(record);
                return new ShapeVoucherStatusDto { VoucherType = "15", Name = "形态转换单", Id = id, Code = code, IsVerified = false };
            }
        }

        private static bool TryVerify(U8CoSession session, string type, long id, out string error)
        {
            error = string.Empty;
            try { return session.Voucher.Verify(type, id.ToString(CultureInfo.InvariantCulture), ref error); }
            catch (COMException exception) { error = exception.Message; return false; }
        }

        private static List<ShapeVoucherStatusDto> WaitForGenerated(ShapeConversionRepository repository, string code)
        {
            List<ShapeVoucherStatusDto> values = new List<ShapeVoucherStatusDto>();
            for (int attempt = 0; attempt < 30; attempt++)
            {
                values = repository.GetGeneratedVouchers(code);
                if (values.Any(x => x.VoucherType == "09") && values.Any(x => x.VoucherType == "08")) return values;
                Thread.Sleep(500);
            }
            return values;
        }

        private VoucherXml BuildXml(ApiSettings settings, ShapeConversionPreviewDto preview)
        {
            string directory = Path.Combine(_applicationDirectory, "ShapeConversionXml");
            string headTemplate = File.ReadAllText(Path.Combine(directory, "head.xml"), Encoding.UTF8);
            string bodyTemplate = File.ReadAllText(Path.Combine(directory, "body.xml"), Encoding.UTF8);
            var head = new Dictionary<string, string>
            {
                ["cvouchtype"] = VoucherType, ["davdate"] = preview.VoucherDate.ToString("yyyy-MM-ddT00:00:00", CultureInfo.InvariantCulture),
                ["cavcode"] = string.Empty, ["cdepcode"] = preview.DepartmentCode, ["cavmemo"] = NormalizeRemark(preview.Remark), ["cvouchname"] = "形态转换单", ["cdepname"] = preview.DepartmentName,
                ["cirdcode"] = preview.InboundRdCode, ["cordcode"] = preview.OutboundRdCode, ["cmaker"] = settings.Maker,
                ["crdname"] = preview.InboundRdName, ["crdname_1"] = preview.OutboundRdName, ["btransflag"] = "False",
                ["vt_id"] = preview.VoucherTemplateId.ToString(CultureInfo.InvariantCulture), ["ctransflag"] = "1", ["iswfcontrolled"] = "0", ["editprop"] = "A"
            };
            var rows = new List<string>();
            int rowNumber = 1;
            foreach (ShapeConversionResolvedPairDto pair in PreviewPairs(preview))
            {
                rows.Add(BuildBodyRow(pair.Source, "转换前", rowNumber++, pair.GroupNumber));
                rows.Add(BuildBodyRow(pair.Target, "转换后", rowNumber++, pair.GroupNumber));
            }
            return new VoucherXml { Head = headTemplate.Replace("<z:row @z_row />", BuildRow(head)), Body = bodyTemplate.Replace("@z_row", string.Join(Environment.NewLine, rows)) };
        }

        private static string BuildBodyRow(ShapeConversionResolvedLineDto line, string type, int rowNumber, int groupNumber) => BuildRow(new Dictionary<string, string>
        {
            ["bavtype"] = type, ["cwhname"] = line.WarehouseName, ["cinvname"] = line.InventoryName, ["cinvstd"] = line.Specification,
            ["cinvdefine1"] = line.Define1, ["cinvdefine4"] = line.Define4, ["cinvdefine5"] = line.Define5, ["cinvdefine6"] = line.Define6,
            ["cinvm_unit"] = line.UnitName, ["cassunit"] = line.UnitCode, ["cavbatch"] = line.BatchCode,
            ["iavquantity"] = line.Quantity.ToString(CultureInfo.InvariantCulture), ["cavcode"] = string.Empty, ["cwhcode"] = line.WarehouseCode,
            ["iinvexchrate"] = "1", ["cinvcode"] = line.InventoryCode, ["bcosting"] = line.IsCosting ? "True" : "False",
            ["iexpiratdatecalcu"] = line.ExpirationDateCalculation.ToString(CultureInfo.InvariantCulture), ["imassdate"] = line.ShelfLife?.ToString(CultureInfo.InvariantCulture),
            ["cmassunit"] = line.ShelfLifeUnit, ["irowno"] = rowNumber.ToString(CultureInfo.InvariantCulture), ["cposition"] = line.PositionCode,
            ["cposname"] = line.PositionName, ["igroupno"] = groupNumber.ToString(CultureInfo.InvariantCulture), ["editprop"] = "A"
        });

        private static string BuildRow(IDictionary<string, string> values) => "<z:row " + string.Join(" ", values.Where(x => x.Value != null).Select(x => x.Key + "=\"" + SecurityElement.Escape(x.Value) + "\"")) + " />";

        private static IXMLDOMDocument2 CreateDocument(string xml, string label)
        {
            IXMLDOMDocument2 document = new DOMDocument26Class();
            if (!document.loadXML(xml)) throw BadRequest("INVALID_U8_XML", label + " XML 无效：" + document.parseError.reason);
            return document;
        }

        private static void SetCode(IXMLDOMDocument2 document, string code)
        {
            IXMLDOMNodeList rows = document.getElementsByTagName("z:row");
            for (int index = 0; index < rows.length; index++) ((IXMLDOMElement)rows[index]).setAttribute("cavcode", code);
        }

        private U8CoSession OpenSession(ApiSettings settings, DateTime date) => new U8CoSession(new U8Account
        {
            SubId = settings.U8SubId, AccId = settings.U8AccId, Year = settings.U8Year, UserId = settings.U8UserId,
            Password = settings.U8Password, Date = date.ToString("yyyy-MM-dd"), Server = settings.U8Server, Serial = settings.U8Serial ?? string.Empty
        });

        private ApiSettings RequiredSettings()
        {
            if (!_settings.IsConfigured) throw new ApiException(HttpStatusCode.ServiceUnavailable, "NOT_CONFIGURED", "请先在服务器本机完成配置。");
            return _settings.Current.ForDatabase(U8DatabaseContext.Current);
        }

        private static ShapeConversionProfile RequiredProfile(ApiSettings settings)
        {
            ShapeConversionProfile value = settings.ShapeProfileForDatabase(settings.SqlDatabase);
            if (value == null || !value.IsComplete) throw new ApiException(HttpStatusCode.ServiceUnavailable, "SHAPE_PROFILE_NOT_CONFIGURED", "当前账套尚未配置形态转换固定参数。");
            return value;
        }

        private static void ValidateYear(ApiSettings settings, DateTime date)
        {
            if (!int.TryParse(settings.U8Year, out int year) || date.Year != year) throw BadRequest("VOUCHER_DATE_YEAR_MISMATCH", "IIS 服务器当天与 U8 登录年度不一致。");
        }

        private static void ValidateImportPair(ShapeConversionPairRequest pair, int groupNumber)
        {
            if (pair?.Source == null || pair.Target == null) throw BadRequest("SHAPE_IMPORT_LINES_REQUIRED", "第 " + groupNumber + " 组转换前和转换后明细不能为空。");
            if (pair.Source.Quantity <= 0 || pair.Source.Quantity != pair.Target.Quantity) throw BadRequest("SHAPE_QUANTITY_INVALID", "第 " + groupNumber + " 组转换前后数量必须相等且大于零。");
            if (!string.Equals(pair.Source.WarehouseCode, pair.Target.WarehouseCode, StringComparison.OrdinalIgnoreCase)) throw BadRequest("SHAPE_WAREHOUSE_MISMATCH", "第 " + groupNumber + " 组转换前后仓库必须一致。");
        }

        private static List<ShapeConversionResolvedPairDto> PreviewPairs(ShapeConversionPreviewDto preview)
        {
            if (preview?.Pairs != null && preview.Pairs.Count > 0) return preview.Pairs;
            if (preview?.Source == null || preview.Target == null) throw BadRequest("SHAPE_PREVIEW_INVALID", "形态转换预览缺少表体明细。");
            return new List<ShapeConversionResolvedPairDto>
            {
                new ShapeConversionResolvedPairDto { GroupNumber = 1, Source = preview.Source, Target = preview.Target }
            };
        }

        private static void EnsureSourceStock(ShapeConversionPreviewDto preview, string message)
        {
            var shortages = PreviewPairs(preview)
                .Select(x => x.Source)
                .GroupBy(x => string.Join("\u001f", new[]
                {
                    x.WarehouseCode ?? string.Empty,
                    x.InventoryCode ?? string.Empty,
                    x.BatchCode ?? string.Empty,
                    x.PositionCode ?? string.Empty
                }), StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Line = group.First(),
                    Required = group.Sum(x => x.Quantity)
                })
                .Where(x => x.Line.CurrentStock < x.Required)
                .ToList();
            if (shortages.Count == 0) return;
            var first = shortages[0];
            string detail = string.Format(
                CultureInfo.InvariantCulture,
                "存货 {0}，批号 {1}，仓库/货位 {2}/{3}，需要 {4}，现存 {5}",
                first.Line.InventoryCode,
                first.Line.BatchCode,
                first.Line.WarehouseCode,
                first.Line.PositionCode,
                first.Required,
                first.Line.CurrentStock);
            if (shortages.Count > 1) detail += "；另有 " + (shortages.Count - 1) + " 项不足";
            throw new ApiException(HttpStatusCode.Conflict, "SHAPE_SOURCE_STOCK_INSUFFICIENT", message + " " + detail);
        }

        private static string TargetInventoryName(ShapeConversionPreviewDto preview)
        {
            ShapeConversionResolvedPairDto pair = PreviewPairs(preview).LastOrDefault();
            return pair?.Target?.InventoryName;
        }

        private static ShapeConversionLineRequest ToRequest(ShapeConversionResolvedLineDto value) => new ShapeConversionLineRequest
        {
            WarehouseCode = value.WarehouseCode, InventoryCode = value.InventoryCode, BatchCode = value.BatchCode,
            PositionCode = value.PositionCode, Quantity = value.Quantity
        };
        private static string NormalizeRemark(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0) return DefaultRemark;
            if (normalized.Length > 255) throw BadRequest("SHAPE_REMARK_TOO_LONG", "形态转换表头备注不能超过 255 个字符。");
            return normalized;
        }

        private ShapeConversionRepository Repository(ApiSettings settings) => new ShapeConversionRepository(settings.BuildConnectionString());
        private static bool AutoVerifyEnabled(ShapeConversionPreviewDto value) => value?.AutoVerify ?? true;
        private static bool IsComplete(ShapeConversionCreatedDto value, bool autoVerify) =>
            !autoVerify
                ? value?.ShapeConversion != null && value.ShapeConversion.Id > 0 && !string.IsNullOrWhiteSpace(value.ShapeConversion.Code)
                : value?.ShapeConversion?.IsVerified == true && value.OutboundVoucher?.IsVerified == true && value.InboundVoucher?.IsVerified == true;
        private static ShapeConversionCreatedDto Result(DateTime date, ShapeVoucherStatusDto shape, IEnumerable<ShapeVoucherStatusDto> generated, string targetName, bool replayed)
        {
            List<ShapeVoucherStatusDto> rows = generated?.ToList() ?? new List<ShapeVoucherStatusDto>();
            return new ShapeConversionCreatedDto { VoucherDate = date, ShapeConversion = shape,
                OutboundVoucher = rows.FirstOrDefault(x => x.VoucherType == "09") ?? new ShapeVoucherStatusDto { VoucherType = "09", Name = "其他出库单" },
                InboundVoucher = rows.FirstOrDefault(x => x.VoucherType == "08") ?? new ShapeVoucherStatusDto { VoucherType = "08", Name = "其他入库单" },
                TargetInventoryName = targetName, Replayed = replayed };
        }
        private static void ValidateIdempotencyKey(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length < 8 || value.Length > 64) throw BadRequest("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key 必须为 8 到 64 个字符。"); }
        private static string Hash(string value) { using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        private static ApiException BadRequest(string code, string message) => new ApiException(HttpStatusCode.BadRequest, code, message);

        private sealed class VoucherXml { public string Head { get; set; } public string Body { get; set; } }
    }
}
