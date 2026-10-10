using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MaterialOutboundApi.Infrastructure;
using MaterialOutboundApi.Models;
using Newtonsoft.Json;

namespace MaterialOutboundApi.Services
{
    internal sealed class SettingsStore
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("MaterialOutboundApi.Settings.v1");
        private readonly object _sync = new object();
        private readonly string _path;
        private ApiSettings _current;

        public SettingsStore(string path)
        {
            _path = path;
            _current = Load();
        }

        public ApiSettings Current
        {
            get
            {
                lock (_sync)
                {
                    return Clone(_current);
                }
            }
        }

        public bool IsConfigured =>
            Current != null &&
            !string.IsNullOrWhiteSpace(Current.SqlPassword) &&
            !string.IsNullOrWhiteSpace(Current.U8Password) &&
            !string.IsNullOrWhiteSpace(Current.ApiKey) &&
            !string.IsNullOrWhiteSpace(Current.AdminPasswordHash);

        public ApiSettings Merge(SettingsInput input)
        {
            ApiSettings prior = Current ?? new ApiSettings();
            return new ApiSettings
            {
                SqlServer = Trim(input.SqlServer),
                SqlDatabase = Trim(input.SqlDatabase),
                SqlUser = Trim(input.SqlUser),
                SqlPassword = string.IsNullOrWhiteSpace(input.SqlPassword)
                    ? prior.SqlPassword
                    : input.SqlPassword,
                U8Server = Trim(input.U8Server),
                U8AccId = Trim(input.U8AccId),
                U8Year = Trim(input.U8Year),
                U8SubId = Trim(input.U8SubId),
                U8UserId = Trim(input.U8UserId),
                U8Password = string.IsNullOrWhiteSpace(input.U8Password)
                    ? prior.U8Password
                    : input.U8Password,
                U8Serial = Trim(input.U8Serial),
                WarehouseCode = Trim(input.WarehouseCode),
                PositionCode = Trim(input.PositionCode),
                Maker = Trim(input.Maker),
                DefaultDepartmentCode = Trim(input.DefaultDepartmentCode),
                WorkCenterCode = Trim(input.WorkCenterCode),
                RdCode = Trim(input.RdCode),
                VoucherTemplateId = input.VoucherTemplateId,
                DefaultUnitCost = input.DefaultUnitCost,
                Shape008 = new ShapeConversionProfile
                {
                    DepartmentCode = Trim(input.Shape008DepartmentCode),
                    InboundRdCode = Trim(input.Shape008InboundRdCode),
                    OutboundRdCode = Trim(input.Shape008OutboundRdCode),
                    VoucherTemplateId = input.Shape008VoucherTemplateId
                },
                Shape998 = new ShapeConversionProfile
                {
                    DepartmentCode = Trim(input.Shape998DepartmentCode),
                    InboundRdCode = Trim(input.Shape998InboundRdCode),
                    OutboundRdCode = Trim(input.Shape998OutboundRdCode),
                    VoucherTemplateId = input.Shape998VoucherTemplateId
                },
                ApiKey = string.IsNullOrWhiteSpace(prior.ApiKey)
                    ? GenerateKey()
                    : prior.ApiKey,
                AdminPasswordHash = prior.AdminPasswordHash,
                AdminPasswordSalt = prior.AdminPasswordSalt,
                Revision = prior.Revision + 1
            };
        }

