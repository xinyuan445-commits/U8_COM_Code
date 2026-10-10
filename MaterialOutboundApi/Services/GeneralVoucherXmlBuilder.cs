using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;

namespace MaterialOutboundApi.Services
{
    internal sealed class GeneralVoucherXml
    {
        public string Head { get; set; }
        public string Body { get; set; }
    }

    internal sealed class GeneralVoucherHead
    {
        public int? brdflag { get; set; } = 0;
        public string cvouchtype { get; set; } = "11";
        public string cwhcode { get; set; }
        public DateTime? ddate { get; set; }
        public string ccode { get; set; }
        public string crdcode { get; set; }
        public string cdepcode { get; set; }
        public string cpersoncode { get; set; }
        public string cprobatch { get; set; }
        public string chandler { get; set; }
        public string cmemo { get; set; }
        public string cmaker { get; set; }
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
        public string csource { get; set; }
        public string cbustype { get; set; }
        public string cbuscode { get; set; }
        public bool? bisstqc { get; set; } = false;
        public int? vt_id { get; set; }
        public string cdefine11 { get; set; }
        public string cdefine12 { get; set; }
        public string cdefine13 { get; set; }
        public string cdefine14 { get; set; }
        public int? cdefine15 { get; set; }
        public decimal? cdefine16 { get; set; }
        public string cpspcode { get; set; }
        public string cmpocode { get; set; }
        public bool? bpufirst { get; set; } = false;
        public bool? biafirst { get; set; } = false;
        public int? iproorderid { get; set; }
        public int? biscomplement { get; set; } = 0;
        public int? ireturncount { get; set; } = 0;
        public DateTime? dnmaketime { get; set; } = DateTime.Now;
        public int? bmotran { get; set; } = 0;
        public int? iPrintCount { get; set; } = 0;
        public string editprop { get; set; }
    }

    internal sealed class GeneralVoucherBody
    {
        public string cinvcode { get; set; }
        public string cposition { get; set; }
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
        public string cbatch { get; set; }
        public decimal? iinvexchrate { get; set; }
        public decimal? inum { get; set; }
        public decimal? iquantity { get; set; }
        public decimal? iunitcost { get; set; }
        public decimal? iprice { get; set; }
        public decimal? inquantity { get; set; }
        public decimal? innum { get; set; }
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
        public long? impoids { get; set; }
        public string cposname { get; set; }
        public string cmocode { get; set; }
        public int? imoseq { get; set; }
        public string iopseq { get; set; }
        public string copdesc { get; set; }
        public string cmworkcentercode { get; set; }
        public string invcode { get; set; }
        public int? isotype { get; set; }
        public int? iordertype { get; set; }
        public int? iorderdid { get; set; }
        public string iordercode { get; set; }
        public int? iorderseq { get; set; }
        public bool? bcosting { get; set; } = true;
        public bool? bvmiused { get; set; } = false;
        public int? iexpiratdatecalcu { get; set; } = 0;
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
        public long? ipesodid { get; set; }
        public int? ipesotype { get; set; }
        public string cpesocode { get; set; }
        public int? ipesoseq { get; set; }
        public int? bcanreplace { get; set; } = 0;
        public int? iposflag { get; set; }
        public int? boutmaterials { get; set; } = 0;
        public string editprop { get; set; } = "A";
    }

    internal sealed class GeneralVoucherXmlBuilder
    {
        private readonly string _headTemplatePath;
        private readonly string _bodyTemplatePath;

        public GeneralVoucherXmlBuilder(string applicationDirectory)
        {
            _headTemplatePath = Path.Combine(applicationDirectory, "XML", "head.xml");
            _bodyTemplatePath = Path.Combine(applicationDirectory, "XML", "body.xml");
        }

        public GeneralVoucherXml Build(
            GeneralVoucherHead head,
            IList<GeneralVoucherBody> lines)
        {
            if (!File.Exists(_headTemplatePath) || !File.Exists(_bodyTemplatePath))
            {
                throw new FileNotFoundException(
                    "找不到 XML/head.xml 或 XML/body.xml 模板。");
            }

            string bodyRows = string.Concat(lines.Select(line =>
                "<z:row " + BuildAttributes(line) + " />"));
            return new GeneralVoucherXml
            {
                Head = File.ReadAllText(_headTemplatePath)
                    .Replace("@z_row", BuildAttributes(head)),
                Body = File.ReadAllText(_bodyTemplatePath)
                    .Replace("@z_row", bodyRows)
            };
        }

        private static string BuildAttributes(object value)
        {
            var result = new StringBuilder();
            foreach (PropertyInfo property in value.GetType().GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                result.Append(property.Name);
                result.Append("=\"");
                result.Append(SecurityElement.Escape(
                    FormatValue(property.GetValue(value, null))));
                result.Append("\" ");
            }
            return result.ToString();
        }

        private static string FormatValue(object value)
        {
            if (value == null) return string.Empty;
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
