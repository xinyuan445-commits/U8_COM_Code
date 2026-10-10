using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using Dapper;
using MaterialOutboundApi.Models;

namespace MaterialOutboundApi.Services
{
    internal sealed class AllocationRow
    {
        public int MoId { get; set; }
        public string MoCode { get; set; }
        public int MoDId { get; set; }
        public int? SortSeq { get; set; }
        public string ProductCode { get; set; }
        public string ProductName { get; set; }
        public string DepartmentCode { get; set; }
        public int AllocateId { get; set; }
        public string OpSeq { get; set; }
        public string MaterialCode { get; set; }
        public string MaterialName { get; set; }
        public decimal PlannedQuantity { get; set; }
        public bool RequiresBatch { get; set; }
        public decimal IssuedQuantity { get; set; }
    }

    internal sealed class WarehouseRow
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public bool UsesPosition { get; set; }
    }

    internal sealed class PositionRow
    {
        public string Code { get; set; }
        public string Name { get; set; }
    }

    internal sealed class TraceBackLabelRow
    {
        public int TraceBackOnlyCodeId { get; set; }
        public string TraceBackCode { get; set; }
        public string Sn { get; set; }
        public string QuantityText { get; set; }
        public string PdaOrderUuid { get; set; }
        public string PdaGoodsUuid { get; set; }
        public string GoodsJson { get; set; }
        public string OrderJson { get; set; }
    }

    internal sealed class InboundUnitCostKey
    {
        public string MaterialCode { get; set; }
        public string BatchCode { get; set; }
    }

    internal sealed class InboundUnitCostRow
    {
        public string MaterialCode { get; set; }
        public string BatchCode { get; set; }
        public decimal UnitCost { get; set; }
    }

    internal sealed class ApiRepository
    {
        private readonly string _connectionString;

        public ApiRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public void TestConnection()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                connection.ExecuteScalar<int>("select 1");
                connection.ExecuteScalar<int>(
                    "select top 1 1 from dbo.momlist_yx");
            }
        }

        public List<string> GetAccountDatabases()
        {
            const string sql = @"
select name
from sys.databases
where state = 0
  and name like 'UFDATA[_]%'
order by name";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<string>(sql).ToList();
            }
        }

        public List<WorkOrderLineDto> SearchWorkOrderLines(
            string keyword,
            int? sortSeq,
            bool includeInactive = false)
        {
            const string sql = @"
select
    v.MoId_a as MoId,
    v.MoCode_a as WorkOrderCode,
    v.MoDId_b as MoDId,
    v.SortSeq_b as LineNumber,
    v.InvCode_b as ProductCode,
    isnull(p.cInvName, '') as ProductName,
    v.Qty_b as OrderQuantity,
    v.MDeptCode_b as DepartmentCode,
    v.Status_b as Status,
    v.RelsDate_b as ReleaseDate,
    count(a.AllocateId) as MaterialCount
from dbo.momlist_yx v
left join dbo.mom_moallocate a on a.MoDId = v.MoDId_b
left join dbo.Inventory p on p.cInvCode = v.InvCode_b
where
    (@includeInactive = 1 or (v.Status_b = 3 and v.RelsDate_b is not null))
    and
    (
        (@sortSeq is not null
         and v.MoCode_a = @keyword
         and v.SortSeq_b = @sortSeq)
        or
        (@sortSeq is null and v.MoCode_a like '%' + @keyword + '%')
    )
group by
    v.MoId_a, v.MoCode_a, v.MoDId_b, v.SortSeq_b, v.InvCode_b,
    p.cInvName, v.Qty_b, v.MDeptCode_b, v.Status_b, v.RelsDate_b
having @includeInactive = 1 or count(a.AllocateId) > 0
order by v.MoCode_a desc, v.SortSeq_b, v.MoDId_b
offset 0 rows fetch next 50 rows only";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<WorkOrderLineDto>(
                    sql,
                    new { keyword, sortSeq, includeInactive }).ToList();
            }
        }

        public TraceBackLabelRow GetTraceBackLabel(string traceBackCode)
        {
            return GetTraceBackLabels(new[] { traceBackCode })
                .FirstOrDefault();
        }

        public List<TraceBackLabelRow> GetTraceBackLabels(
            ICollection<string> traceBackCodes)
        {
            if (traceBackCodes == null || traceBackCodes.Count == 0)
            {
                return new List<TraceBackLabelRow>();
            }

            const string sql = @"
select
    c.id as TraceBackOnlyCodeId,
    c.traceBackCode as TraceBackCode,
    c.sn as Sn,
    convert(varchar(100), totals.RemainingQuantity) as QuantityText,
    c.pdaOrderUuid as PdaOrderUuid,
    c.pdaGoodsUuid as PdaGoodsUuid,
    convert(nvarchar(max), g.json) as GoodsJson,
    convert(nvarchar(max), o.json) as OrderJson
from dbo.TraceBackOnlyCode c
inner join
(
    select
        flow.traceBackCode,
        max(flow.id) as LatestId,
        sum(
        try_convert(
            decimal(28, 6),
            nullif(ltrim(rtrim(flow.[sum])), '')
        )
    ) as RemainingQuantity
    from dbo.TraceBackOnlyCode flow
    where flow.traceBackCode in @traceBackCodes
    group by flow.traceBackCode
) totals on totals.LatestId = c.id
left join dbo.TraceBackGoods g
    on g.pdaGoodsUuid = c.pdaGoodsUuid
left join dbo.TraceBackOrder o
    on o.pdaOrderUuid = c.pdaOrderUuid
order by c.id desc";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<TraceBackLabelRow>(sql, new
                {
                    traceBackCodes
                }).ToList();
            }
        }

        public List<AllocationRow> GetAllocations(
            string workOrderCode,
            ICollection<int> selectedMoDIds)
        {
            const string sql = @"
select
    v.MoId_a as MoId,
    v.MoCode_a as MoCode,
    v.MoDId_b as MoDId,
    v.SortSeq_b as SortSeq,
    v.InvCode_b as ProductCode,
    isnull(p.cInvName, '') as ProductName,
    v.MDeptCode_b as DepartmentCode,
    a.AllocateId,
    a.OpSeq,
    a.InvCode as MaterialCode,
    isnull(i.cInvName, '') as MaterialName,
    isnull(a.Qty, 0) as PlannedQuantity,
    cast(isnull(i.bInvBatch, 0) as bit) as RequiresBatch,
    isnull(a.IssQty, 0) as IssuedQuantity
from dbo.momlist_yx v
join dbo.mom_moallocate a on a.MoDId = v.MoDId_b
left join dbo.Inventory p on p.cInvCode = v.InvCode_b
left join dbo.Inventory i on i.cInvCode = a.InvCode
where v.MoCode_a = @workOrderCode
  and v.MoDId_b in @selectedMoDIds
  and v.Status_b = 3
  and v.RelsDate_b is not null
order by v.SortSeq_b, a.OpSeq, a.AllocateId";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<AllocationRow>(
                    sql,
                    new { workOrderCode, selectedMoDIds }).ToList();
            }
        }

        public WarehouseRow GetWarehouse(string warehouseCode)
        {
            const string sql = @"
select
    cWhCode as Code,
    cWhName as Name,
    cast(isnull(bWhPos, 0) as bit) as UsesPosition
from dbo.Warehouse
where cWhCode = @warehouseCode";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<WarehouseRow>(
                    sql,
                    new { warehouseCode }).FirstOrDefault();
            }
        }

        public PositionRow GetPosition(
            string warehouseCode,
            string positionCode)
        {
            const string sql = @"
select cPosCode as Code, cPosName as Name
from dbo.Position
where cWhCode = @warehouseCode and cPosCode = @positionCode";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<PositionRow>(
                    sql,
                    new { warehouseCode, positionCode }).FirstOrDefault();
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

        public List<InboundUnitCostRow> GetLatestInboundUnitCosts(
            ICollection<InboundUnitCostKey> keys)
        {
            if (keys == null || keys.Count == 0)
            {
                return new List<InboundUnitCostRow>();
            }

            var parameters = new DynamicParameters();
            var values = new List<string>();
            int index = 0;
            foreach (InboundUnitCostKey key in keys)
            {
                string materialParameter = "material" + index;
                string batchParameter = "batch" + index;
                values.Add("(@" + materialParameter + ", @" + batchParameter + ")");
                parameters.Add(materialParameter, (key.MaterialCode ?? string.Empty).Trim());
                parameters.Add(batchParameter, (key.BatchCode ?? string.Empty).Trim());
                index++;
            }

            var sql = new StringBuilder();
            sql.AppendLine("select requested.MaterialCode, requested.BatchCode,");
            sql.AppendLine("       isnull(latest.iInCost, 0) as UnitCost");
            sql.AppendLine("from (values");
            sql.AppendLine(string.Join(",\r\n", values));
            sql.AppendLine(") requested(MaterialCode, BatchCode)");
            sql.AppendLine("outer apply");
            sql.AppendLine("(");
            sql.AppendLine("    select top (1) subsidiary.iInCost");
            sql.AppendLine("    from dbo.IA_Subsidiary subsidiary");
            sql.AppendLine("    where ltrim(rtrim(subsidiary.cInvCode)) = requested.MaterialCode");
            sql.AppendLine("      and isnull(ltrim(rtrim(subsidiary.cBatchIA)), '') = requested.BatchCode");
            sql.AppendLine("      and isnull(subsidiary.iAInQuantity, 0) > 0");
            sql.AppendLine("    order by subsidiary.dVouDate desc, subsidiary.AutoID desc");
            sql.AppendLine(") latest");

            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<InboundUnitCostRow>(
                    sql.ToString(),
                    parameters).ToList();
            }
        }

        public int? GetVoucherId(string voucherCode)
        {
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<int?>(
                    "select top 1 ID from dbo.RdRecord11 where cCode=@voucherCode",
                    new { voucherCode }).FirstOrDefault();
            }
        }

        public string GetVoucherCodeById(int voucherId)
        {
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<string>(
                    "select top 1 cCode from dbo.RdRecord11 where ID=@voucherId",
                    new { voucherId }).FirstOrDefault();
            }
        }

        public MaterialOutboundDto GetMaterialOutbound(string voucherCode)
        {
            const string headSql = @"
select top 1
    ID,
    cCode as VoucherCode,
    dDate as VoucherDate,
    cMPoCode as WorkOrderCode,
    cWhCode as WarehouseCode,
    cDepCode as DepartmentCode,
    cMaker as Maker,
    cHandler as Auditor,
    dVeriDate as AuditDate
from dbo.RdRecord11
where cCode=@voucherCode";
            const string bodySql = @"
select
    AutoID,
    iRowNo as LineNumber,
    cInvCode as MaterialCode,
    isnull(iQuantity,0) as Quantity,
    isnull(iUnitCost,0) as UnitCost,
    isnull(iPrice,0) as Price,
    cBatch as BatchCode,
    cPosition as PositionCode,
    iMPoIds as AllocateId
from dbo.RdRecords11
where ID=@id
order by iRowNo, AutoID";
            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                var result = connection.Query<MaterialOutboundDto>(
                    headSql,
                    new { voucherCode }).FirstOrDefault();
                if (result == null)
                {
                    return null;
                }
                result.IsVerified = !string.IsNullOrWhiteSpace(result.Auditor);
                result.Lines = connection.Query<MaterialOutboundLineDto>(
                    bodySql,
                    new { id = result.Id }).ToList();
                return result;
            }
        }
    }
}
