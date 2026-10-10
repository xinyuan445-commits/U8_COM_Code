using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MaterialOutboundConsole.Configuration;
using MaterialOutboundConsole.Data;
using MaterialOutboundConsole.Models;
using MSXML2;

namespace MaterialOutboundConsole.Services
{
    internal sealed class MaterialOutboundAddResult
    {
        public string VoucherId { get; set; }
        public string VoucherCode { get; set; }
    }

    internal sealed class MaterialOutboundService
    {
        private const string VoucherType = "11";
        private const string ShapeConversionVoucherType = "15";

        private readonly AppOptions _options;
        private readonly U8Repository _repository;
        private readonly VoucherXmlBuilder _xmlBuilder;

        public MaterialOutboundService(AppOptions options)
        {
            _options = options;
            _repository = new U8Repository(options.ConnectionString);
            _xmlBuilder = new VoucherXmlBuilder(AppDomain.CurrentDomain.BaseDirectory);
        }

        public string[] PreviewAdd(string productionOrderCode, string outboundCode)
        {
            return PreviewAdd(productionOrderCode, outboundCode, null, null, null);
        }

        public string[] PreviewAdd(
            string productionOrderCode,
            string outboundCode,
            ICollection<int> selectedMoDIds)
        {
            return PreviewAdd(
                productionOrderCode,
                outboundCode,
                selectedMoDIds,
                null,
                null);
        }

        public string[] PreviewAdd(
            string productionOrderCode,
            string outboundCode,
            ICollection<int> selectedMoDIds,
            string warehouseCode)
        {
            return PreviewAdd(
                productionOrderCode,
                outboundCode,
                selectedMoDIds,
                warehouseCode,
                null);
        }

        public string[] PreviewAdd(
            string productionOrderCode,
            string outboundCode,
            ICollection<int> selectedMoDIds,
            string warehouseCode,
            string positionCode)
        {
            var xml = BuildAddXml(
                productionOrderCode,
                outboundCode,
                selectedMoDIds,
                warehouseCode,
                positionCode);
            var outputCode = string.IsNullOrWhiteSpace(outboundCode)
                ? "AUTO"
                : outboundCode;
            var output = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Output",
                outputCode + "-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(output);

            var headPath = Path.Combine(output, "head.xml");
            var bodyPath = Path.Combine(output, "body.xml");
            File.WriteAllText(headPath, xml.Head);
            File.WriteAllText(bodyPath, xml.Body);
            return new[] { headPath, bodyPath };
        }

        public MaterialOutboundAddResult Add(
            string productionOrderCode,
            string outboundCode)
        {
            return Add(productionOrderCode, outboundCode, null, null, null);
        }

        public MaterialOutboundAddResult Add(
            string productionOrderCode,
            string outboundCode,
            ICollection<int> selectedMoDIds)
        {
            return Add(
                productionOrderCode,
                outboundCode,
                selectedMoDIds,
                null,
                null);
        }

        public MaterialOutboundAddResult Add(
            string productionOrderCode,
            string outboundCode,
            ICollection<int> selectedMoDIds,
            string warehouseCode)
        {
            return Add(
                productionOrderCode,
                outboundCode,
                selectedMoDIds,
                warehouseCode,
                null);
        }

