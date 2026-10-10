using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using MaterialOutboundConsole.Models;

namespace MaterialOutboundConsole.Data
{
    internal sealed class U8Repository
    {
        private readonly string _connectionString;

        public U8Repository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public MaterialOutboundRecord GetMaterialOutbound(string code)
        {
            const string sql =
                "select top 1 d.ID, d.cCode from rdrecord11 d where d.cCode = @code";

            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<MaterialOutboundRecord>(sql, new { code }).FirstOrDefault();
            }
        }

        public ShapeConversionRecord GetShapeConversion(string code)
        {
            const string sql = @"
select top (1)
    ID,
    cAVCode as Code
from dbo.AssemVouch
where cAVCode = @code
  and cVouchType = N'15'";

            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<ShapeConversionRecord>(
                    sql,
                    new { code }).FirstOrDefault();
            }
        }

        public List<MaterialOutboundRecord> GetMaterialOutboundLines(string code)
        {
            const string sql = @"
select
    r.ID,
    r.cCode,
    r.cPsPcode,
    r.cMPoCode,
    r.iproorderid,
    r.cMaker,
    r.cWhCode,
    r.cDepCode,
    s.ID as PId,
    s.AutoID,
    s.irowno,
    s.invcode,
    s.cInvCode,
    s.iQuantity,
    s.iUnitCost,
    s.iPrice,
    s.iNQuantity,
    s.cBatch,
    s.iMPoIds,
    s.cmocode,
    s.imoseq,
    s.iopseq,
    s.copdesc,
    s.cmworkcentercode,
    s.cPosition,
    p.cPosName,
    s.iPosFlag
from rdrecord11 r
join rdrecords11 s on r.ID = s.ID
left join Position p on p.cPosCode = s.cPosition and p.cWhCode = r.cWhCode
where r.cCode = @code
order by s.irowno, s.AutoID";

            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<MaterialOutboundRecord>(sql, new { code }).ToList();
            }
        }

        public List<ProductionMaterial> GetProductionMaterials(string productionOrderCode)
        {
            return GetProductionMaterials(productionOrderCode, null);
        }

        public List<WorkOrderLine> GetWorkOrderLines(string productionOrderCode)
        {
            const string sql = @"
select
    v.MoId_a as MoId,
    v.MoCode_a as MoCode,
    v.MoDId_b as MoDId,
    v.SortSeq_b as SortSeq,
    v.InvCode_b as InvCode,
    v.Qty_b as Qty,
    v.MDeptCode_b as DepartmentCode,
    v.Status_b as Status,
    v.RelsDate_b as ReleaseDate,
    count(a.AllocateId) as AllocationCount
from dbo.momlist_yx v
left join dbo.mom_moallocate a on a.MoDId = v.MoDId_b
where v.MoCode_a = @productionOrderCode
group by
    v.MoId_a,
    v.MoCode_a,
    v.MoDId_b,
    v.SortSeq_b,
    v.InvCode_b,
    v.Qty_b,
    v.MDeptCode_b,
    v.Status_b,
    v.RelsDate_b
order by v.SortSeq_b, v.MoDId_b";

            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<WorkOrderLine>(
                    sql,
                    new { productionOrderCode }).ToList();
            }
        }

        public List<ProductionMaterial> GetProductionMaterials(
            string productionOrderCode,
            ICollection<int> selectedMoDIds)
        {
            var sql = @"
select
    v.MoId_a as MoId,
    v.MoCode_a as MoCode,
    v.MoDId_b as MoDId,
    v.InvCode_b as InvCode,
    v.Qty_b as Qty,
    v.SortSeq_b as SortSeq,
    v.MDeptCode_b as DepartmentCode,
    a.AllocateId,
    a.OpSeq,
    a.InvCode as CInvCode,
    a.BaseQtyN,
    a.BaseQtyD,
    a.CompScrap
from dbo.momlist_yx v
join dbo.mom_moallocate a on a.MoDId = v.MoDId_b
where v.MoCode_a = @productionOrderCode";

            if (selectedMoDIds != null)
            {
                if (selectedMoDIds.Count == 0)
                {
                    return new List<ProductionMaterial>();
                }

                sql += "\nand v.MoDId_b in @selectedMoDIds";
            }

            sql += "\norder by v.SortSeq_b, a.OpSeq, a.AllocateId";

            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<ProductionMaterial>(
                    sql,
                    new { productionOrderCode, selectedMoDIds }).ToList();
            }
        }

        public WarehouseInfo GetWarehouse(string warehouseCode)
        {
            const string sql = @"
select
    cWhCode as WarehouseCode,
    cWhName as WarehouseName,
    cast(isnull(bWhPos, 0) as bit) as UsesPosition
from dbo.Warehouse
where cWhCode = @warehouseCode";

            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<WarehouseInfo>(
                    sql,
                    new { warehouseCode }).FirstOrDefault();
            }
        }

        public PositionInfo GetPosition(
            string warehouseCode,
            string positionCode)
        {
            const string sql = @"
select
    cPosCode as PositionCode,
    cPosName as PositionName,
    cWhCode as WarehouseCode
from dbo.Position
where cWhCode = @warehouseCode
  and cPosCode = @positionCode
  and isnull(bPosEnd, 0) = 1";

            using (IDbConnection connection = new SqlConnection(_connectionString))
            {
                return connection.Query<PositionInfo>(
                    sql,
                    new { warehouseCode, positionCode }).FirstOrDefault();
            }
        }
    }
}