        public string Save(ApiSettings settings, string newAdminPassword)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(settings.AdminPasswordHash))
                {
                    if (string.IsNullOrWhiteSpace(newAdminPassword) ||
                        newAdminPassword.Length < 12)
                    {
                        throw new ApiException(
                            System.Net.HttpStatusCode.BadRequest,
                            "ADMIN_PASSWORD_REQUIRED",
                            "首次配置必须设置至少 12 位的管理密码。");
                    }
                    SetAdminPassword(settings, newAdminPassword);
                }

                Persist(settings);
                _current = Clone(settings);
                return settings.ApiKey;
            }
        }

        public string RotateApiKey()
        {
            lock (_sync)
            {
                ApiSettings settings = Clone(_current);
                settings.ApiKey = GenerateKey();
                settings.Revision++;
                Persist(settings);
                _current = settings;
                return settings.ApiKey;
            }
        }

        public bool VerifyAdmin(string password)
        {
            ApiSettings settings = Current;
            if (settings == null ||
                string.IsNullOrWhiteSpace(settings.AdminPasswordHash))
            {
                return true;
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                return false;
            }

            byte[] salt = Convert.FromBase64String(settings.AdminPasswordSalt);
            byte[] expected = Convert.FromBase64String(settings.AdminPasswordHash);
            using (var derive = new Rfc2898DeriveBytes(
                password,
                salt,
                100000,
                HashAlgorithmName.SHA256))
            {
                byte[] actual = derive.GetBytes(expected.Length);
                return FixedTimeEquals(expected, actual);
            }
        }

        public SettingsView GetView()
        {
            ApiSettings value = Current ?? new ApiSettings();
            return new SettingsView
            {
                Configured = IsConfigured,
                SqlServer = value.SqlServer,
                SqlDatabase = value.SqlDatabase,
                SqlUser = value.SqlUser,
                HasSqlPassword = !string.IsNullOrWhiteSpace(value.SqlPassword),
                U8Server = value.U8Server,
                U8AccId = value.U8AccId,
                U8Year = value.U8Year,
                U8SubId = value.U8SubId,
                U8UserId = value.U8UserId,
                HasU8Password = !string.IsNullOrWhiteSpace(value.U8Password),
                U8Serial = value.U8Serial,
                WarehouseCode = value.WarehouseCode,
                PositionCode = value.PositionCode,
                Maker = value.Maker,
                DefaultDepartmentCode = value.DefaultDepartmentCode,
                WorkCenterCode = value.WorkCenterCode,
                RdCode = value.RdCode,
                VoucherTemplateId = value.VoucherTemplateId,
                DefaultUnitCost = value.DefaultUnitCost,
                Shape008DepartmentCode = value.Shape008?.DepartmentCode,
                Shape008InboundRdCode = value.Shape008?.InboundRdCode,
                Shape008OutboundRdCode = value.Shape008?.OutboundRdCode,
                Shape008VoucherTemplateId = value.Shape008?.VoucherTemplateId ?? 91,
                Shape998DepartmentCode = value.Shape998?.DepartmentCode,
                Shape998InboundRdCode = value.Shape998?.InboundRdCode,
                Shape998OutboundRdCode = value.Shape998?.OutboundRdCode,
                Shape998VoucherTemplateId = value.Shape998?.VoucherTemplateId ?? 91,
                ApiKeyMasked = Mask(value.ApiKey),
                Revision = value.Revision
            };
        }

        public static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null)
            {
                return false;
            }
            return FixedTimeEquals(
                Encoding.UTF8.GetBytes(left),
                Encoding.UTF8.GetBytes(right));
        }

        private ApiSettings Load()
        {
            if (!File.Exists(_path))
            {
                return new ApiSettings();
            }
            var stored = JsonConvert.DeserializeObject<PersistedSettings>(
                File.ReadAllText(_path));
            return stored?.ToRuntime() ?? new ApiSettings();
        }

        private void Persist(ApiSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string json = JsonConvert.SerializeObject(
                PersistedSettings.FromRuntime(settings),
                Formatting.Indented);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, null);
            }
            else
            {
                File.Move(temp, _path);
            }
        }

        private static void SetAdminPassword(
            ApiSettings settings,
            string password)
        {
            byte[] salt = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(salt);
            }
            using (var derive = new Rfc2898DeriveBytes(
                password,
                salt,
                100000,
                HashAlgorithmName.SHA256))
            {
                settings.AdminPasswordSalt = Convert.ToBase64String(salt);
                settings.AdminPasswordHash =
                    Convert.ToBase64String(derive.GetBytes(32));
            }
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                Entropy,
                DataProtectionScope.LocalMachine));
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(value),
                Entropy,
                DataProtectionScope.LocalMachine));
        }

        private static string GenerateKey()
        {
            byte[] value = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(value);
            }
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string Mask(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return value.Length <= 8
                ? "********"
                : value.Substring(0, 4) + "…" +
                  value.Substring(value.Length - 4);
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            int result = 0;
            for (int i = 0; i < left.Length; i++)
            {
                result |= left[i] ^ right[i];
            }
            return result == 0;
        }

        private static string Trim(string value) =>
            value == null ? string.Empty : value.Trim();

        private static ApiSettings Clone(ApiSettings value)
        {
            return value == null
                ? null
                : JsonConvert.DeserializeObject<ApiSettings>(
                    JsonConvert.SerializeObject(value));
        }

        private sealed class PersistedSettings
        {
            public ApiSettings Public { get; set; }
            public string SqlPasswordProtected { get; set; }
            public string U8PasswordProtected { get; set; }
            public string ApiKeyProtected { get; set; }

            public static PersistedSettings FromRuntime(ApiSettings value)
            {
                ApiSettings publicValue = Clone(value);
                publicValue.SqlPassword = null;
                publicValue.U8Password = null;
                publicValue.ApiKey = null;
                return new PersistedSettings
                {
                    Public = publicValue,
                    SqlPasswordProtected = Protect(value.SqlPassword),
                    U8PasswordProtected = Protect(value.U8Password),
                    ApiKeyProtected = Protect(value.ApiKey)
                };
            }

            public ApiSettings ToRuntime()
            {
                ApiSettings value = Public ?? new ApiSettings();
                value.SqlPassword = Unprotect(SqlPasswordProtected);
                value.U8Password = Unprotect(U8PasswordProtected);
                value.ApiKey = Unprotect(ApiKeyProtected);
                return value;
            }
        }
    }
}
