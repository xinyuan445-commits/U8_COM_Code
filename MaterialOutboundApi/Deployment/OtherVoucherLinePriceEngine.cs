using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MSXML2;
using USERPCO;

public sealed class LinePriceEditResult
{
    public string VoucherCode { get; set; }
    public bool Success { get; set; }
    public string Status { get; set; }
    public int LineNo { get; set; }
    public string InventoryCode { get; set; }
    public string InventoryName { get; set; }
    public string Batch { get; set; }
    public string Position { get; set; }
    public string Quantity { get; set; }
    public string OldUnitPrice { get; set; }
    public string NewUnitPrice { get; set; }
    public string OldAmount { get; set; }
    public string NewAmount { get; set; }
    public bool PriceChanged { get; set; }
    public bool AmountChanged { get; set; }
    public string Error { get; set; }
}

public static class OtherVoucherLinePriceEngine
{
    private static readonly XNamespace Row = "#RowsetSchema";

    public static LinePriceEditResult Query(string type,string code,string connectionString,int lineNo)
    {
        var snapshot=Read(connectionString,type,code);Validate(snapshot,code);
        return Describe(snapshot,code,lineNo);
    }
    public static LinePriceEditResult Prepare(string type,string code,string connectionString,string root,int lineNo,decimal unitPrice,bool recalculate)
    {
        ValidateEdit(lineNo,unitPrice);
        var snapshot=Read(connectionString,type,code);Validate(snapshot,code);
        var result=DescribeEdit(snapshot,code,lineNo,unitPrice,recalculate);
        var folder=Path.Combine(root,type+"-"+code);Directory.CreateDirectory(folder);
        var plan=Path.Combine(folder,"plan.xml");
        if(File.Exists(plan))throw new InvalidOperationException("This plan already exists. Use a new job.");
        snapshot.WriteXml(plan,XmlWriteMode.WriteSchema);
        result.Success=true;result.Status="Prepared";return result;
    }