        public MaterialOutboundAddResult Add(
            string productionOrderCode,
            string outboundCode,
            ICollection<int> selectedMoDIds,
            string warehouseCode,
            string positionCode)
        {
            if (!string.IsNullOrWhiteSpace(outboundCode) &&
                _repository.GetMaterialOutbound(outboundCode) != null)
            {
                throw new InvalidOperationException("材料出库单号已经存在：" + outboundCode);
            }

            var xml = BuildAddXml(
                productionOrderCode,
                outboundCode,
                selectedMoDIds,
                warehouseCode,
                positionCode);
            using (var session = new U8CoSession(_options.Account))
            {
                IXMLDOMDocument2 headDom = CreateDocument(xml.Head, "表头");
                IXMLDOMDocument2 bodyDom = CreateDocument(xml.Body, "表体");
                IXMLDOMDocument2 positionDom = new DOMDocument26Class();
                var error = string.Empty;
                var voucherId = string.Empty;
                var voucherCode = outboundCode;

                if (string.IsNullOrWhiteSpace(voucherCode))
                {
                    var validate = false;
                    var phase = false;
                    try
                    {
                        voucherCode = session.Voucher.GetNewVouchCode(
                            VoucherType,
                            headDom,
                            ref error,
                            ref validate,
                            ref phase);
                    }
                    catch (COMException exception)
                    {
                        throw U8ComException.Wrap(
                            "调用 VoucherCO.GetNewVouchCode（自动取材料出库单号）时",
                            exception);
                    }

                    if (string.IsNullOrWhiteSpace(voucherCode))
                    {
                        throw new InvalidOperationException(
                            "U8 自动生成材料出库单号失败：" + error);
                    }

                    var row = headDom.selectSingleNode("//z:row") as IXMLDOMElement;
                    if (row == null)
                    {
                        throw new InvalidOperationException("表头 XML 中找不到 z:row 节点。");
                    }
                    row.setAttribute("ccode", voucherCode);
                }

                ADODB.Connection connection = null;
                bool succeeded;
                try
                {
                    succeeded = session.Voucher.Insert(
                        VoucherType,
                        headDom,
                        bodyDom,
                        positionDom,
                        ref error,
                        ref connection,
                        ref voucherId);
                }
                catch (COMException exception)
                {
                    throw U8ComException.Wrap(
                        "调用 VoucherCO.Insert（提交材料出库单）时",
                        exception);
                }

                if (!succeeded)
                {
                    throw new InvalidOperationException("新增失败：" + error);
                }

                return new MaterialOutboundAddResult
                {
                    VoucherId = voucherId,
                    VoucherCode = voucherCode
                };
            }
        }

        public string Query(string outboundCode)
        {
            var record = GetRequiredOutbound(outboundCode);
            using (var session = new U8CoSession(_options.Account))
            {
                IXMLDOMDocument2 headDom = new DOMDocument26Class();
                IXMLDOMDocument2 bodyDom = new DOMDocument26Class();
                IXMLDOMDocument2 positionDom = new DOMDocument26Class();
                var error = string.Empty;

                var succeeded = session.Voucher.Load(
                    VoucherType,
                    "ID=" + record.Id,
                    ref headDom,
                    ref bodyDom,
                    ref positionDom,
                    ref error);

                if (!succeeded)
                {
                    throw new InvalidOperationException("查询失败：" + error);
                }

                var output = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Output",
                    outboundCode + "-query-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "head.xml"), headDom.xml);
                File.WriteAllText(Path.Combine(output, "body.xml"), bodyDom.xml);
                return output;
            }
        }

        public string ExportShapeConversionTemplate(string shapeConversionCode)
        {
            if (string.IsNullOrWhiteSpace(shapeConversionCode))
            {
                throw new ArgumentException("形态转换单号不能为空。");
            }

            var record = _repository.GetShapeConversion(shapeConversionCode.Trim());
            if (record == null)
            {
                throw new InvalidOperationException(
                    "未找到形态转换单：" + shapeConversionCode);
            }

            using (var session = new U8CoSession(_options.Account))
            {
                IXMLDOMDocument2 headDom = new DOMDocument26Class();
                IXMLDOMDocument2 bodyDom = new DOMDocument26Class();
                IXMLDOMDocument2 positionDom = new DOMDocument26Class();
                var error = string.Empty;

                bool succeeded;
                try
                {
                    succeeded = session.Voucher.Load(
                        ShapeConversionVoucherType,
                        "ID=" + record.Id,
                        ref headDom,
                        ref bodyDom,
                        ref positionDom,
                        ref error);
                }
                catch (COMException exception)
                {
                    throw U8ComException.Wrap(
                        "调用 VoucherCO.Load（读取形态转换单模板）时",
                        exception);
                }

                if (!succeeded)
                {
                    throw new InvalidOperationException(
                        "读取形态转换单失败：" + error);
                }

                if (headDom == null || string.IsNullOrWhiteSpace(headDom.xml))
                {
                    throw new InvalidOperationException(
                        "U8 已返回成功，但形态转换单表头 XML 为空。");
                }
                if (bodyDom == null || string.IsNullOrWhiteSpace(bodyDom.xml))
                {
                    throw new InvalidOperationException(
                        "U8 已返回成功，但形态转换单表体 XML 为空。");
                }

                var headXml = headDom.xml;
                var bodyXml = bodyDom.xml;
                var positionXml = positionDom == null
                    ? string.Empty
                    : positionDom.xml ?? string.Empty;

                var output = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Output",
                    "shape-template-" + record.Code + "-" +
                    DateTime.Now.ToString(
                        "yyyyMMddHHmmss",
                        CultureInfo.InvariantCulture));
                var rawOutput = Path.Combine(output, "raw");
                var templateOutput = Path.Combine(output, "template");
                Directory.CreateDirectory(rawOutput);
                Directory.CreateDirectory(templateOutput);

                File.WriteAllText(
                    Path.Combine(rawOutput, "head.xml"),
                    headXml);
                File.WriteAllText(
                    Path.Combine(rawOutput, "body.xml"),
                    bodyXml);
                File.WriteAllText(
                    Path.Combine(rawOutput, "position.xml"),
                    positionXml);

                File.WriteAllText(
                    Path.Combine(templateOutput, "head.xml"),
                    ReplaceRowsetData(
                        headXml,
                        "<z:row @z_row />"));
                File.WriteAllText(
                    Path.Combine(templateOutput, "body.xml"),
                    ReplaceRowsetData(
                        bodyXml,
                        "@z_row"));

                return output;
            }
        }

