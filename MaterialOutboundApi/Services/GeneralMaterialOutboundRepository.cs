using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Dapper;

namespace MaterialOutboundApi.Services
{
    internal sealed class GeneralInventoryRow
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public bool RequiresBatch { get; set; }
    }

    internal sealed class GeneralCodeRow
    {
        public string Code { get; set; }
        public string Name { get; set; }
    }

    internal sealed class GeneralMaterialOutboundRepository
    {
        private readonly string _connectionString;

        public GeneralMaterialOutboundRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public GeneralInventoryRow GetInventory(string materialCode)
        {
            const string sql = @"
select top 1
    cInvCode as Code,
    isnull(cInvName, '') as Name,
    cast(isnull(bInvBatch, 0) as bit) as RequiresBatch
from dbo.Inventory
where cInvCode = @materialCode";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<GeneralInventoryRow>(
                    sql,
                    new { materialCode }).FirstOrDefault();
            }
        }

        public GeneralCodeRow GetDepartment(string departmentCode)
        {
            const string sql = @"
select top 1 cDepCode as Code, isnull(cDepName, '') as Name
from dbo.Department
where cDepCode = @departmentCode";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<GeneralCodeRow>(
                    sql,
                    new { departmentCode }).FirstOrDefault();
            }
        }

        public GeneralCodeRow GetRdStyle(string rdCode)
        {
            const string sql = @"
select top 1 cRdCode as Code, isnull(cRdName, '') as Name
from dbo.rd_style
where cRdCode = @rdCode";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<GeneralCodeRow>(
                    sql,
                    new { rdCode }).FirstOrDefault();
            }
        }

        public decimal GetAvailableStock(
            string warehouseCode,
            string materialCode,
            string batchCode)
        {
            const string sql = @"
select isnull(sum(
    isnull(iQuantity, 0) - isnull(fOutQuantity, 0)
), 0)
from dbo.CurrentStock
where cWhCode = @warehouseCode
  and cInvCode = @materialCode
  and isnull(cBatch, '') = isnull(@batchCode, '')";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.ExecuteScalar<decimal>(sql, new
                {
                    warehouseCode,
                    materialCode,
                    batchCode = batchCode ?? string.Empty
                });
            }
        }

        public decimal GetLatestInboundUnitCost(
            string materialCode,
            string batchCode)
        {
            const string sql = @"
select isnull((
    select top (1) subsidiary.iInCost
    from dbo.IA_Subsidiary subsidiary
    where ltrim(rtrim(subsidiary.cInvCode)) = @materialCode
      and isnull(ltrim(rtrim(subsidiary.cBatchIA)), '') = isnull(@batchCode, '')
      and isnull(subsidiary.iAInQuantity, 0) > 0
    order by subsidiary.dVouDate desc, subsidiary.AutoID desc
), 0)";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.ExecuteScalar<decimal>(sql, new
                {
                    materialCode,
                    batchCode = batchCode ?? string.Empty
                });
            }
        }
    }
}
