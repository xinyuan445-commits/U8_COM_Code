using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using MaterialOutboundConsole.Models;

namespace MaterialOutboundConsole.Services
{
    internal sealed class VoucherXml
    {
        public string Head { get; set; }
        public string Body { get; set; }
    }

    internal sealed class VoucherXmlBuilder
    {
        private readonly string _headTemplatePath;
        private readonly string _bodyTemplatePath;

        public VoucherXmlBuilder(string applicationDirectory)
        {
            _headTemplatePath = Path.Combine(applicationDirectory, "XML", "head.xml");
            _bodyTemplatePath = Path.Combine(applicationDirectory, "XML", "body.xml");
        }

        public VoucherXml Build(
            MaterialOutboundHead head,
            IList<MaterialOutboundBody> body)
        {
            if (!File.Exists(_headTemplatePath) || !File.Exists(_bodyTemplatePath))
            {
                throw new FileNotFoundException("找不到 XML/head.xml 或 XML/body.xml 模板。");
            }

            var headAttributes = BuildAttributes(head);
            var bodyRows = new StringBuilder();
            foreach (var item in body)
            {
                bodyRows.Append("<z:row ");
                bodyRows.Append(BuildAttributes(item));
                bodyRows.Append(" />");
            }

            return new VoucherXml
            {
                Head = File.ReadAllText(_headTemplatePath).Replace("@z_row", headAttributes),
                Body = File.ReadAllText(_bodyTemplatePath).Replace("@z_row", bodyRows.ToString())
            };
        }

        private static string BuildAttributes<T>(T value)
        {
            var result = new StringBuilder();
            foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                result.Append(property.Name);
                result.Append("=\"");
                result.Append(SecurityElement.Escape(FormatValue(property.GetValue(value, null))));
                result.Append("\" ");
            }

            return result.ToString();
        }

        private static string FormatValue(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is DateTime)
            {
                return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value is bool)
            {
                return ((bool)value) ? "True" : "False";
            }

            var formattable = value as IFormattable;
            return formattable == null
                ? value.ToString()
                : formattable.ToString(null, CultureInfo.InvariantCulture);
        }
    }
}
