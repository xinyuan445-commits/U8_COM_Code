using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using GeneralInventoryCount.Models;

namespace MaterialOutboundApi.Services
{
    internal sealed class InventoryCountInventoryRow
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public bool RequiresBatch { get; set; }
        public bool RequiresFree1 { get; set; }
        public bool RequiresFree2 { get; set; }
        public bool RequiresFree3 { get; set; }
        public bool RequiresFree4 { get; set; }
        public bool RequiresFree5 { get; set; }
        public bool RequiresFree6 { get; set; }
        public bool RequiresFree7 { get; set; }
        public bool RequiresFree8 { get; set; }
        public bool RequiresFree9 { get; set; }
        public bool RequiresFree10 { get; set; }
        public bool RequiresAuxiliaryUnit { get; set; }
        public string AuxiliaryUnitCode { get; set; }
        public string AuxiliaryUnitName { get; set; }
        public decimal? ExchangeRate { get; set; }
        public bool RequiresExpirationDate { get; set; }
        public int? MassDate { get; set; }
        public string MassUnit { get; set; }
    }

    internal sealed class InventoryCountBalanceRow
    {
        public decimal Quantity { get; set; }
        public decimal Number { get; set; }
    }

    internal sealed class InventoryCountCodeRow
    {
        public string Code { get; set; }
        public string Name { get; set; }
    }

    internal sealed class InventoryCountVoucherRow
    {
        public int Id { get; set; }
        public string VoucherCode { get; set; }
        public DateTime VoucherDate { get; set; }
        public DateTime? CountDate { get; set; }
        public string WarehouseCode { get; set; }
        public string DepartmentCode { get; set; }
        public string InboundRdCode { get; set; }
        public string OutboundRdCode { get; set; }
        public string Maker { get; set; }
        public string Auditor { get; set; }
        public DateTime? AuditDate { get; set; }
        public string CountType { get; set; }
        public string Memo { get; set; }
    }

    internal sealed class InventoryCountVoucherLineRow
    {
        public int AutoId { get; set; }
        public int? LineNo { get; set; }
        public string InventoryCode { get; set; }
        public string InventoryName { get; set; }
        public string Specification { get; set; }
        public string UnitName { get; set; }
        public string BatchCode { get; set; }
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public decimal BookQuantity { get; set; }
        public decimal ActualQuantity { get; set; }
        public string AuxiliaryUnitCode { get; set; }
        public decimal? ExchangeRate { get; set; }
        public decimal? BookNumber { get; set; }
        public decimal? ActualNumber { get; set; }
        public decimal AdjustInQuantity { get; set; }
        public decimal AdjustOutQuantity { get; set; }
        public string Reason { get; set; }
        public string Remark { get; set; }
        public byte[] RowVersion { get; set; }
        public string LineVersion => AutoId + ":" +
            BitConverter.ToString(RowVersion ?? new byte[0]).Replace("-", "");
    }

    internal sealed class GeneralInventoryCountRepository
    {
        private readonly string _connectionString;

        public GeneralInventoryCountRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public InventoryCountInventoryRow GetInventory(string inventoryCode)
        {
            const string sql = @"
select top (1)
    inv.cInvCode as Code,
    isnull(inv.cInvName, '') as Name,
    cast(isnull(inv.bInvBatch, 0) as bit) as RequiresBatch,
    cast(isnull(inv.bFree1, 0) as bit) as RequiresFree1,
    cast(isnull(inv.bFree2, 0) as bit) as RequiresFree2,
    cast(isnull(inv.bFree3, 0) as bit) as RequiresFree3,
    cast(isnull(inv.bFree4, 0) as bit) as RequiresFree4,
    cast(isnull(inv.bFree5, 0) as bit) as RequiresFree5,
    cast(isnull(inv.bFree6, 0) as bit) as RequiresFree6,
    cast(isnull(inv.bFree7, 0) as bit) as RequiresFree7,
    cast(isnull(inv.bFree8, 0) as bit) as RequiresFree8,
    cast(isnull(inv.bFree9, 0) as bit) as RequiresFree9,
    cast(isnull(inv.bFree10, 0) as bit) as RequiresFree10,
    cast(case when isnull(inv.cSTComUnitCode, '') <> ''
                   and inv.cSTComUnitCode <> inv.cComUnitCode
              then 1 else 0 end as bit) as RequiresAuxiliaryUnit,
    case when isnull(inv.cSTComUnitCode, '') <> ''
              and inv.cSTComUnitCode <> inv.cComUnitCode
         then inv.cSTComUnitCode end as AuxiliaryUnitCode,
    case when isnull(inv.cSTComUnitCode, '') <> ''
              and inv.cSTComUnitCode <> inv.cComUnitCode
         then isnull(aux.cComUnitName, '') end as AuxiliaryUnitName,
    case when isnull(inv.cSTComUnitCode, '') <> ''
              and inv.cSTComUnitCode <> inv.cComUnitCode
         then aux.iChangRate end as ExchangeRate,
    cast(isnull(inv.bInvQuality, 0) as bit) as RequiresExpirationDate,
    inv.iMassDate as MassDate,
    inv.cMassUnit as MassUnit
from dbo.Inventory inv
left join dbo.ComputationUnit aux
  on inv.cSTComUnitCode = aux.cComUnitCode
where inv.cInvCode = @inventoryCode";
            using (IDbConnection connection = Open())
            {
                return connection.Query<InventoryCountInventoryRow>(
                    sql,
                    new { inventoryCode }).FirstOrDefault();
            }
        }

        public InventoryCountCodeRow GetDepartment(string departmentCode)
        {
            const string sql = @"
select top (1) cDepCode as Code, isnull(cDepName, '') as Name
from dbo.Department
where cDepCode = @departmentCode";
            using (IDbConnection connection = Open())
            {
                return connection.Query<InventoryCountCodeRow>(
                    sql,
                    new { departmentCode }).FirstOrDefault();
            }
        }

        public InventoryCountCodeRow GetRdStyle(string rdCode)
        {
            const string sql = @"
select top (1) cRdCode as Code, isnull(cRdName, '') as Name
from dbo.rd_style
where cRdCode = @rdCode";
            using (IDbConnection connection = Open())
            {
                return connection.Query<InventoryCountCodeRow>(
                    sql,
                    new { rdCode }).FirstOrDefault();
            }
        }

        public InventoryCountBalanceRow GetCurrentBalance(
            string warehouseCode,
            string inventoryCode,
            string batchCode,
            string positionCode,
            InventoryCountFreeFields freeFields)
        {
            const string sql = @"
if @positionCode = ''
begin
    select isnull(sum(isnull(iQuantity, 0)), 0) as Quantity,
           isnull(sum(isnull(iNum, 0)), 0) as Number
    from dbo.CurrentStock
    where cWhCode = @warehouseCode
      and cInvCode = @inventoryCode
      and isnull(cBatch, '') = isnull(@batchCode, '')
      and isnull(cFree1, '') = isnull(@free1, '')
      and isnull(cFree2, '') = isnull(@free2, '')
      and isnull(cFree3, '') = isnull(@free3, '')
      and isnull(cFree4, '') = isnull(@free4, '')
      and isnull(cFree5, '') = isnull(@free5, '')
      and isnull(cFree6, '') = isnull(@free6, '')
      and isnull(cFree7, '') = isnull(@free7, '')
      and isnull(cFree8, '') = isnull(@free8, '')
      and isnull(cFree9, '') = isnull(@free9, '')
      and isnull(cFree10, '') = isnull(@free10, '')
end
else
begin
    select isnull(sum(isnull(iQuantity, 0)), 0) as Quantity,
           isnull(sum(isnull(iNum, 0)), 0) as Number
    from dbo.InvPositionSum
    where cWhCode = @warehouseCode
      and cInvCode = @inventoryCode
      and isnull(cBatch, '') = isnull(@batchCode, '')
      and isnull(cPosCode, '') = @positionCode
      and isnull(cFree1, '') = isnull(@free1, '')
      and isnull(cFree2, '') = isnull(@free2, '')
      and isnull(cFree3, '') = isnull(@free3, '')
      and isnull(cFree4, '') = isnull(@free4, '')
      and isnull(cFree5, '') = isnull(@free5, '')
      and isnull(cFree6, '') = isnull(@free6, '')
      and isnull(cFree7, '') = isnull(@free7, '')
      and isnull(cFree8, '') = isnull(@free8, '')
      and isnull(cFree9, '') = isnull(@free9, '')
      and isnull(cFree10, '') = isnull(@free10, '')
end";
            freeFields = freeFields ?? new InventoryCountFreeFields();
            using (IDbConnection connection = Open())
            {
                return connection.Query<InventoryCountBalanceRow>(sql, new
                {
                    warehouseCode,
                    inventoryCode,
                    batchCode = batchCode ?? string.Empty,
                    positionCode = positionCode ?? string.Empty,
                    free1 = freeFields.Free1 ?? string.Empty,
                    free2 = freeFields.Free2 ?? string.Empty,
                    free3 = freeFields.Free3 ?? string.Empty,
                    free4 = freeFields.Free4 ?? string.Empty,
                    free5 = freeFields.Free5 ?? string.Empty,
                    free6 = freeFields.Free6 ?? string.Empty,
                    free7 = freeFields.Free7 ?? string.Empty,
                    free8 = freeFields.Free8 ?? string.Empty,
                    free9 = freeFields.Free9 ?? string.Empty,
                    free10 = freeFields.Free10 ?? string.Empty
                }).Single();
            }
        }

        public int? GetTemplateVoucherId()
        {
            const string sql = @"
select top (1) ID
from dbo.CheckVouch
order by ID desc";
            using (IDbConnection connection = Open())
            {
                return connection.Query<int?>(sql).FirstOrDefault();
            }
        }

        public bool VoucherCodeExists(string voucherCode)
        {
            const string sql = @"
select count(1)
from dbo.CheckVouch
where cCVCode = @voucherCode";
            using (IDbConnection connection = Open())
            {
                return connection.ExecuteScalar<int>(
                    sql,
                    new { voucherCode }) > 0;
            }
        }

        public string GetVoucherCodeById(int id)
        {
            const string sql = @"
select top (1) cCVCode
from dbo.CheckVouch
where ID = @id";
            using (IDbConnection connection = Open())
            {
                return connection.ExecuteScalar<string>(sql, new { id });
            }
        }

        public InventoryCountVoucherRow GetVoucher(string voucherCode)
        {
            const string sql = @"
select top (1)
    ID as Id,
    cCVCode as VoucherCode,
    dCVDate as VoucherDate,
    dACDate as CountDate,
    cWhCode as WarehouseCode,
    cDepCode as DepartmentCode,
    cIRdCode as InboundRdCode,
    cORdCode as OutboundRdCode,
    cMaker as Maker,
    cAccounter as Auditor,
    dVeriDate as AuditDate,
    cCVType as CountType,
    cCVMemo as Memo
from dbo.CheckVouch
where cCVCode = @voucherCode";
            using (IDbConnection connection = Open())
            {
                return connection.Query<InventoryCountVoucherRow>(
                    sql,
                    new { voucherCode }).FirstOrDefault();
            }
        }

        public List<InventoryCountVoucherLineRow> GetVoucherLines(int id)
        {
            const string sql = @"
select
    s.AutoID as AutoId,
    s.iRowNo as [LineNo],
    s.cInvCode as InventoryCode,
    isnull(inv.cInvName, '') as InventoryName,
    isnull(inv.cInvStd, '') as Specification,
    isnull(mainUnit.cComUnitName, '') as UnitName,
    s.cCVBatch as BatchCode,
    s.cPosition as PositionCode,
    isnull(pos.cPosName, '') as PositionName,
    isnull(s.iCVQuantity, 0) as BookQuantity,
    isnull(s.iCVCQuantity, 0) as ActualQuantity,
    nullif(s.cAssUnit, '') as AuxiliaryUnitCode,
    s.iInvExchRate as ExchangeRate,
    s.iCVNum as BookNumber,
    s.iCVCNum as ActualNumber,
    isnull(s.iAdInQuantity, 0) as AdjustInQuantity,
    isnull(s.iAdOutQuantity, 0) as AdjustOutQuantity,
    s.cCVReason as Reason,
    s.cbMemo as Remark,
    s.cbufts as RowVersion
from dbo.CheckVouchs s
left join dbo.Inventory inv on s.cInvCode = inv.cInvCode
left join dbo.ComputationUnit mainUnit
  on inv.cComUnitCode = mainUnit.cComUnitCode
left join dbo.Position pos on s.cPosition = pos.cPosCode
where s.ID = @id
order by isnull(s.iRowNo, s.AutoID), s.AutoID";
            using (IDbConnection connection = Open())
            {
                return connection.Query<InventoryCountVoucherLineRow>(
                    sql,
                    new { id }).ToList();
            }
        }

        private IDbConnection Open() =>
            new SqlConnection(_connectionString);

        public List<PositionRow> GetPositionsByName(
            string warehouseCode, string positionName)
        {
            const string sql = @"
select cPosCode as Code, cPosName as Name
from dbo.Position
where cWhCode = @warehouseCode and cPosName = @positionName";
            using (IDbConnection connection = Open())
            {
                return connection.Query<PositionRow>(sql,
                    new { warehouseCode, positionName }).ToList();
            }
        }
    }
}
