param(
    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",
    [ValidateSet("UFDATA_008_2021", "UFDATA_998_2021")]
    [string]$Database = "UFDATA_008_2021",
    [string]$VoucherCode = "PDD26091292",
    [string]$OutputDirectory = "D:\MupiInventoryCountResearch\export11"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
trap {
    ($_ | Out-String) | Set-Content -LiteralPath `
        (Join-Path $OutputDirectory "error.txt") -Encoding UTF8
    exit 1
}

$interopDirectory = Split-Path -Parent $SettingsPath | Split-Path -Parent
$interopDirectory = Join-Path $interopDirectory "bin"
foreach ($assemblyName in @(
    "adodb.dll",
    "Interop.VBA.dll",
    "Interop.Scripting.dll",
    "Interop.USCOMMON.dll",
    "Interop.U8Login.dll",
    "Interop.MSXML2.dll")) {
    Add-Type -Path (Join-Path $interopDirectory $assemblyName)
}
Add-Type -Path "D:\U8SOFT\U8KCSN\bin\Interop.USERPVO.dll"
Add-Type -Path (Join-Path $interopDirectory "Interop.USERPCO.dll")

$helperSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MSXML2;
using USERPCO;

public sealed class U8InventoryXmlAttempt
{
    public string VoucherType { get; set; }
    public bool Loaded { get; set; }
    public string Error { get; set; }
}

public static class U8InventoryXmlExportHelper
{
    public static U8InventoryXmlAttempt[] Export(
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
        U8Login.clsLogin login = null;
        VoucherCO voucher = null;
        var attempts = new List<U8InventoryXmlAttempt>();
        try
        {
            login = new U8Login.clsLogin();
            if (!login.Login(subId, account, year, user, password, loginDate, server, serial))
            {
                throw new InvalidOperationException("U8 login failed: " + login.ShareString);
            }

            voucher = new VoucherCOClass();
            var initializeError = string.Empty;
            if (!voucher.IniLogin(login, ref initializeError))
            {
                throw new InvalidOperationException("VoucherCO init failed: " + initializeError);
            }

            foreach (var voucherType in new[] { "18" })
            {
                IXMLDOMDocument2 head = new DOMDocument26Class();
                IXMLDOMDocument2 body = new DOMDocument26Class();
                IXMLDOMDocument2 position = new DOMDocument26Class();
                var error = string.Empty;
                try
                {
                    var loaded = voucher.Load(
                        voucherType,
                        "ID=" + voucherId,
                        ref head,
                        ref body,
                        ref position,
                        ref error,
                        false,
                        string.Empty);
                    attempts.Add(new U8InventoryXmlAttempt
                    {
                        VoucherType = voucherType,
                        Loaded = loaded,
                        Error = error
                    });
                    if (loaded && head != null && !string.IsNullOrWhiteSpace(head.xml))
                    {
                        var typeDirectory = Path.Combine(outputDirectory, voucherType);
                        Directory.CreateDirectory(typeDirectory);
                        File.WriteAllText(Path.Combine(typeDirectory, "head.xml"), head.xml, Encoding.UTF8);
                        File.WriteAllText(Path.Combine(typeDirectory, "body.xml"), body == null ? string.Empty : body.xml, Encoding.UTF8);
                        File.WriteAllText(Path.Combine(typeDirectory, "position.xml"), position == null ? string.Empty : position.xml, Encoding.UTF8);
                    }
                }
                catch (Exception exception)
                {
                    attempts.Add(new U8InventoryXmlAttempt
                    {
                        VoucherType = voucherType,
                        Loaded = false,
                        Error = exception.Message
                    });
                }
                finally
                {
                    Release(position);
                    Release(body);
                    Release(head);
                }
            }

            return attempts.ToArray();
        }
        finally
        {
            if (login != null)
            {
                try { login.ShutDown(); } catch { }
            }
            Release(voucher);
            Release(login);
        }
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
    (Join-Path $interopDirectory "Interop.U8Login.dll"),
    (Join-Path $interopDirectory "Interop.MSXML2.dll"),
    (Join-Path $interopDirectory "Interop.USERPCO.dll"))

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

$results = [U8InventoryXmlExportHelper]::Export(
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
    Results = $results
} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "result.json") -Encoding UTF8
