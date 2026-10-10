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

public sealed class BatchPriceResult
{
    public string VoucherCode { get; set; }
    public bool Success { get; set; }
    public string Status { get; set; }
    public int Lines { get; set; }
    public int PricesFilled { get; set; }
    public int RemarksUpdated { get; set; }
    public int MissingPriceLines { get; set; }
    public List<string> MissingPriceDetails { get; set; }
    public string Error { get; set; }
}

public static class OtherVoucherPriceEngine
{
    private static readonly XNamespace Row = "#RowsetSchema";

    public static BatchPriceResult Prepare(string type, string code, string connectionString, string root, bool replaceZeroPrices = false)
    {
        ValidateInput(type, code, connectionString);
        var snapshot = Read(connectionString, type, code);
        Validate(snapshot, code);
        var folder = Path.Combine(root, type + "-" + code);
        Directory.CreateDirectory(folder);
        var plan = Path.Combine(folder, "plan.xml");
        if (File.Exists(plan)) throw new InvalidOperationException("This plan already exists. Use a new job.");
        snapshot.WriteXml(plan, XmlWriteMode.WriteSchema);
        File.WriteAllText(Path.Combine(folder, "replace-zero-prices.txt"), replaceZeroPrices ? "true" : "false", Encoding.UTF8);
        var result = Summarize(snapshot, code);
        result.Success = true; result.Status = "Prepared";
        foreach (DataRow row in snapshot.Rows)
            if (HasUsablePrice(row["LatestUnitCost"]))
            {
                if (ShouldFillPrice(row["iUnitCost"], replaceZeroPrices)) result.PricesFilled++;
                string old = Convert.ToString(row["cbMemo"]);
                if (Memo(old, Convert.ToDecimal(row["LatestUnitCost"])) != old) result.RemarksUpdated++;
            }
        return result;
    }

