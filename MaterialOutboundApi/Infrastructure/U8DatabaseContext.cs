using System;
using System.Threading;

namespace MaterialOutboundApi.Infrastructure
{
    internal static class U8DatabaseContext
    {
        private static readonly AsyncLocal<string> CurrentValue =
            new AsyncLocal<string>();

        public static string Current
        {
            get => CurrentValue.Value;
            set => CurrentValue.Value = value;
        }

        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string database = value.Trim().ToUpperInvariant();
            if (!string.Equals(
                    database,
                    "UFDATA_998_2021",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    database,
                    "UFDATA_888_2021",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    database,
                    "UFDATA_008_2021",
                    StringComparison.Ordinal))
            {
                return null;
            }
            return database;
        }
    }
}
