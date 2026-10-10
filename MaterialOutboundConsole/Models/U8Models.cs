using System;

namespace MaterialOutboundConsole.Models
{
    internal sealed class U8Account
    {
        public string Date { get; set; }
        public string SubId { get; set; }
        public string Year { get; set; }
        public string AccId { get; set; }
        public string UserId { get; set; }
        public string Password { get; set; }
        public string Server { get; set; }
        public string Serial { get; set; }
    }

    internal sealed class ProductionMaterial
    {
        public int? MoId { get; set; }
        public string MoCode { get; set; }
        public int MoDId { get; set; }
        public string InvCode { get; set; }
        public decimal? Qty { get; set; }
        public int? SortSeq { get; set; }
        public string DepartmentCode { get; set; }
        public int? AllocateId { get; set; }
        public string OpSeq { get; set; }
        public string CInvCode { get; set; }
        public decimal? BaseQtyN { get; set; }
        public decimal? BaseQtyD { get; set; }
        public decimal? CompScrap { get; set; }
    }

    internal sealed class WorkOrderLine
    {
        public int MoId { get; set; }
        public string MoCode { get; set; }
        public int MoDId { get; set; }
        public int? SortSeq { get; set; }
        public string InvCode { get; set; }
        public decimal? Qty { get; set; }
        public string DepartmentCode { get; set; }
        public int? Status { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int AllocationCount { get; set; }
    }

    internal sealed class WarehouseInfo
    {
        public string WarehouseCode { get; set; }
        public string WarehouseName { get; set; }
        public bool UsesPosition { get; set; }
    }

    internal sealed class PositionInfo
    {
        public string PositionCode { get; set; }
        public string PositionName { get; set; }
        public string WarehouseCode { get; set; }
    }

    internal sealed class MaterialOutboundRecord
    {
        public int Id { get; set; }
        public string CCode { get; set; }
        public string Cpspcode { get; set; }
        public string Cmpocode { get; set; }
        public int? Iproorderid { get; set; }
        public string Cmaker { get; set; }
        public string Cwhcode { get; set; }
        public string Cdepcode { get; set; }
        public int? PId { get; set; }
        public int? AutoId { get; set; }
        public int? Irowno { get; set; }
        public string Invcode { get; set; }
        public string Cinvcode { get; set; }
        public decimal? Iquantity { get; set; }
        public decimal? Iunitcost { get; set; }
        public decimal? Iprice { get; set; }
        public decimal? Inquantity { get; set; }
        public string Cbatch { get; set; }
        public long? Impoids { get; set; }
        public string Cmocode { get; set; }
        public int? Imoseq { get; set; }
        public string Iopseq { get; set; }
        public string Copdesc { get; set; }
        public string Cmworkcentercode { get; set; }
        public string Cposition { get; set; }
        public string Cposname { get; set; }
        public int? Iposflag { get; set; }
    }

    internal sealed class ShapeConversionRecord
    {
        public int Id { get; set; }
        public string Code { get; set; }
    }

    internal sealed class MaterialOutboundHead
    {
        public int? brdflag { get; set; } = 0;
        public string cvouchtype { get; set; } = "11";
        public DateTime? dnmaketime { get; set; } = DateTime.Now;
        public DateTime? ddate { get; set; } = DateTime.Today;
        public string crdcode { get; set; } = "202";
        public string csource { get; set; } = "生产订单";
        public string cbustype { get; set; } = "领料";
        public bool? bisstqc { get; set; } = false;
        public int? vt_id { get; set; } = 65;
        public bool? bpufirst { get; set; } = false;
        public bool? biafirst { get; set; } = false;
        public int? biscomplement { get; set; } = 0;
        public int? ireturncount { get; set; } = 0;
        public int? bmotran { get; set; } = 0;
        public int? iPrintCount { get; set; } = 0;
        public string cdepcode { get; set; }
        public string cwhcode { get; set; }
        public string cmaker { get; set; }
        public int? id { get; set; }
        public string csysbarcode { get; set; }
        public string ccode { get; set; }
        public float? imquantity { get; set; }
        public string cpspcode { get; set; }
        public string cmpocode { get; set; }
        public int? iproorderid { get; set; }
        public int? iverifystate { get; set; }
        public int? iswfcontrolled { get; set; }
        public string ufts { get; set; }
        public string cmodifyperson { get; set; }
        public string editprop { get; set; }
    }

    internal sealed class MaterialOutboundBody
    {
        public bool? bcosting { get; set; } = true;
        public bool? bvmiused { get; set; } = false;
        public int? iexpiratdatecalcu { get; set; } = 0;
        public int? autoid { get; set; }
        public long? id { get; set; }
        public string cinvcode { get; set; }
        public decimal? iquantity { get; set; }
        public decimal? iunitcost { get; set; }
        public decimal? iprice { get; set; }
        public decimal? inquantity { get; set; }
        public string cbatch { get; set; }
        public int? isotype { get; set; }
        public int? iordertype { get; set; }
        public int? iorderdid { get; set; }
        public long? impoids { get; set; }
        public string cmocode { get; set; }
        public int? imoseq { get; set; }
        public string iopseq { get; set; }
        public string copdesc { get; set; }
        public string cmworkcentercode { get; set; }
        public string invcode { get; set; }
        public string corufts { get; set; }
        public int? irowno { get; set; }
        public long? ipesodid { get; set; }
        public int? ipesotype { get; set; }
        public string cpesocode { get; set; }
        public int? ipesoseq { get; set; }
        public string cbsysbarcode { get; set; }
        public int? bcanreplace { get; set; }
        public int? boutmaterials { get; set; }
        public string cposition { get; set; }
        public string cposname { get; set; }
        public int? iposflag { get; set; }
        public string cbmemo { get; set; }
        public string editprop { get; set; }
    }
}
