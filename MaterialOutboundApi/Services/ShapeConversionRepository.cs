using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using MaterialOutboundApi.Models;

namespace MaterialOutboundApi.Services
{
    internal sealed class ShapeConversionRepository
    {
        private readonly string _connectionString;

        public ShapeConversionRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public ShapeConversionPreviewDto Resolve(
            ShapeConversionPreviewRequest request,
            ShapeConversionProfile profile,
            DateTime date)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                return new ShapeConversionPreviewDto
                {
                    VoucherDate = date,
                    DepartmentCode = profile.DepartmentCode,
                    DepartmentName = RequiredName(connection, "Department", "cDepCode", "cDepName", profile.DepartmentCode, "部门"),
                    InboundRdCode = profile.InboundRdCode,
                    InboundRdName = RequiredName(connection, "Rd_Style", "cRdCode", "cRdName", profile.InboundRdCode, "入库类别"),
                    OutboundRdCode = profile.OutboundRdCode,
                    OutboundRdName = RequiredName(connection, "Rd_Style", "cRdCode", "cRdName", profile.OutboundRdCode, "出库类别"),
                    VoucherTemplateId = profile.VoucherTemplateId,
                    Source = ResolveLine(connection, request.Source, "转换前"),
                    Target = ResolveLine(connection, request.Target, "转换后")
                };
            }
        }

        public ShapeConversionPreviewDto ResolveImport(
            ShapeConversionImportPreviewRequest request,
            ShapeConversionProfile profile,
            DateTime date)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var cache = new Dictionary<string, ShapeConversionResolvedLineDto>(StringComparer.OrdinalIgnoreCase);
                var pairs = new List<ShapeConversionResolvedPairDto>();
                for (int index = 0; index < request.Pairs.Count; index++)
                {
                    ShapeConversionPairRequest pair = request.Pairs[index];
                    int groupNumber = index + 1;
                    pairs.Add(new ShapeConversionResolvedPairDto
                    {
                        GroupNumber = groupNumber,
                        Source = ResolveCachedLine(connection, pair.Source, "第 " + groupNumber + " 组转换前", cache),
                        Target = ResolveCachedLine(connection, pair.Target, "第 " + groupNumber + " 组转换后", cache)
                    });
                }
                return new ShapeConversionPreviewDto
                {
                    VoucherDate = date,
                    DepartmentCode = profile.DepartmentCode,
                    DepartmentName = RequiredName(connection, "Department", "cDepCode", "cDepName", profile.DepartmentCode, "部门"),
                    InboundRdCode = profile.InboundRdCode,
                    InboundRdName = RequiredName(connection, "Rd_Style", "cRdCode", "cRdName", profile.InboundRdCode, "入库类别"),
                    OutboundRdCode = profile.OutboundRdCode,
                    OutboundRdName = RequiredName(connection, "Rd_Style", "cRdCode", "cRdName", profile.OutboundRdCode, "出库类别"),
                    VoucherTemplateId = profile.VoucherTemplateId,
                    Pairs = pairs
                };
            }
        }

        public void ValidateProfile(ShapeConversionProfile profile)
        {
            if (profile == null || !profile.IsComplete)
            {
                throw new InvalidOperationException("形态转换固定参数不完整。");
            }
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                RequiredName(connection, "Department", "cDepCode", "cDepName", profile.DepartmentCode, "部门");
                RequiredName(connection, "Rd_Style", "cRdCode", "cRdName", profile.InboundRdCode, "入库类别");
                RequiredName(connection, "Rd_Style", "cRdCode", "cRdName", profile.OutboundRdCode, "出库类别");
                if (connection.ExecuteScalar<int>("select count(1) from sys.tables where object_id=object_id(N'dbo.AssemVouch')") != 1)
                {
                    throw new InvalidOperationException("当前账套缺少形态转换单表 AssemVouch。");
                }
            }
        }

        public ShapeVoucherStatusDto GetShapeVoucher(string code, long? id = null)
        {
            const string sql = @"
select top (1) '15' VoucherType, N'形态转换单' Name, ID, cAVCode Code,
       convert(bit, case when cVerifyPerson is not null and dVerifyDate is not null then 1 else 0 end) IsVerified
from dbo.AssemVouch
where (@id is not null and ID=@id) or (@code<>'' and cAVCode=@code)
order by case when @id is not null and ID=@id then 0 else 1 end, ID desc";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<ShapeVoucherStatusDto>(sql, new { code = code ?? string.Empty, id }).FirstOrDefault();
            }
        }

        public List<ShapeVoucherStatusDto> GetGeneratedVouchers(string code)
        {
            const string sql = @"
select VoucherType, Name, ID, Code, IsVerified from (
 select '09' VoucherType, N'其他出库单' Name, ID, cCode Code,
        convert(bit, case when cHandler is not null and dVeriDate is not null then 1 else 0 end) IsVerified,
        row_number() over(order by ID desc) RowNumber
 from dbo.RdRecord09 where cSource=N'形态转换' and cBusCode=@code
 union all
 select '08', N'其他入库单', ID, cCode,
        convert(bit, case when cHandler is not null and dVeriDate is not null then 1 else 0 end),
        row_number() over(order by ID desc)
 from dbo.RdRecord08 where cSource=N'形态转换' and cBusCode=@code
) x where RowNumber=1
order by case VoucherType when '09' then 0 else 1 end";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<ShapeVoucherStatusDto>(sql, new { code }).ToList();
            }
        }

        private static ShapeConversionResolvedLineDto ResolveLine(
            SqlConnection connection,
            ShapeConversionLineRequest input,
            string label)
        {
            string costing = HasColumn(connection, "Inventory", "bCosting") ? "cast(isnull(i.bCosting,1) as bit)" : "cast(1 as bit)";
            string expiration = HasColumn(connection, "Inventory", "iExpiratDateCalcu") ? "isnull(i.iExpiratDateCalcu,0)" : "cast(0 as int)";
            string shelfLife = HasColumn(connection, "Inventory", "iMassDate") ? "i.iMassDate" : "cast(null as int)";
            string shelfUnit = HasColumn(connection, "Inventory", "cMassUnit") ? "i.cMassUnit" : "cast(null as varchar(20))";
            string sql = @"
select top (1)
 w.cWhCode WarehouseCode, w.cWhName WarehouseName,
 i.cInvCode InventoryCode, i.cInvName InventoryName, i.cInvStd Specification,
 i.cInvDefine1 Define1, i.cInvDefine4 Define4, i.cInvDefine5 Define5, i.cInvDefine6 Define6,
 i.cComUnitCode UnitCode, u.cComUnitName UnitName,
 cast(isnull(i.bInvBatch,0) as bit) RequiresBatch,
 " + costing + @" IsCosting, " + expiration + @" ExpirationDateCalculation,
 " + shelfLife + @" ShelfLife, " + shelfUnit + @" ShelfLifeUnit,
 p.cPosCode PositionCode, p.cPosName PositionName
from dbo.Inventory i
cross join dbo.Warehouse w
left join dbo.ComputationUnit u on u.cComUnitCode=i.cComUnitCode
left join dbo.Position p on p.cWhCode=w.cWhCode and p.cPosCode=@position
where i.cInvCode=@inventory and w.cWhCode=@warehouse";
            var row = connection.Query<ShapeConversionResolvedLineDto>(sql, new
            {
                inventory = input.InventoryCode,
                warehouse = input.WarehouseCode,
                position = input.PositionCode
            }).FirstOrDefault();
            if (row == null) throw new InvalidOperationException(label + "的仓库或存货不存在。");
            if (string.IsNullOrWhiteSpace(row.PositionCode)) throw new InvalidOperationException(label + "货位不存在：" + input.WarehouseCode + "/" + input.PositionCode);
            if (row.RequiresBatch && string.IsNullOrWhiteSpace(input.BatchCode)) throw new InvalidOperationException(label + "存货启用批次管理，批号不能为空。");
            row.BatchCode = input.BatchCode;
            row.Quantity = input.Quantity;
            row.CurrentStock = connection.ExecuteScalar<decimal?>(@"
select sum(isnull(iQuantity,0)) from dbo.InvPositionSum
where cWhCode=@warehouse and cInvCode=@inventory
  and isnull(cBatch,'')=@batch and isnull(cPosCode,'')=@position", new
            {
                warehouse = input.WarehouseCode,
                inventory = input.InventoryCode,
                batch = input.BatchCode ?? string.Empty,
                position = input.PositionCode ?? string.Empty
            }) ?? 0m;
            return row;
        }

        private static ShapeConversionResolvedLineDto ResolveCachedLine(
            SqlConnection connection,
            ShapeConversionLineRequest input,
            string label,
            IDictionary<string, ShapeConversionResolvedLineDto> cache)
        {
            string key = string.Join("\u001f", new[]
            {
                input.WarehouseCode ?? string.Empty,
                input.InventoryCode ?? string.Empty,
                input.BatchCode ?? string.Empty,
                input.PositionCode ?? string.Empty
            });
            if (!cache.TryGetValue(key, out ShapeConversionResolvedLineDto resolved))
            {
                resolved = ResolveLine(connection, input, label);
                cache[key] = resolved;
            }
            return CopyLine(resolved, input.Quantity);
        }

        private static ShapeConversionResolvedLineDto CopyLine(
            ShapeConversionResolvedLineDto value,
            decimal quantity) => new ShapeConversionResolvedLineDto
        {
            WarehouseCode = value.WarehouseCode,
            WarehouseName = value.WarehouseName,
            InventoryCode = value.InventoryCode,
            InventoryName = value.InventoryName,
            Specification = value.Specification,
            BatchCode = value.BatchCode,
            PositionCode = value.PositionCode,
            PositionName = value.PositionName,
            UnitCode = value.UnitCode,
            UnitName = value.UnitName,
            Quantity = quantity,
            CurrentStock = value.CurrentStock,
            RequiresBatch = value.RequiresBatch,
            IsCosting = value.IsCosting,
            ExpirationDateCalculation = value.ExpirationDateCalculation,
            ShelfLife = value.ShelfLife,
            ShelfLifeUnit = value.ShelfLifeUnit,
            Define1 = value.Define1,
            Define4 = value.Define4,
            Define5 = value.Define5,
            Define6 = value.Define6
        };

        private static string RequiredName(SqlConnection connection, string table, string codeColumn, string nameColumn, string code, string label)
        {
            string sql = "select top (1) " + nameColumn + " from dbo." + table + " where " + codeColumn + "=@code";
            string value = connection.Query<string>(sql, new { code }).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(label + "不存在：" + code);
            return value;
        }

        private static bool HasColumn(SqlConnection connection, string table, string column) =>
            connection.ExecuteScalar<int>("select count(1) from sys.columns where object_id=object_id(@table) and name=@column", new { table = "dbo." + table, column }) > 0;
    }
}
