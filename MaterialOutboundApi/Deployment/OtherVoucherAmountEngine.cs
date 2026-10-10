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

public sealed class AmountRepairResult
{
    public string VoucherCode { get; set; }
    public bool Success { get; set; }
    public string Status { get; set; }
    public int Lines { get; set; }
    public int AmountsFilled { get; set; }
    public int ZeroPriceAmounts { get; set; }
    public int MissingInputsLines { get; set; }
    public List<string> MissingInputsDetails { get; set; }
    public string Error { get; set; }
}

public static class OtherVoucherAmountEngine
{
    private static readonly XNamespace Row = "#RowsetSchema";

    public static AmountRepairResult Prepare(string type, string code, string connectionString, string root)
    {
        ValidateInput(type, code, connectionString);
        var snapshot = Read(connectionString, type, code);
        Validate(snapshot, code);
        var folder = Path.Combine(root, type + "-" + code);
        Directory.CreateDirectory(folder);
        var plan = Path.Combine(folder, "plan.xml");
        if (File.Exists(plan)) throw new InvalidOperationException("This plan already exists. Use a new job.");
        snapshot.WriteXml(plan, XmlWriteMode.WriteSchema);
        var result = Summarize(snapshot, code);
        result.Success = true; result.Status = "Prepared";
        CountChanges(snapshot, result);
        return result;
    }