    public static LinePriceEditResult Run(string type, string code, string connectionString, string subId, string account, string year,
        string user, string password, string server, string serial, string root, int lineNo, decimal unitPrice, bool recalculate)
    {
        var result = new LinePriceEditResult { VoucherCode = code, Status = "NotSaved", Error = "" };
        U8Login.clsLogin login = null;
        VoucherCO voucher = null;
        IXMLDOMDocument2 head = null, body = null, position = null;
        bool acquired = false;
        using (var mutex = new Mutex(false, @"Global\MaterialOutboundApi.U8Operations"))
        {
            try
            {
                ValidateInput(type, code, connectionString);
                ValidateEdit(lineNo, unitPrice);
                string database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
                if (!account.EndsWith("@" + database.Substring(7,3))) throw new InvalidOperationException("Account/database mismatch.");
                var folder = Path.Combine(root, type + "-" + code);
                var plan = new DataTable("VoucherLines");
                plan.ReadXml(Path.Combine(folder, "plan.xml"));
                if (File.Exists(Path.Combine(folder, "save-attempted.txt")))
                    throw new InvalidOperationException("An earlier save was attempted. Do not replay it.");
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("U8 operations are busy.");
                var snapshot = Read(connectionString, type, code);
                Validate(snapshot, code);
                SamePlan(plan, snapshot);
                result = DescribeEdit(snapshot,code,lineNo,unitPrice,recalculate);
                if(!result.PriceChanged && !result.AmountChanged)
                { result.Success=true;result.Status="NoChangesNeeded";return result; }
                snapshot.WriteXml(Path.Combine(folder, "before-sql.xml"), XmlWriteMode.WriteSchema);
                login = new U8Login.clsLogin();
                if (!login.Login(subId, account, year, user, password,
                    Convert.ToDateTime(snapshot.Rows[0]["VoucherDate"]).ToString("yyyy-MM-dd"), server, serial))
                    throw new InvalidOperationException("U8 login failed: " + login.ShareString);
                voucher = new VoucherCOClass();
                string error = "";
                if (!voucher.IniLogin(login, ref error)) throw new InvalidOperationException("U8 init failed: " + error);
                head = new DOMDocument26Class(); body = new DOMDocument26Class(); position = new DOMDocument26Class();
                if (!voucher.Load(type, "ID=" + snapshot.Rows[0]["VoucherId"], ref head, ref body, ref position, ref error))
                    throw new InvalidOperationException("U8 Load failed: " + error);
                File.WriteAllText(Path.Combine(folder, "before-head.xml"), head.xml, Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "before-body.xml"), body.xml, Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "before-position.xml"), position.xml, Encoding.UTF8);
                var h = XDocument.Parse(head.xml, LoadOptions.PreserveWhitespace);
                var b = XDocument.Parse(body.xml, LoadOptions.PreserveWhitespace);
                var header = h.Descendants(Row + "row").Single();
                if (Text(header, "ccode") != code || Number(header, "id") != Convert.ToDecimal(snapshot.Rows[0]["VoucherId"]) ||
                    Text(header, "cvouchtype") != type || !string.IsNullOrWhiteSpace(Text(header, "chandler")) ||
                    Number(header, "iverifystate") != 0m || Number(header, "iswfcontrolled") != 0m)
                    throw new InvalidOperationException("Native voucher identity, audit or workflow state is not editable.");
                Transform(b,snapshot,lineNo,unitPrice,recalculate);
                header.SetAttributeValue("editprop", "M");
                if (!head.loadXML(h.ToString(SaveOptions.DisableFormatting)) || !body.loadXML(b.ToString(SaveOptions.DisableFormatting)))
                    throw new InvalidOperationException("Edited native XML is invalid.");
                File.WriteAllText(Path.Combine(folder, "requested-head.xml"), head.xml, Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "requested-body.xml"), body.xml, Encoding.UTF8);
                var latest = Read(connectionString, type, code);
                Validate(latest, code);
                SamePlan(snapshot, latest);
                using (var marker = new FileStream(Path.Combine(folder, "save-attempted.txt"), FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(marker)) { writer.Write(DateTime.Now.ToString("O")); }
                if (!voucher.Update(type, head, body, position, ref error))
                    throw new InvalidOperationException("U8 Update failed: " + error);
                // The user requested stopping at U8 success: no post-save query or Load.
                result.Success = true;
                result.Status = "U8SaveSucceeded";
                File.WriteAllText(Path.Combine(folder, "u8-save-success.txt"), DateTime.Now.ToString("O"), Encoding.UTF8);
            }
            catch (Exception exception)
            { result.Error = exception.Message; }
            finally
            {
                if (login != null) { try { login.ShutDown(); } catch { } }
                Release(position); Release(body); Release(head); Release(voucher); Release(login);
                if (acquired) mutex.ReleaseMutex();
            }
        }
        return result;
    }

    private static void ValidateInput(string type, string code, string connectionString)
    {
        if ((type != "08" && type != "09") || !Regex.IsMatch(code ?? "", @"\A[A-Za-z0-9_-]{1,30}\z"))
            throw new InvalidOperationException("Invalid voucher type/code.");
        string database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (database != "UFDATA_008_2021" && database != "UFDATA_998_2021") throw new InvalidOperationException("Unsupported database.");
    }
    private static DataRow Target(DataTable table,int lineNo)
    {
        if(lineNo<=0)throw new InvalidOperationException("行号必须为正整数。");
        var rows=table.AsEnumerable().Where(r=>Convert.ToInt32(r["irowno"])==lineNo).ToArray();
        if(rows.Length!=1)throw new InvalidOperationException("行号不存在或不唯一："+lineNo);
        return rows[0];
    }
    private static void ValidateEdit(int lineNo,decimal unitPrice)
    {
        if(lineNo<=0 || unitPrice<0m || unitPrice>999999999999999999.9999999999m || Decimal.Round(unitPrice,10)!=unitPrice)
            throw new InvalidOperationException("单价须为非负数，最多18位整数和10位小数；行号须为正整数。");
    }
    private static string Value(object value)
    { return value==DBNull.Value ? "NULL" : Convert.ToString(value,CultureInfo.InvariantCulture); }
    private static LinePriceEditResult Describe(DataTable table,string code,int lineNo)
    {
        var row=Target(table,lineNo);
        return new LinePriceEditResult {VoucherCode=code,Success=true,Status="Queried",Error="",LineNo=lineNo,
            InventoryCode=Convert.ToString(row["cInvCode"]),InventoryName=Convert.ToString(row["cInvName"]),
            Batch=Convert.ToString(row["cBatch"]),Position=Convert.ToString(row["cPosition"]),
            Quantity=Value(row["iQuantity"]),OldUnitPrice=Value(row["iUnitCost"]),OldAmount=Value(row["iPrice"]),
            NewUnitPrice=Value(row["iUnitCost"]),NewAmount=Value(row["iPrice"])};
    }
    private static decimal Amount(DataRow row,decimal unitPrice)
    {
        if(row["iQuantity"]==DBNull.Value)throw new InvalidOperationException("该行数量为空，不能重算金额。请选择只改单价。");
        decimal amount=Decimal.Round(Convert.ToDecimal(row["iQuantity"])*unitPrice,4,MidpointRounding.AwayFromZero);
        if(amount < -922337203685477.5808m || amount > 922337203685477.5807m)
            throw new InvalidOperationException("新金额超出允许范围。");
        return amount;
    }
    private static LinePriceEditResult DescribeEdit(DataTable table,string code,int lineNo,decimal unitPrice,bool recalculate)
    {
        ValidateEdit(lineNo,unitPrice);
        var row=Target(table,lineNo);var result=Describe(table,code,lineNo);result.Success=false;result.Status="NotSaved";
        result.NewUnitPrice=unitPrice.ToString("0.##########",CultureInfo.InvariantCulture);
        result.PriceChanged=row["iUnitCost"]==DBNull.Value || Convert.ToDecimal(row["iUnitCost"])!=unitPrice;
        if(recalculate)
        {
            var amount=Amount(row,unitPrice);result.NewAmount=amount.ToString("0.####",CultureInfo.InvariantCulture);
            result.AmountChanged=row["iPrice"]==DBNull.Value || Convert.ToDecimal(row["iPrice"])!=amount;
        }
        return result;
    }
    private static void SamePlan(DataTable before, DataTable current)
    {
        if(before.Rows.Count != current.Rows.Count) throw new InvalidOperationException("Line count changed. Preview again with a new job.");
        for(int i=0;i<before.Rows.Count;i++)
            foreach(string field in new[]{"VoucherId","HeadVersion","AutoID","LineVersion","iQuantity","iUnitCost","iPrice"})
                if(!Object.Equals(before.Rows[i][field],current.Rows[i][field]))
                    throw new InvalidOperationException("Voucher changed: " + field + ". Preview again with a new job.");
    }
    private static DataTable Read(string connectionString, string type, string code)
    {
        ValidateInput(type, code, connectionString);
        var table = new DataTable("VoucherLines");
        using (var connection = new SqlConnection(connectionString))
        using (var adapter = new SqlDataAdapter(@"
select h.ID as VoucherId,h.cCode,h.cHandler,h.dVeriDate,h.dDate as VoucherDate,
convert(varchar(18),h.ufts,1) as HeadVersion,b.AutoID,b.irowno,b.cInvCode,b.cBatch,b.cPosition,
b.iQuantity,b.iUnitCost,b.iPrice,b.cbMemo,convert(varchar(18),b.rowufts,1) as LineVersion,inv.cInvName
from dbo.RdRecord{TYPE} h join dbo.RdRecords{TYPE} b on b.ID=h.ID
left join dbo.Inventory inv on inv.cInvCode=b.cInvCode
where h.cCode=@code order by b.irowno,b.AutoID".Replace("{TYPE}", type), connection))
        {
            adapter.SelectCommand.Parameters.Add("@code", SqlDbType.NVarChar, 30).Value = code;
            adapter.SelectCommand.CommandTimeout = 120;
            adapter.Fill(table);
        }
        return table;
    }

    private static void Validate(DataTable table, string code)
    {
        if (table.Rows.Count == 0) throw new InvalidOperationException("其他出入库单不存在或没有明细：" + code);
        if (table.AsEnumerable().Select(r => Convert.ToInt64(r["VoucherId"])).Distinct().Count() != 1)
            throw new InvalidOperationException("Voucher code is ambiguous.");
        foreach (DataRow r in table.Rows)
        {
            if (!string.IsNullOrWhiteSpace(Convert.ToString(r["cHandler"])) || r["dVeriDate"] != DBNull.Value)
                throw new InvalidOperationException("Voucher is audited; no change was made.");
        }
    }

    public static void Transform(XDocument body, DataTable snapshot, int lineNo, decimal unitPrice, bool recalculate)
    {
        ValidateEdit(lineNo,unitPrice);
        var target=Target(snapshot,lineNo);
        decimal? amount=recalculate ? (decimal?)Amount(target,unitPrice) : null;
        var rows = body.Descendants(Row + "row").ToList();
        if (rows.Count != snapshot.Rows.Count) throw new InvalidOperationException("Native line count mismatch.");
        foreach (DataRow current in snapshot.Rows)
        {
            var row = rows.Single(r => Number(r, "autoid") == Convert.ToDecimal(current["AutoID"]));
            if (Number(row, "irowno") != Convert.ToDecimal(current["irowno"]) ||
                Text(row, "cinvcode") != Convert.ToString(current["cInvCode"]) ||
                Text(row, "cbatch") != Convert.ToString(current["cBatch"]) ||
                Text(row, "cposition") != Convert.ToString(current["cPosition"]) ||
                !SameNullableNumber(row, "iquantity", current["iQuantity"]) ||
                !SameNullableNumber(row, "iunitcost", current["iUnitCost"]) ||
                !SameNullableNumber(row, "iprice", current["iPrice"]) ||
                Text(row, "cbmemo") != Convert.ToString(current["cbMemo"]))
                throw new InvalidOperationException("Native line differs from SQL snapshot: " + current["irowno"]);
            if(current!=target)continue;
            bool changed=false;
            if(current["iUnitCost"]==DBNull.Value || Convert.ToDecimal(current["iUnitCost"])!=unitPrice)
            {row.SetAttributeValue("iunitcost",unitPrice.ToString("0.##########",CultureInfo.InvariantCulture));changed=true;}
            if(amount.HasValue && (current["iPrice"]==DBNull.Value || Convert.ToDecimal(current["iPrice"])!=amount.Value))
            {row.SetAttributeValue("iprice",amount.Value.ToString("0.####",CultureInfo.InvariantCulture));changed=true;}
            if(changed)row.SetAttributeValue("editprop","M");
        }
    }

    public static void SelfTest()
    {
        var table=new DataTable();
        foreach(var name in new[]{"AutoID","irowno","cInvCode","cBatch","cPosition","iQuantity","iUnitCost","iPrice","cbMemo"})
            table.Columns.Add(name,typeof(object));
        table.Rows.Add(101,5,"I1","B1","P1",2m,8m,99m,"retain");
        table.Rows.Add(102,9,"I2","B2","P2",3m,0m,DBNull.Value,"keep");
        var original=XDocument.Parse("<root xmlns:z='#RowsetSchema'><schema/><z:row autoid='101' irowno='5' cinvcode='I1' cbatch='B1' cposition='P1' iquantity='2' iunitcost='8' iprice='99' cbmemo='retain' free='keep' ufts='same'/><z:row autoid='102' irowno='9' cinvcode='I2' cbatch='B2' cposition='P2' iquantity='3' iunitcost='0' cbmemo='keep'/></root>");
        var body=new XDocument(original);Transform(body,table,5,1.23455m,true);
        var expected=new XDocument(original);var first=expected.Descendants(Row+"row").First();
        first.SetAttributeValue("iunitcost","1.23455");first.SetAttributeValue("iprice","2.4691");first.SetAttributeValue("editprop","M");
        if(!XNode.DeepEquals(body,expected))throw new InvalidOperationException("Target-only XML edit failed.");
        body=new XDocument(original);Transform(body,table,9,0m,true);
        if(Text(body.Descendants(Row+"row").Last(),"iprice")!="0")throw new InvalidOperationException("Zero-price amount failed.");
        body=new XDocument(original);Transform(body,table,5,0m,false);
        if(Text(body.Descendants(Row+"row").First(),"iprice")!="99" || Text(body.Descendants(Row+"row").First(),"iunitcost")!="0")
            throw new InvalidOperationException("Keep amount mode failed.");
        body=new XDocument(original);Transform(body,table,5,8m,false);
        if(!XNode.DeepEquals(body,original))throw new InvalidOperationException("No-change mode failed.");
        bool rejected=false;try{Target(table,1);}catch(InvalidOperationException){rejected=true;}
        if(!rejected)throw new InvalidOperationException("Array index accepted as U8 row number.");
        var duplicate=table.Copy();duplicate.Rows[1]["irowno"]=5;
        rejected=false;try{Target(duplicate,5);}catch(InvalidOperationException){rejected=true;}
        if(!rejected)throw new InvalidOperationException("Duplicate row number accepted.");
        foreach(decimal invalid in new[]{-1m,0.00000000001m})
        {rejected=false;try{ValidateEdit(5,invalid);}catch(InvalidOperationException){rejected=true;}if(!rejected)throw new InvalidOperationException("Invalid price accepted.");}
        table.Rows[0]["iQuantity"]=DBNull.Value;
        rejected=false;try{Amount(table.Rows[0],1m);}catch(InvalidOperationException){rejected=true;}
        if(!rejected)throw new InvalidOperationException("Missing quantity accepted for recalculation.");
        table.Rows[0]["iQuantity"]=2m;
        rejected=false;try{Amount(table.Rows[0],922337203685477.5807m);}catch(InvalidOperationException){rejected=true;}
        if(!rejected)throw new InvalidOperationException("Money overflow accepted.");
    }
    private static string Text(XElement row, string name) { return (string)row.Attribute(name) ?? ""; }
    private static decimal Number(XElement row, string name)
    { var text = Text(row, name); return string.IsNullOrWhiteSpace(text) ? 0m : Decimal.Parse(text, CultureInfo.InvariantCulture); }
    private static bool SameNullableNumber(XElement row, string name, object expected)
    { return expected == DBNull.Value ? string.IsNullOrWhiteSpace(Text(row,name)) : !string.IsNullOrWhiteSpace(Text(row,name)) && Number(row,name) == Convert.ToDecimal(expected); }
    private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
