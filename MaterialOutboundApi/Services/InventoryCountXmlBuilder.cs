using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace MaterialOutboundApi.Services
{
    internal sealed class InventoryCountVoucherXml
    {
        public string Head { get; set; }
        public string Body { get; set; }
    }

    internal sealed class InventoryCountVoucherHead
    {
        public DateTime? dcvdate { get; set; }
        public string ccvcode { get; set; }
        public DateTime? dacdate { get; set; }
        public int? vt_id { get; set; }
        public bool? btransflag { get; set; } = false;
        public string cwhcode { get; set; }
        public string ccvbatch { get; set; }
        public string cdepcode { get; set; }
        public string cirdcode { get; set; }
        public string cordcode { get; set; }
        public string csource { get; set; }
        public string cpersoncode { get; set; }
        public string ccvmemo { get; set; }
        public string cdefine1 { get; set; }
        public string cdefine2 { get; set; }
        public string cdefine3 { get; set; }
        public DateTime? cdefine4 { get; set; }
        public int? cdefine5 { get; set; }
        public DateTime? cdefine6 { get; set; }
        public decimal? cdefine7 { get; set; }
        public string cdefine8 { get; set; }
        public string cdefine9 { get; set; }
        public string cdefine10 { get; set; }
        public string cdefine11 { get; set; }
        public string cdefine12 { get; set; }
        public string cdefine13 { get; set; }
        public string cdefine14 { get; set; }
        public int? cdefine15 { get; set; }
        public decimal? cdefine16 { get; set; }
        public string cposition { get; set; }
        public string cmaker { get; set; }
        public string ccvtype { get; set; }
        public string ccvperiod { get; set; }
        public int? bposcheck { get; set; }
        public int? ireturncount { get; set; } = 0;
        public int? iverifystate { get; set; } = 0;
        public int? iswfcontrolled { get; set; } = 0;
        public DateTime? dnmaketime { get; set; } = DateTime.Now;
        public int? iPrintCount { get; set; } = 0;
        public int? checkvouchtype { get; set; } = 0;
        public int? buploaded { get; set; } = 0;
        public int? bwirelessvouch { get; set; } = 0;
        public string editprop { get; set; } = "A";
    }

    internal sealed class InventoryCountVoucherBody
    {
        public string cinvcode { get; set; }
        public string cposition { get; set; }
        public string ccvbatch { get; set; }
        public decimal? iadinquantity { get; set; } = 0;
        public decimal? iadinnum { get; set; } = 0;
        public decimal? iadoutquantity { get; set; } = 0;
        public decimal? iadoutnum { get; set; } = 0;
        public decimal? iallowwaste { get; set; }
        public decimal? iactualwaste { get; set; }
        public string cfree1 { get; set; }
        public string cfree2 { get; set; }
        public string cfree3 { get; set; }
        public string cfree4 { get; set; }
        public string cfree5 { get; set; }
        public string cfree6 { get; set; }
        public string cfree7 { get; set; }
        public string cfree8 { get; set; }
        public string cfree9 { get; set; }
        public string cfree10 { get; set; }
        public string cdefine22 { get; set; }
        public string cdefine23 { get; set; }
        public string cdefine24 { get; set; }
        public string cdefine25 { get; set; }
        public decimal? cdefine26 { get; set; }
        public decimal? cdefine27 { get; set; }
        public string cdefine28 { get; set; }
        public string cdefine29 { get; set; }
        public string cdefine30 { get; set; }
        public string cdefine31 { get; set; }
        public string cdefine32 { get; set; }
        public string cdefine33 { get; set; }
        public int? cdefine34 { get; set; }
        public int? cdefine35 { get; set; }
        public DateTime? cdefine36 { get; set; }
        public DateTime? cdefine37 { get; set; }
        public int? imassdate { get; set; }
        public string cmassunit { get; set; }
        public string cassunit { get; set; }
        public decimal? iinvexchrate { get; set; }
        public decimal? icvnum { get; set; }
        public decimal? icvquantity { get; set; }
        public decimal? icvcnum { get; set; }
        public decimal? icvcquantity { get; set; }
        public decimal? ykjs { get; set; }
        public decimal? yksl { get; set; }
        public decimal? icvjustnum { get; set; }
        public decimal? icvjustquantity { get; set; }
        public string ccvreason { get; set; }
        public string ccvcode { get; set; }
        public long? rdsid { get; set; }
        public decimal? ijhdj { get; set; }
        public decimal? ijhje { get; set; }
        public decimal? isjdj { get; set; }
        public decimal? isjje { get; set; }
        public string citemcode { get; set; }
        public string citem_class { get; set; }
        public string cbvencode { get; set; }
        public string cinvouchcode { get; set; }
        public string cinvouchtype { get; set; }
        public DateTime? ddisdate { get; set; }
        public DateTime? dmadedate { get; set; }
        public int? isotype { get; set; } = 0;
        public long? isodid { get; set; }
        public int? iexpiratdatecalcu { get; set; } = 0;
        public string cexpirationdate { get; set; }
        public DateTime? dexpirationdate { get; set; }
        public decimal? cbatchproperty1 { get; set; }
        public decimal? cbatchproperty2 { get; set; }
        public decimal? cbatchproperty3 { get; set; }
        public decimal? cbatchproperty4 { get; set; }
        public decimal? cbatchproperty5 { get; set; }
        public string cbatchproperty6 { get; set; }
        public string cbatchproperty7 { get; set; }
        public string cbatchproperty8 { get; set; }
        public string cbatchproperty9 { get; set; }
        public DateTime? cbatchproperty10 { get; set; }
        public string cbmemo { get; set; }
        public int? irowno { get; set; }
        public string editprop { get; set; } = "A";
    }

    internal static class InventoryCountXmlBuilder
    {
        private static readonly XNamespace Rowset =
            "urn:schemas-microsoft-com:rowset";
        private static readonly XNamespace Row = "#RowsetSchema";

        public static InventoryCountVoucherXml Build(
            string headTemplate,
            string bodyTemplate,
            InventoryCountVoucherHead head,
            IList<InventoryCountVoucherBody> lines)
        {
            return new InventoryCountVoucherXml
            {
                Head = ReplaceRows(headTemplate, new object[] { head }),
                Body = ReplaceRows(bodyTemplate, lines.Cast<object>())
            };
        }

        private static string ReplaceRows(
            string template,
            IEnumerable<object> values)
        {
            XDocument document = XDocument.Parse(
                template,
                LoadOptions.PreserveWhitespace);
            XElement data = document.Descendants(Rowset + "data").FirstOrDefault();
            if (data == null)
            {
                throw new InvalidOperationException(
                    "U8 XML 模板缺少 rs:data 节点。");
            }
            data.RemoveNodes();
            foreach (object value in values)
            {
                var row = new XElement(Row + "row");
                foreach (PropertyInfo property in value.GetType().GetProperties(
                             BindingFlags.Public | BindingFlags.Instance))
                {
                    object field = property.GetValue(value, null);
                    if (field == null) continue;
                    row.SetAttributeValue(property.Name, Format(field));
                }
                data.Add(row);
            }
            return document.ToString(SaveOptions.DisableFormatting);
        }

        private static string Format(object value)
        {
            if (value is DateTime date)
            {
                return date.ToString(
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture);
            }
            if (value is bool flag) return flag ? "True" : "False";
            return value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }
    }
}