    public static AmountRepairResult Run(string type, string code, string connectionString, string subId, string account, string year,
        string user, string password, string server, string serial, string root)
    {
        var result = new AmountRepairResult { VoucherCode = code, Status = "NotSaved", Error = "", MissingInputsDetails = new List<string>() };
        U8Login.clsLogin login = null;
        VoucherCO voucher = null;
        IXMLDOMDocument2 head = null, body = null, position = null;
        bool acquired = false;
        using (var mutex = new Mutex(false, @"Global\MaterialOutboundApi.U8Operations"))
        {
            try
            {
                ValidateInput(type, code, connectionString);
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
                result = Summarize(snapshot, code);
                CountChanges(snapshot, result);
                if (result.AmountsFilled == 0)
                {
                    result.Success = result.MissingInputsLines == 0;
                    result.Status = result.Success ? "NoChangesNeeded" : "MissingInputs";
                    if (!result.Success) result.Error = "金额为空，但缺少单价或数量，未保存。";
                    return result;
                }
                result.AmountsFilled = 0; result.ZeroPriceAmounts = 0;
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
                Transform(b, snapshot, result);
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
                result.Status = result.MissingInputsLines > 0 ? "U8SaveSucceededWithMissingInputs" : "U8SaveSucceeded";
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
    private static AmountRepairResult Summarize(DataTable table, string code)
    {
        var result = new AmountRepairResult { VoucherCode=code, Lines=table.Rows.Count, Status="NotSaved", Error="", MissingInputsDetails=new List<string>() };
        foreach(DataRow row in table.Rows)
            if(row["iPrice"] == DBNull.Value && (row["iUnitCost"] == DBNull.Value || row["iQuantity"] == DBNull.Value))
            {
                result.MissingInputsLines++;
                result.MissingInputsDetails.Add("行 " + row["irowno"] + "：" + row["cInvCode"] + "/" + row["cBatch"] + "，缺少" + (row["iUnitCost"] == DBNull.Value ? "单价" : "数量"));
            }
        return result;
    }
    private static bool CanFill(DataRow row)
    { return row["iPrice"] == DBNull.Value && row["iUnitCost"] != DBNull.Value && row["iQuantity"] != DBNull.Value; }
    private static decimal Amount(DataRow row)
    {
        decimal amount = Decimal.Round(Convert.ToDecimal(row["iQuantity"]) * Convert.ToDecimal(row["iUnitCost"]), 4, MidpointRounding.AwayFromZero);
        // iPrice is SQL money; reject overflow in preview, before any U8 save.
        if (amount < -922337203685477.5808m || amount > 922337203685477.5807m)
            throw new InvalidOperationException("金额超出允许范围，行 " + row["irowno"]);
        return amount;
    }
    private static void CountChanges(DataTable table, AmountRepairResult result)
    {
        foreach (DataRow row in table.Rows)
            if (CanFill(row))
            { Amount(row); result.AmountsFilled++; if (Convert.ToDecimal(row["iUnitCost"]) == 0m) result.ZeroPriceAmounts++; }
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
b.iQuantity,b.iUnitCost,b.iPrice,b.cbMemo,convert(varchar(18),b.rowufts,1) as LineVersion
from dbo.RdRecord{TYPE} h join dbo.RdRecords{TYPE} b on b.ID=h.ID
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

    public static void Transform(XDocument body, DataTable snapshot, AmountRepairResult result)
    {
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
            if (!CanFill(current)) continue;
            row.SetAttributeValue("iprice", Amount(current).ToString("0.####", CultureInfo.InvariantCulture));
            row.SetAttributeValue("editprop", "M");
            result.AmountsFilled++;
            if (Convert.ToDecimal(current["iUnitCost"]) == 0m) result.ZeroPriceAmounts++;
        }
    }

    public static void SelfTest()
    {
        var table = new DataTable();
        foreach (var name in new[] { "AutoID", "irowno", "cInvCode", "cBatch", "cPosition", "iQuantity", "iUnitCost", "iPrice", "cbMemo" })
            table.Columns.Add(name, typeof(object));
        table.Rows.Add(1,1,"I1","B1","P1",2m,1.23456m,DBNull.Value,"retain");
        table.Rows.Add(2,2,"I2","B2","P2",3m,0m,DBNull.Value,"zero");
        table.Rows.Add(3,3,"I3","B3","P3",4m,8m,0m,"existing zero");
        table.Rows.Add(4,4,"I4","B4","P4",2m,8m,99m,"existing amount");
        table.Rows.Add(5,5,"I5","B5","P5",2m,DBNull.Value,DBNull.Value,"missing price");
        table.Rows.Add(6,6,"I6","B6","P6",DBNull.Value,8m,DBNull.Value,"missing quantity");
        table.Rows.Add(7,7,"I7","B7","P7",-1m,1.23455m,DBNull.Value,"negative");
        table.Rows.Add(8,8,"I8","B8","P8",0m,8m,DBNull.Value,"zero quantity");
        var body=new XDocument(new XElement("root",new XAttribute(XNamespace.Xmlns+"z",Row),new XElement("schema")));
        foreach(DataRow current in table.Rows)
        {
            var row=new XElement(Row+"row",new XAttribute("free","unchanged"),new XAttribute("ufts","keep"));
            foreach(DataColumn column in table.Columns)
                if(current[column]!=DBNull.Value)
                    row.SetAttributeValue(column.ColumnName.ToLowerInvariant(),Convert.ToString(current[column],CultureInfo.InvariantCulture));
            body.Root.Add(row);
        }
        var original=new XDocument(body);
        var result=Summarize(table,"test"); CountChanges(table,result);
        if(result.AmountsFilled!=4 || result.ZeroPriceAmounts!=1 || result.MissingInputsLines!=2)
            throw new InvalidOperationException("Preview counters failed.");
        result.AmountsFilled=0;result.ZeroPriceAmounts=0;Transform(body,table,result);
        var rows=body.Descendants(Row+"row").ToArray();
        if(result.AmountsFilled!=4 || result.ZeroPriceAmounts!=1 || Number(rows[0],"iprice")!=2.4691m ||
            Number(rows[1],"iprice")!=0m || Number(rows[6],"iprice")!=-1.2346m || Number(rows[7],"iprice")!=0m)
            throw new InvalidOperationException("Amount calculation/rounding failed.");
        var expected=new XDocument(original);var expectedRows=expected.Descendants(Row+"row").ToArray();
        foreach(int index in new[]{0,1,6,7})
        { expectedRows[index].SetAttributeValue("iprice",Text(rows[index],"iprice"));expectedRows[index].SetAttributeValue("editprop","M"); }
        if(!XNode.DeepEquals(body,expected)) throw new InvalidOperationException("Unrelated XML fields changed.");
        var next=table.Copy();
        for(int i=0;i<rows.Length;i++)
            if(!string.IsNullOrEmpty(Text(rows[i],"iprice")))next.Rows[i]["iPrice"]=Number(rows[i],"iprice");
        string saved=body.ToString();var again=new AmountRepairResult();Transform(body,next,again);
        if(again.AmountsFilled!=0 || body.ToString()!=saved)throw new InvalidOperationException("Idempotency failed.");
        table.Rows[0]["iUnitCost"]=922337203685477.5807m;table.Rows[0]["iQuantity"]=2m;
        bool rejected=false;try{Amount(table.Rows[0]);}catch(InvalidOperationException){rejected=true;}
        if(!rejected)throw new InvalidOperationException("Money overflow accepted.");
    }
    private static string Text(XElement row, string name) { return (string)row.Attribute(name) ?? ""; }
    private static decimal Number(XElement row, string name)
    { var text = Text(row, name); return string.IsNullOrWhiteSpace(text) ? 0m : Decimal.Parse(text, CultureInfo.InvariantCulture); }
    private static bool SameNullableNumber(XElement row, string name, object expected)
    { return expected == DBNull.Value ? string.IsNullOrWhiteSpace(Text(row,name)) : !string.IsNullOrWhiteSpace(Text(row,name)) && Number(row,name) == Convert.ToDecimal(expected); }
    private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
