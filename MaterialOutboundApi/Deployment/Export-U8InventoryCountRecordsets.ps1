param(
    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",
    [ValidateSet("UFDATA_008_2021", "UFDATA_998_2021")]
    [string]$Database = "UFDATA_008_2021",
    [string]$VoucherCode = "PDD26091292",
    [string]$OutputDirectory = "D:\MupiInventoryCountResearch\recordsets4"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
trap {
    ($_ | Out-String) | Set-Content -LiteralPath `
        (Join-Path $OutputDirectory "error.txt") -Encoding UTF8
    exit 1
}

function Unprotect-Text {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    Add-Type -AssemblyName System.Security
    $entropy = [Text.Encoding]::UTF8.GetBytes("MaterialOutboundApi.Settings.v1")
    $protected = [Convert]::FromBase64String($Value)
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $protected,
        $entropy,
        [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Text.Encoding]::UTF8.GetString($plain)
}

$helperSource = @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class U8InventoryCountRecordsetExporter
{
    public static void Export(
        string subId,
        string account,
        string year,
        string user,
        string password,
        string loginDate,
        string server,
        string serial,
        int voucherId,
        string outputDirectory)
    {
        dynamic oldLogin = null;
        dynamic login = null;
        dynamic persistence = null;
        object voucher = null;
        object connection = null;
        try
        {
            oldLogin = Create("U8Login.clsLogin");
            if (!oldLogin.Login(
                ref subId,
                ref account,
                ref year,
                ref user,
                ref password,
                ref loginDate,
                ref server,
                ref serial))
            {
                throw new InvalidOperationException("U8 login failed: " + oldLogin.ShareString);
            }

            login = Create("USCOMMON.Login");
            object loginSource = oldLogin;
            if (!login.Load(ref loginSource))
            {
                throw new InvalidOperationException("USCOMMON.Login.Load failed: " + login.ShareString);
            }

            connection = login.AccountConnection;
            persistence = Create("USERPDMO.CheckVouchPst");
            persistence.Login = login;
            voucher = Create("USERPVO.CheckVouch");
            if (!persistence.Load("ID=" + voucherId, ref voucher, ref connection))
            {
                throw new InvalidOperationException("CheckVouchPst.Load returned false.");
            }

            dynamic loadedVoucher = voucher;
            Save(loadedVoucher.Head, Path.Combine(outputDirectory, "head.xml"));
            Save(loadedVoucher.Body, Path.Combine(outputDirectory, "body.xml"));
            Save(loadedVoucher.Position, Path.Combine(outputDirectory, "position.xml"));
        }
        finally
        {
            if (oldLogin != null)
            {
                try { oldLogin.ShutDown(); } catch { }
            }
            Release(voucher);
            Release(persistence);
            Release(login);
            Release(oldLogin);
        }
    }

    private static dynamic Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, true);
        return Activator.CreateInstance(type);
    }

    private static void Save(dynamic recordset, string path)
    {
        if (recordset == null || Convert.ToInt32(recordset.State) == 0)
        {
            File.WriteAllText(path, string.Empty);
            return;
        }
        recordset.Save(path, 1);
    }

    private static void Release(object value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
'@

Add-Type -TypeDefinition $helperSource -Language CSharp -ReferencedAssemblies @(
    "System.dll",
    "System.Core.dll",
    "Microsoft.CSharp.dll")

$stored = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$sqlPassword = Unprotect-Text ([string]$stored.SqlPasswordProtected)
$u8Password = Unprotect-Text ([string]$stored.U8PasswordProtected)
$accountCode = $Database.Substring("UFDATA_".Length, 3)
$u8Account = [string]$stored.Public.U8AccId
$separator = $u8Account.LastIndexOf("@")
$u8Account = if ($separator -ge 0) {
    $u8Account.Substring(0, $separator) + "@" + $accountCode
} else {
    "(default)@" + $accountCode
}

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder["Data Source"] = [string]$stored.Public.SqlServer
$builder["Initial Catalog"] = $Database
$builder["User ID"] = [string]$stored.Public.SqlUser
$builder["Password"] = $sqlPassword
$builder["Encrypt"] = $false
$builder["TrustServerCertificate"] = $true
$connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = "select top (1) ID from dbo.CheckVouch where cCVCode=@code"
    [void]$command.Parameters.Add("@code", [Data.SqlDbType]::NVarChar, 30)
    $command.Parameters["@code"].Value = $VoucherCode
    $voucherId = $command.ExecuteScalar()
    if ($null -eq $voucherId -or $voucherId -is [DBNull]) {
        throw "未找到盘点单：$VoucherCode"
    }
}
finally {
    $connection.Dispose()
}

[U8InventoryCountRecordsetExporter]::Export(
    [string]$stored.Public.U8SubId,
    $u8Account,
    [string]$stored.Public.U8Year,
    [string]$stored.Public.U8UserId,
    $u8Password,
    (Get-Date).ToString("yyyy-MM-dd"),
    [string]$stored.Public.U8Server,
    [string]$stored.Public.U8Serial,
    [int]$voucherId,
    $OutputDirectory)

[pscustomobject]@{
    Database = $Database
    VoucherCode = $VoucherCode
    VoucherId = $voucherId
    HeadPath = (Join-Path $OutputDirectory "head.xml")
    BodyPath = (Join-Path $OutputDirectory "body.xml")
    PositionPath = (Join-Path $OutputDirectory "position.xml")
} | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "result.json") -Encoding UTF8