    public static BatchPriceResult Run(string type, string code, string connectionString, string subId, string account, string year,
        string user, string password, string server, string serial, string root, bool replaceZeroPrices = false)
    {
        var result = new BatchPriceResult { VoucherCode = code, Status = "NotSaved", Error = "", MissingPriceDetails = new List<string>() };
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
                string policyPath = Path.Combine(folder, "replace-zero-prices.txt");
                if (!File.Exists(policyPath) || File.ReadAllText(policyPath, Encoding.UTF8) != (replaceZeroPrices ? "true" : "false"))
                    throw new InvalidOperationException("Zero-price policy differs from the preview. Preview again with a new job.");
                if (File.Exists(Path.Combine(folder, "save-attempted.txt")))
                    throw new InvalidOperationException("An earlier save was attempted. Do not replay it.");
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("U8 operations are busy.");
                var snapshot = Read(connectionString, type, code);
                Validate(snapshot, code);
                SamePlan(plan, snapshot);
                result = Summarize(snapshot, code);
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
                Transform(b, snapshot, result, replaceZeroPrices);
                if (result.PricesFilled == 0 && result.RemarksUpdated == 0)
                {
                    result.Success = result.MissingPriceLines == 0;
                    result.Status = result.MissingPriceLines > 0 ? "NoAvailablePriceChanges" : "NoChangesNeeded";
                    if (!result.Success) result.Error = "没有可保存的修改；部分行未找到同料号、同批号的历史入库单价，已保留原值。";
                    return result;
                }
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
                result.Status = result.MissingPriceLines > 0 ? "U8SaveSucceededWithMissingPrices" : "U8SaveSucceeded";
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
    private static BatchPriceResult Summarize(DataTable table, string code)
    {
        var result = new BatchPriceResult { VoucherCode=code, Lines=table.Rows.Count, Status="NotSaved", Error="", MissingPriceDetails=new List<string>() };
        foreach(DataRow row in table.Rows)
            if(!HasUsablePrice(row["LatestUnitCost"]))
            { result.MissingPriceLines++; result.MissingPriceDetails.Add("行 " + row["irowno"] + "：" + row["cInvCode"] + "/" + row["cBatch"]); }
        return result;
    }
    private static void SamePlan(DataTable before, DataTable current)
    {
        if(before.Rows.Count != current.Rows.Count) throw new InvalidOperationException("Line count changed. Preview again with a new job.");
        for(int i=0;i<before.Rows.Count;i++)
            foreach(string field in new[]{"VoucherId","HeadVersion","AutoID","LineVersion","SourceAutoID","LatestUnitCost"})
                if(!Object.Equals(before.Rows[i][field],current.Rows[i][field]))
                    throw new InvalidOperationException("Voucher or price source changed: " + field + ". Preview again with a new job.");
    }
    private static DataTable Read(string connectionString, string type, string code)
    {
        ValidateInput(type, code, connectionString);
        var table = new DataTable("VoucherLines");
        using (var connection = new SqlConnection(connectionString))
        using (var adapter = new SqlDataAdapter(@"
select h.ID as VoucherId,h.cCode,h.cHandler,h.dVeriDate,h.dDate as VoucherDate,
convert(varchar(18),h.ufts,1) as HeadVersion,b.AutoID,b.irowno,b.cInvCode,b.cBatch,b.cPosition,
b.iQuantity,b.iUnitCost,b.iPrice,b.cbMemo,convert(varchar(18),b.rowufts,1) as LineVersion,
s.AutoID as SourceAutoID,s.iInCost as LatestUnitCost
from dbo.RdRecord{TYPE} h join dbo.RdRecords{TYPE} b on b.ID=h.ID
outer apply (select top(1) i.AutoID,i.iInCost from dbo.IA_Subsidiary i
where i.cInvCode=b.cInvCode
and (i.cBatchIA=b.cBatch or (i.cBatchIA is null and isnull(b.cBatch,'')=''))
and i.iAInQuantity>0 and i.iInCost>=0
and (i.ID is null or i.ID<>h.ID or isnull(i.cVouType,'')<>'{TYPE}')
and i.dVouDate<dateadd(day,1,convert(date,h.dDate))
order by i.dVouDate desc,i.AutoID desc) s
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

    private static bool HasUsablePrice(object price)
    { return price != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(price)) && Convert.ToDecimal(price) >= 0m; }

    private static bool ShouldFillPrice(object price, bool replaceZeroPrices)
    { return price == DBNull.Value || string.IsNullOrWhiteSpace(Convert.ToString(price)) || (replaceZeroPrices && Convert.ToDecimal(price) == 0m); }

    public static void Transform(XDocument body, DataTable snapshot, BatchPriceResult result, bool replaceZeroPrices = false)
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
                Number(row, "iquantity") != Convert.ToDecimal(current["iQuantity"]) ||
                !SameNullableNumber(row, "iunitcost", current["iUnitCost"]) ||
                !SameNullableNumber(row, "iprice", current["iPrice"]) ||
                Text(row, "cbmemo") != Convert.ToString(current["cbMemo"]))
                throw new InvalidOperationException("Native line differs from SQL snapshot: " + current["irowno"]);
            if (!HasUsablePrice(current["LatestUnitCost"])) continue;
            var cost = Convert.ToDecimal(current["LatestUnitCost"]);
            if (ShouldFillPrice(current["iUnitCost"], replaceZeroPrices))
            {
                row.SetAttributeValue("iunitcost", cost.ToString("0.##########", CultureInfo.InvariantCulture));
                var amount = Decimal.Round(Convert.ToDecimal(current["iQuantity"]) * cost, 4, MidpointRounding.AwayFromZero);
                row.SetAttributeValue("iprice", amount.ToString("0.####", CultureInfo.InvariantCulture));
                row.SetAttributeValue("editprop", "M");
                result.PricesFilled++;
            }
            // All priced rows receive the historical-price remark, including
            // newly filled rows and rows whose existing unit price is zero.
            string old = Convert.ToString(current["cbMemo"]);
            string memo = Memo(old, cost);
            if (memo != old)
            { row.SetAttributeValue("cbmemo", memo); row.SetAttributeValue("editprop", "M"); result.RemarksUpdated++; }
        }
    }

