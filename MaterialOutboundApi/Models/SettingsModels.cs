using System;
using System.ComponentModel.DataAnnotations;

namespace MaterialOutboundApi.Models
{
    public sealed class ShapeConversionProfile
    {
        public string DepartmentCode { get; set; }
        public string InboundRdCode { get; set; }
        public string OutboundRdCode { get; set; }
        public int VoucherTemplateId { get; set; } = 91;

        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(DepartmentCode) &&
            !string.IsNullOrWhiteSpace(InboundRdCode) &&
            !string.IsNullOrWhiteSpace(OutboundRdCode) &&
            VoucherTemplateId > 0;
    }

    public sealed class ApiSettings
    {
        public string SqlServer { get; set; } = "";
        public string SqlDatabase { get; set; } = "UFDATA_998_2021";
        public string SqlUser { get; set; } = "";
        public string SqlPassword { get; set; }
        public string U8Server { get; set; } = "";
        public string U8AccId { get; set; } = "";
        public string U8Year { get; set; } = "2026";
        public string U8SubId { get; set; } = "ST";
        public string U8UserId { get; set; } = "";
        public string U8Password { get; set; }
        public string U8Serial { get; set; } = "";
        public string WarehouseCode { get; set; } = "KML_PP";
        public string PositionCode { get; set; } = "PP2";
        public string Maker { get; set; } = "";
        public string DefaultDepartmentCode { get; set; } = "";
        public string WorkCenterCode { get; set; } = "";
        public string RdCode { get; set; } = "202";
        public int VoucherTemplateId { get; set; } = 65;
        public decimal DefaultUnitCost { get; set; } = 0m;
        public ShapeConversionProfile Shape008 { get; set; } =
            new ShapeConversionProfile();
        public ShapeConversionProfile Shape998 { get; set; } =
            new ShapeConversionProfile
            {
                DepartmentCode = "04125",
                InboundRdCode = "112",
                OutboundRdCode = "216",
                VoucherTemplateId = 91
            };
        public string ApiKey { get; set; }
        public string AdminPasswordHash { get; set; }
        public string AdminPasswordSalt { get; set; }
        public int Revision { get; set; }

        public string BuildConnectionString(string database = null)
        {
            var builder = new System.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = SqlServer,
                InitialCatalog = string.IsNullOrWhiteSpace(database)
                    ? SqlDatabase
                    : database,
                UserID = SqlUser,
                Password = SqlPassword,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = 10,
                MaxPoolSize = 100
            };
            return builder.ConnectionString;
        }

        public ApiSettings ForDatabase(string database)
        {
            string normalized =
                MaterialOutboundApi.Infrastructure.U8DatabaseContext.Normalize(database);
            if (normalized == null)
            {
                return this;
            }

            var copy = (ApiSettings)MemberwiseClone();
            copy.SqlDatabase = normalized;
            string accountCode = normalized.Substring("UFDATA_".Length, 3);
            int separator = (U8AccId ?? string.Empty).LastIndexOf('@');
            string prefix = separator >= 0
                ? U8AccId.Substring(0, separator)
                : "(default)";
            copy.U8AccId = prefix + "@" + accountCode;
            return copy;
        }

        public ShapeConversionProfile ShapeProfileForDatabase(string database)
        {
            string normalized =
                MaterialOutboundApi.Infrastructure.U8DatabaseContext.Normalize(database)
                ?? SqlDatabase;
            return normalized != null && normalized.StartsWith(
                    "UFDATA_998_",
                    StringComparison.OrdinalIgnoreCase)
                ? Shape998
                : Shape008;
        }
    }

    public sealed class SettingsInput
    {
        [Required] public string SqlServer { get; set; }
        [Required] public string SqlDatabase { get; set; }
        [Required] public string SqlUser { get; set; }
        public string SqlPassword { get; set; }
        [Required] public string U8Server { get; set; }
        [Required] public string U8AccId { get; set; }
        [Required]
        [RegularExpression(@"^\d{4}$")]
        public string U8Year { get; set; }
        [Required] public string U8SubId { get; set; }
        [Required] public string U8UserId { get; set; }
        public string U8Password { get; set; }
        public string U8Serial { get; set; }
        [Required] public string WarehouseCode { get; set; }
        public string PositionCode { get; set; }
        [Required] public string Maker { get; set; }
        public string DefaultDepartmentCode { get; set; }
        public string WorkCenterCode { get; set; }
        [Required] public string RdCode { get; set; }
        [Range(1, int.MaxValue)]
        public int VoucherTemplateId { get; set; } = 65;
        [Range(typeof(decimal), "0", "79228162514264337593543950335")]
        public decimal DefaultUnitCost { get; set; }
        public string Shape008DepartmentCode { get; set; }
        public string Shape008InboundRdCode { get; set; }
        public string Shape008OutboundRdCode { get; set; }
        [Range(1, int.MaxValue)] public int Shape008VoucherTemplateId { get; set; } = 91;
        public string Shape998DepartmentCode { get; set; }
        public string Shape998InboundRdCode { get; set; }
        public string Shape998OutboundRdCode { get; set; }
        [Range(1, int.MaxValue)] public int Shape998VoucherTemplateId { get; set; } = 91;
        public string AdminPassword { get; set; }
        public string TestDate { get; set; }
    }

    public sealed class SettingsView
    {
        public bool Configured { get; set; }
        public string SqlServer { get; set; }
        public string SqlDatabase { get; set; }
        public string SqlUser { get; set; }
        public bool HasSqlPassword { get; set; }
        public string U8Server { get; set; }
        public string U8AccId { get; set; }
        public string U8Year { get; set; }
        public string U8SubId { get; set; }
        public string U8UserId { get; set; }
        public bool HasU8Password { get; set; }
        public string U8Serial { get; set; }
        public string WarehouseCode { get; set; }
        public string PositionCode { get; set; }
        public string Maker { get; set; }
        public string DefaultDepartmentCode { get; set; }
        public string WorkCenterCode { get; set; }
        public string RdCode { get; set; }
        public int VoucherTemplateId { get; set; }
        public decimal DefaultUnitCost { get; set; }
        public string Shape008DepartmentCode { get; set; }
        public string Shape008InboundRdCode { get; set; }
        public string Shape008OutboundRdCode { get; set; }
        public int Shape008VoucherTemplateId { get; set; }
        public string Shape998DepartmentCode { get; set; }
        public string Shape998InboundRdCode { get; set; }
        public string Shape998OutboundRdCode { get; set; }
        public int Shape998VoucherTemplateId { get; set; }
        public string ApiKeyMasked { get; set; }
        public int Revision { get; set; }
    }
}
