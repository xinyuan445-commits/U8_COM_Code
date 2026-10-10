using System;
using System.Configuration;
using System.Globalization;
using MaterialOutboundConsole.Models;

namespace MaterialOutboundConsole.Configuration
{
    internal sealed class AppOptions
    {
        public string ConnectionString { get; private set; }
        public U8Account Account { get; private set; }
        public string WarehouseCode { get; private set; }
        public string DepartmentCode { get; private set; }
        public string Maker { get; private set; }
        public string WorkCenterCode { get; private set; }
        public string DefaultPositionCode { get; private set; }
        public string DefaultBatchCode { get; private set; }
        public decimal DefaultUnitCost { get; private set; }
        public DateTime VoucherDate { get; private set; }

        public static AppOptions Load(Func<string, string> readSecret)
        {
            var connection = ConfigurationManager.ConnectionStrings["U8Database"];
            if (connection == null)
            {
                throw new ConfigurationErrorsException(
                    "请先在 MaterialOutboundConsole.exe.config 中配置 U8Database 连接字符串。");
            }

            var connectionBuilder =
                new System.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString);
            if (string.IsNullOrWhiteSpace(connectionBuilder.Password) ||
                IsPlaceholder(connectionBuilder.Password))
            {
                if (readSecret == null)
                {
                    throw new ConfigurationErrorsException("未提供 SQL Server 密码。");
                }

                connectionBuilder.Password = readSecret("请输入 SQL Server 密码：");
            }

            var options = new AppOptions
            {
                ConnectionString = connectionBuilder.ConnectionString,
                Account = new U8Account
                {
                    SubId = Required("U8SubId"),
                    AccId = Required("U8AccId"),
                    Year = Required("U8Year"),
                    UserId = Required("U8UserId"),
                    Password = Optional("U8Password"),
                    Date = Required("U8Date"),
                    Server = Required("U8Server"),
                    Serial = Optional("U8Serial")
                },
                WarehouseCode = Optional("WarehouseCode"),
                DepartmentCode = Optional("DepartmentCode"),
                Maker = Required("Maker"),
                WorkCenterCode = Optional("WorkCenterCode"),
                DefaultPositionCode = Optional("DefaultPositionCode"),
                DefaultBatchCode = Required("DefaultBatchCode")
            };

            decimal unitCost;
            if (!decimal.TryParse(
                    ConfigurationManager.AppSettings["DefaultUnitCost"],
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out unitCost))
            {
                throw new ConfigurationErrorsException("DefaultUnitCost 必须是有效数字。");
            }

            options.DefaultUnitCost = unitCost;

            DateTime voucherDate;
            if (!DateTime.TryParseExact(
                    options.Account.Date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out voucherDate))
            {
                throw new ConfigurationErrorsException("U8Date 必须使用 yyyy-MM-dd 格式。");
            }

            options.VoucherDate = voucherDate;
            return options;
        }

        private static string Required(string key)
        {
            var value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value) || IsPlaceholder(value))
            {
                throw new ConfigurationErrorsException("配置项 " + key + " 尚未填写。");
            }

            return value.Trim();
        }

        private static string Optional(string key)
        {
            var value = ConfigurationManager.AppSettings[key];
            return value == null || IsPlaceholder(value)
                ? string.Empty
                : value.Trim();
        }

        private static bool IsPlaceholder(string value)
        {
            return value != null &&
                   value.IndexOf("YOUR_", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