        public void UpdateQuantity(string outboundCode, int autoId, decimal newQuantity)
        {
            if (newQuantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newQuantity), "新数量必须大于零。");
            }

            var records = _repository.GetMaterialOutboundLines(outboundCode);
            if (records.Count == 0)
            {
                throw new InvalidOperationException("未找到材料出库单：" + outboundCode);
            }

            if (records.All(item => item.AutoId != autoId))
            {
                throw new InvalidOperationException("未找到材料出库单行 AutoID=" + autoId);
            }

            using (var session = new U8CoSession(_options.Account))
            {
                var timestamp = LoadTimestamp(session, records[0].Id);
                var head = BuildUpdateHead(records[0], timestamp);
                var body = records.Select((item, index) =>
                {
                    var quantity = item.AutoId == autoId ? newQuantity : item.Iquantity;
                    return new MaterialOutboundBody
                    {
                        id = item.PId,
                        autoid = item.AutoId,
                        irowno = index + 1,
                        editprop = "M",
                        invcode = item.Invcode,
                        cinvcode = item.Cinvcode,
                        iquantity = quantity,
                        iunitcost = item.Iunitcost,
                        iprice = quantity * item.Iunitcost,
                        inquantity = quantity,
                        cbatch = item.Cbatch,
                        impoids = item.Impoids,
                        cmocode = item.Cmocode,
                        imoseq = item.Imoseq,
                        iopseq = item.Iopseq,
                        copdesc = item.Copdesc,
                        cmworkcentercode = item.Cmworkcentercode,
                        cposition = item.Cposition,
                        cposname = item.Cposname,
                        iposflag = item.Iposflag
                    };
                }).ToList();

                var xml = _xmlBuilder.Build(head, body);
                IXMLDOMDocument2 headDom = CreateDocument(xml.Head, "表头");
                IXMLDOMDocument2 bodyDom = CreateDocument(xml.Body, "表体");
                IXMLDOMDocument2 positionDom = new DOMDocument26Class();
                var error = string.Empty;
                var succeeded = session.Voucher.Update(
                    VoucherType,
                    headDom,
                    bodyDom,
                    positionDom,
                    ref error);

                if (!succeeded)
                {
                    throw new InvalidOperationException("修改失败：" + error);
                }
            }
        }

        public void Verify(string outboundCode)
        {
            RunSimpleAction(outboundCode, "审核", (USERPCO.VoucherCO voucher, object id, ref string error) =>
                voucher.Verify(VoucherType, id, ref error));
        }

        public void Unverify(string outboundCode)
        {
            RunSimpleAction(outboundCode, "弃审", (USERPCO.VoucherCO voucher, object id, ref string error) =>
                voucher.UnVerify(VoucherType, id, ref error));
        }

        public void Delete(string outboundCode)
        {
            RunSimpleAction(outboundCode, "删除", (USERPCO.VoucherCO voucher, object id, ref string error) =>
                voucher.Delete(VoucherType, id, ref error));
        }

        public List<WorkOrderLine> GetWorkOrderLines(string productionOrderCode)
        {
            return _repository.GetWorkOrderLines(productionOrderCode);
        }

        private VoucherXml BuildAddXml(
            string productionOrderCode,
            string outboundCode,
            ICollection<int> selectedMoDIds,
            string warehouseCode,
            string positionCode)
        {
            var materials = _repository.GetProductionMaterials(
                productionOrderCode,
                selectedMoDIds);
            if (materials.Count == 0)
            {
                throw new InvalidOperationException(
                    "工单不存在、所选记录没有子件用料，或选择结果为空：" + productionOrderCode);
            }

            var departments = materials
                .Select(item => item.DepartmentCode)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (departments.Count > 1)
            {
                throw new InvalidOperationException(
                    "所选记录属于多个部门（" + string.Join(", ", departments) +
                    "），请按部门分别生成材料出库单。");
            }

            var first = materials[0];
            var resolvedWarehouse = string.IsNullOrWhiteSpace(warehouseCode)
                ? _options.WarehouseCode
                : warehouseCode.Trim();
            if (string.IsNullOrWhiteSpace(resolvedWarehouse))
            {
                throw new InvalidOperationException(
                    "材料出库仓库不能为空，请在交互流程中输入仓库编码。");
            }
            var warehouse = _repository.GetWarehouse(resolvedWarehouse);
            if (warehouse == null)
            {
                throw new InvalidOperationException(
                    "U8 中不存在仓库编码：" + resolvedWarehouse);
            }

            var resolvedPosition = string.IsNullOrWhiteSpace(positionCode)
                ? _options.DefaultPositionCode
                : positionCode.Trim();
            PositionInfo position = null;
            if (warehouse.UsesPosition)
            {
                if (string.IsNullOrWhiteSpace(resolvedPosition))
                {
                    throw new InvalidOperationException(
                        "仓库 " + resolvedWarehouse + " 启用了货位管理，货位编码不能为空。");
                }

                position = _repository.GetPosition(
                    resolvedWarehouse,
                    resolvedPosition);
                if (position == null)
                {
                    throw new InvalidOperationException(
                        "仓库 " + resolvedWarehouse +
                        " 中不存在末级货位编码：" + resolvedPosition);
                }
            }
            if (departments.Count == 0 &&
                string.IsNullOrWhiteSpace(_options.DepartmentCode))
            {
                throw new InvalidOperationException(
                    "所选工单记录没有生产部门，且未配置默认部门。");
            }

            var head = new MaterialOutboundHead
            {
                ddate = _options.VoucherDate,
                ccode = outboundCode,
                cpspcode = first.InvCode,
                cmpocode = first.MoCode,
                iproorderid = first.MoId,
                cmaker = _options.Maker,
                cwhcode = resolvedWarehouse,
                cdepcode = departments.Count == 1
                    ? departments[0]
                    : _options.DepartmentCode
            };

            var body = materials.Select((item, index) =>
            {
                if (!item.BaseQtyD.HasValue || item.BaseQtyD.Value == 0)
                {
                    throw new InvalidOperationException(
                        "生产订单子件 " + item.CInvCode + " 的基本用量分母为空或为零。");
                }

                var quantity =
                    (item.BaseQtyN.GetValueOrDefault() / item.BaseQtyD.Value) *
                    (1m + item.CompScrap.GetValueOrDefault()) *
                    item.Qty.GetValueOrDefault();

                return new MaterialOutboundBody
                {
                    irowno = index + 1,
                    editprop = "A",
                    invcode = item.InvCode,
                    cinvcode = item.CInvCode,
                    iquantity = quantity,
                    iunitcost = _options.DefaultUnitCost,
                    iprice = quantity * _options.DefaultUnitCost,
                    inquantity = quantity,
                    cbatch = _options.DefaultBatchCode,
                    impoids = item.AllocateId,
                    cmocode = item.MoCode,
                    imoseq = item.SortSeq,
                    iopseq = item.OpSeq,
                    copdesc = null,
                    cmworkcentercode = string.IsNullOrWhiteSpace(_options.WorkCenterCode)
                        ? null
                        : _options.WorkCenterCode,
                    cposition = position == null ? null : position.PositionCode,
                    cposname = position == null ? null : position.PositionName,
                    iposflag = position == null ? (int?)null : 1
                };
            }).ToList();

            return _xmlBuilder.Build(head, body);
        }

        private MaterialOutboundRecord GetRequiredOutbound(string outboundCode)
        {
            var record = _repository.GetMaterialOutbound(outboundCode);
            if (record == null)
            {
                throw new InvalidOperationException("未找到材料出库单：" + outboundCode);
            }

            return record;
        }

        private static MaterialOutboundHead BuildUpdateHead(
            MaterialOutboundRecord record,
            string timestamp)
        {
            return new MaterialOutboundHead
            {
                id = record.Id,
                ccode = record.CCode,
                cpspcode = record.Cpspcode,
                cmpocode = record.Cmpocode,
                iproorderid = record.Iproorderid,
                cmaker = record.Cmaker,
                cwhcode = record.Cwhcode,
                cdepcode = record.Cdepcode,
                ufts = timestamp,
                editprop = "M"
            };
        }

        private static string LoadTimestamp(U8CoSession session, int id)
        {
            IXMLDOMDocument2 headDom = new DOMDocument26Class();
            IXMLDOMDocument2 bodyDom = new DOMDocument26Class();
            IXMLDOMDocument2 positionDom = new DOMDocument26Class();
            var error = string.Empty;

            if (!session.Voucher.Load(
                    VoucherType,
                    "ID=" + id,
                    ref headDom,
                    ref bodyDom,
                    ref positionDom,
                    ref error))
            {
                throw new InvalidOperationException("加载修改时间戳失败：" + error);
            }

            var row = headDom.selectSingleNode("//z:row");
            var attribute = row == null ? null : row.attributes.getNamedItem("ufts");
            if (attribute == null || string.IsNullOrWhiteSpace(attribute.text))
            {
                throw new InvalidOperationException("材料出库单没有返回 ufts 时间戳。");
            }

            return attribute.text;
        }

        private static IXMLDOMDocument2 CreateDocument(string xml, string label)
        {
            IXMLDOMDocument2 document = new DOMDocument26Class();
            if (!document.loadXML(xml))
            {
                throw new InvalidOperationException(
                    label + " XML 无效：" + document.parseError.reason);
            }

            return document;
        }

        private static string ReplaceRowsetData(
            string xml,
            string replacement)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                throw new InvalidOperationException(
                    "U8 返回的 Recordset XML 为空。");
            }

            const string openTag = "<rs:data";
            const string closeTag = "</rs:data>";
            var openIndex = xml.IndexOf(
                openTag,
                StringComparison.OrdinalIgnoreCase);
            if (openIndex < 0)
            {
                throw new InvalidOperationException(
                    "U8 返回的 XML 中找不到 rs:data 节点。");
            }

            var contentStart = xml.IndexOf('>', openIndex);
            var contentEnd = xml.IndexOf(
                closeTag,
                contentStart + 1,
                StringComparison.OrdinalIgnoreCase);
            if (contentStart < 0 || contentEnd < 0)
            {
                throw new InvalidOperationException(
                    "U8 返回的 rs:data 节点结构不完整。");
            }

            return xml.Substring(0, contentStart + 1) +
                   Environment.NewLine +
                   "    " + replacement +
                   Environment.NewLine +
                   "  " +
                   xml.Substring(contentEnd);
        }

        private delegate bool VoucherAction(
            USERPCO.VoucherCO voucher,
            object id,
            ref string error);

        private void RunSimpleAction(
            string outboundCode,
            string actionName,
            VoucherAction action)
        {
            var record = GetRequiredOutbound(outboundCode);
            using (var session = new U8CoSession(_options.Account))
            {
                var error = string.Empty;
                var succeeded = action(session.Voucher, record.Id, ref error);
                if (!succeeded)
                {
                    throw new InvalidOperationException(actionName + "失败：" + error);
                }
            }
        }
    }
}