    public static string Memo(string old, decimal cost)
    {
        string marker="最近单价：" + cost.ToString("0.##########", CultureInfo.InvariantCulture);
        string pattern=@"(^|[；;\r\n])最近(?:入库)?单价[:：]\s*[-+]?\d+(?:\.\d+)?(?=$|[；;\r\n])";
        string memo=Regex.IsMatch(old,pattern) ? Regex.Replace(old,pattern,m => m.Groups[1].Value+marker) : (string.IsNullOrWhiteSpace(old)?marker:old+"；"+marker);
        if(memo.Length>255) throw new InvalidOperationException("备注超过255字，未保存。请先缩短原备注。");
        return memo;
    }

    public static void SelfTest()
    {
        SelfTestZeroPrices();
        var table = new DataTable();
        foreach (var name in new[] { "AutoID", "irowno", "cInvCode", "cBatch", "cPosition", "iQuantity", "iUnitCost", "iPrice", "cbMemo", "LatestUnitCost" })
            table.Columns.Add(name, typeof(object));
        table.Rows.Add(1, 1, "I1", "B1", "P1", 2m, DBNull.Value, DBNull.Value, "retain", 1.23456m);
        table.Rows.Add(2, 2, "I2", "B2", "P2", 3m, 0m, 0m, "original", 8.9m);
        var b = XDocument.Parse("<root xmlns:z='#RowsetSchema'><schema/><z:row autoid='1' irowno='1' cinvcode='I1' cbatch='B1' cposition='P1' iquantity='2' cbmemo='retain' free='unchanged'/><z:row autoid='2' irowno='2' cinvcode='I2' cbatch='B2' cposition='P2' iquantity='3' iunitcost='0' iprice='0' cbmemo='original' free='unchanged'/></root>");
        var result = new BatchPriceResult(); Transform(b, table, result);
        var rows = b.Descendants(Row + "row").ToArray();
        if (result.PricesFilled != 1 || result.RemarksUpdated != 2 || Number(rows[0], "iunitcost") != 1.23456m ||
            Number(rows[0], "iprice") != 2.4691m || Text(rows[0], "cbmemo") != "retain；最近单价：1.23456" ||
            Number(rows[1], "iunitcost") != 0m || Number(rows[1], "iprice") != 0m ||
            Text(rows[1], "cbmemo") != "original；最近单价：8.9" || rows.Any(r => Text(r,"free") != "unchanged") || !b.Root.Elements("schema").Any())
            throw new InvalidOperationException("Pure XML editing self-test failed.");
        if(Memo("说明；最近单价：1.2",1m)!="说明；最近单价：1" || Memo(Memo("说明",8m),8m)!=Memo("说明",8m))
            throw new InvalidOperationException("Remark refresh/idempotency test failed.");
        table.Rows[0]["LatestUnitCost"]=DBNull.Value;
        var untouched=XDocument.Parse("<root xmlns:z='#RowsetSchema'><z:row autoid='1' irowno='1' cinvcode='I1' cbatch='B1' cposition='P1' iquantity='2' cbmemo='retain'/><z:row autoid='2' irowno='2' cinvcode='I2' cbatch='B2' cposition='P2' iquantity='3' iunitcost='0' iprice='0' cbmemo='original'/></root>");
        string original=untouched.Descendants(Row+"row").First().ToString();
        Transform(untouched,table,new BatchPriceResult());
        if(untouched.Descendants(Row+"row").First().ToString()!=original) throw new InvalidOperationException("Missing-price row changed.");
        bool rejected=false;try{Memo(new string('x',255),1m);}catch(InvalidOperationException){rejected=true;}
        if(!rejected)throw new InvalidOperationException("Remark length test failed.");
    }
    private static void SelfTestZeroPrices()
    {
        foreach (bool replaceZero in new[] { false, true })
        {
            var table = new DataTable();
            foreach (string name in new[] { "AutoID", "irowno", "cInvCode", "cBatch", "cPosition", "iQuantity", "iUnitCost", "iPrice", "cbMemo", "LatestUnitCost" })
                table.Columns.Add(name, typeof(object));
            table.Rows.Add(1, 1, "I1", "B1", "", 2m, DBNull.Value, DBNull.Value, "", 0m);
            table.Rows.Add(2, 2, "I2", "B2", "", 3m, 0m, DBNull.Value, "", 8.9m);
            table.Rows.Add(3, 3, "I3", "B3", "", 4m, 0m, 7m, "", 0m);
            table.Rows.Add(4, 4, "I4", "B4", "", 5m, 6m, 30m, "retain", 0m);
            table.Rows.Add(5, 5, "I5", "B5", "", 6m, 0m, 9m, "keep", DBNull.Value);
            var body = XDocument.Parse("<root xmlns:z='#RowsetSchema'><z:row autoid='1' irowno='1' cinvcode='I1' cbatch='B1' iquantity='2'/><z:row autoid='2' irowno='2' cinvcode='I2' cbatch='B2' iquantity='3' iunitcost='0'/><z:row autoid='3' irowno='3' cinvcode='I3' cbatch='B3' iquantity='4' iunitcost='0' iprice='7'/><z:row autoid='4' irowno='4' cinvcode='I4' cbatch='B4' iquantity='5' iunitcost='6' iprice='30' cbmemo='retain'/><z:row autoid='5' irowno='5' cinvcode='I5' cbatch='B5' iquantity='6' iunitcost='0' iprice='9' cbmemo='keep'/></root>");
            string missingBefore = body.Descendants(Row + "row").Last().ToString();
            var result = new BatchPriceResult();
            Transform(body, table, result, replaceZero);
            var rows = body.Descendants(Row + "row").ToArray();
            if (result.PricesFilled != (replaceZero ? 3 : 1) || result.RemarksUpdated != 4 ||
                Text(rows[0], "iunitcost") != "0" || Text(rows[0], "iprice") != "0" || Text(rows[0], "cbmemo") != "最近单价：0" ||
                Number(rows[1], "iunitcost") != (replaceZero ? 8.9m : 0m) ||
                Text(rows[1], "iprice") != (replaceZero ? "26.7" : "") || Text(rows[1], "cbmemo") != "最近单价：8.9" ||
                Number(rows[2], "iunitcost") != 0m || Number(rows[2], "iprice") != (replaceZero ? 0m : 7m) || Text(rows[2], "cbmemo") != "最近单价：0" ||
                Number(rows[3], "iunitcost") != 6m || Number(rows[3], "iprice") != 30m || Text(rows[3], "cbmemo") != "retain；最近单价：0" ||
                rows[4].ToString() != missingBefore)
                throw new InvalidOperationException("Zero-price selection/source regression failed.");
        }
        if (HasUsablePrice(DBNull.Value) || HasUsablePrice(-1m) || !HasUsablePrice(0m) || !HasUsablePrice(8m))
            throw new InvalidOperationException("Historical zero/NULL/negative price guard failed.");
        if (!ShouldFillPrice(DBNull.Value, false) || ShouldFillPrice(0m, false) || !ShouldFillPrice(0m, true) || ShouldFillPrice(8m, true))
            throw new InvalidOperationException("Zero-price eligibility guard failed.");
    }
    private static string Text(XElement row, string name) { return (string)row.Attribute(name) ?? ""; }
    private static decimal Number(XElement row, string name)
    { var text = Text(row, name); return string.IsNullOrWhiteSpace(text) ? 0m : Decimal.Parse(text, CultureInfo.InvariantCulture); }
    private static bool SameNullableNumber(XElement row, string name, object expected)
    { return expected == DBNull.Value ? string.IsNullOrWhiteSpace(Text(row,name)) : !string.IsNullOrWhiteSpace(Text(row,name)) && Number(row,name) == Convert.ToDecimal(expected); }
    private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
