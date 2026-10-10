using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Xml.Linq;
using GeneralInventoryCount.Models;
using MaterialOutboundApi.Infrastructure;

namespace MaterialOutboundApi.Services
{
    internal class InventoryCountLineEdit
    {
        public InventoryCountQuantityUpdateLineRequest Input { get; set; }
        public InventoryCountVoucherLineRow Current { get; set; }
        public string PositionCode { get; set; }
    }

    // Work only on the native Load result: schemas, timestamps and unmanaged
    // fields are retained. This transformation never calls COM or the database.
    internal static class InventoryCountLineEditor
    {
        private static readonly XNamespace Row = "#RowsetSchema";

        public static InventoryCountVoucherXml Apply(
            string headXml, string bodyXml, string positionXml,
            IEnumerable<InventoryCountLineEdit> edits)
        {
            var head = XDocument.Parse(headXml, LoadOptions.PreserveWhitespace);
            var body = XDocument.Parse(bodyXml, LoadOptions.PreserveWhitespace);
            var headRow = head.Descendants(Row + "row").Single();
            if (!string.IsNullOrWhiteSpace(Text(headRow, "caccounter")) ||
                Number(headRow, "iverifystate") > 0 ||
                Number(headRow, "iswfcontrolled") > 0)
                throw Error(409, "VOUCHER_NOT_EDITABLE", "盘点单已审核或受审批流程控制，不能修改。");

            var rows = body.Descendants(Row + "row").ToList();
            var changedIdentities = new List<XElement>();
            foreach (var edit in edits)
            {
                var input = edit.Input;
                var current = edit.Current;
                var found = rows.Where(r => Number(r, "irowno") == input.LineNo).ToList();
                if (found.Count != 1)
                    throw Error(409, "VOUCHER_LINE_XML_AMBIGUOUS", "U8 明细行不存在或行号重复：" + input.LineNo);
                var row = found[0];
                if (Number(row, "autoid") != current.AutoId ||
                    Text(row, "cinvcode") != (current.InventoryCode ?? "") ||
                    Text(row, "ccvbatch") != (current.BatchCode ?? "") ||
                    Text(row, "cposition") != (current.PositionCode ?? "") ||
                    Number(row, "icvquantity") != current.BookQuantity ||
                    Number(row, "icvcquantity") != current.ActualQuantity ||
                    Text(row, "ccvreason") != (current.Reason ?? "") ||
                    Text(row, "cbmemo") != (current.Remark ?? ""))
                    throw Error(409, "VOUCHER_LINE_CHANGED", "第 " + input.LineNo + " 行已变化，请重新查询。");

                bool identityChanged =
                    (input.UpdateBatchCode && (input.BatchCode ?? "") != Text(row, "ccvbatch")) ||
                    (input.UpdatePosition && (edit.PositionCode ?? "") != Text(row, "cposition"));
                if (identityChanged)
                {
                    if (Number(row, "rdsid") != 0)
                        throw Error(409, "LINKED_LINE_IDENTITY_CHANGE", "第 " + input.LineNo + " 行关联收发记录，不能更改批号或货位。");
                    if (input.UpdatePosition)
                    {
                        string headerPosition = Text(headRow, "cposition");
                        if (Number(headRow, "bposcheck") != 1 ||
                            (headerPosition != "" && headerPosition != edit.PositionCode))
                            throw Error(400, "POSITION_COUNT_SCOPE_MISMATCH", "该盘点单的盘点范围不允许改为此货位。");
                        if (!string.IsNullOrWhiteSpace(positionXml) &&
                            XDocument.Parse(positionXml).Descendants(Row + "row").Any())
                            throw Error(409, "POSITION_ALLOCATION_NOT_SUPPORTED", "该单有独立货位分配，请在 U8 中修改货位。");
                    }
                    changedIdentities.Add(row);
                }

                row.SetAttributeValue("editprop", "M");
                if (input.UpdateBatchCode) row.SetAttributeValue("ccvbatch", input.BatchCode ?? "");
                if (input.UpdatePosition) row.SetAttributeValue("cposition", edit.PositionCode ?? "");
                if (input.UpdateReason) row.SetAttributeValue("ccvreason", input.Reason ?? "");
                if (input.UpdateRemark) row.SetAttributeValue("cbmemo", input.Remark ?? "");

                decimal actual = input.ActualQuantity.Value;
                if (actual != current.ActualQuantity)
                {
                    decimal difference = actual - current.BookQuantity;
                    Set(row, "icvcquantity", actual);
                    Set(row, "yksl", difference);
                    decimal rate = Number(row, "iinvexchrate");
                    if (rate > 0)
                    {
                        decimal actualNumber = actual / rate;
                        Set(row, "icvcnum", actualNumber);
                        Set(row, "ykjs", actualNumber - Number(row, "icvnum"));
                    }
                    if (row.Attribute("ijhdj") != null)
                    {
                        Set(row, "isjdj", actual * Number(row, "ijhdj"));
                        Set(row, "isjje", difference * Number(row, "ijhdj"));
                    }
                }
            }
            // Only reject new identity collisions, not legitimate pre-existing
            // repeated lines in a voucher that is undergoing quantity edits.
            foreach (var changed in changedIdentities)
                if (rows.Any(other => other != changed && SameIdentity(other, changed)))
                    throw Error(400, "DUPLICATE_COUNT_LINE", "修改后与其他行的存货、批号、货位及自由项重复，请合并或重新选择。");

            headRow.SetAttributeValue("editprop", "M");
            return new InventoryCountVoucherXml
            {
                Head = head.ToString(SaveOptions.DisableFormatting),
                Body = body.ToString(SaveOptions.DisableFormatting)
            };
        }

        private static bool SameIdentity(XElement a, XElement b)
        {
            var fields = new[] { "cinvcode", "ccvbatch", "cposition" }
                .Concat(Enumerable.Range(1, 10).Select(i => "cfree" + i));
            return fields.All(f => string.Equals(Text(a, f), Text(b, f), StringComparison.OrdinalIgnoreCase));
        }

        private static string Text(XElement row, string name) => (string)row.Attribute(name) ?? "";
        private static decimal Number(XElement row, string name)
        {
            string text = Text(row, name);
            if (string.IsNullOrWhiteSpace(text)) return 0;
            if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out decimal value)) return value;
            throw Error(502, "INVALID_U8_XML_NUMBER", "U8 返回了无效数值字段：" + name);
        }
        private static void Set(XElement row, string name, decimal value) =>
            row.SetAttributeValue(name, value.ToString(CultureInfo.InvariantCulture));
        private static ApiException Error(int status, string code, string message) =>
            new ApiException((HttpStatusCode)status, code, message);
    }
}
