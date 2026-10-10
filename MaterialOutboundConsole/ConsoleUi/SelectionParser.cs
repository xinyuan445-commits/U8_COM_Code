using System;
using System.Collections.Generic;
using System.Linq;

namespace MaterialOutboundConsole.ConsoleUi
{
    internal static class SelectionParser
    {
        public static List<int> Parse(string input, int itemCount)
        {
            if (itemCount <= 0)
            {
                return new List<int>();
            }

            if (string.Equals(input == null ? null : input.Trim(), "all",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Enumerable.Range(1, itemCount).ToList();
            }

            var selected = new SortedSet<int>();
            foreach (var part in (input ?? string.Empty)
                         .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                var dash = token.IndexOf('-');
                if (dash < 0)
                {
                    AddIndex(selected, ParseIndex(token), itemCount);
                    continue;
                }

                if (token.IndexOf('-', dash + 1) >= 0)
                {
                    throw new FormatException("选择范围格式错误：" + token);
                }

                var start = ParseIndex(token.Substring(0, dash).Trim());
                var end = ParseIndex(token.Substring(dash + 1).Trim());
                if (start > end)
                {
                    throw new FormatException("选择范围起始值不能大于结束值：" + token);
                }

                for (var index = start; index <= end; index++)
                {
                    AddIndex(selected, index, itemCount);
                }
            }

            if (selected.Count == 0)
            {
                throw new FormatException("至少选择一条记录。");
            }

            return selected.ToList();
        }

        private static int ParseIndex(string value)
        {
            int index;
            if (!int.TryParse(value, out index))
            {
                throw new FormatException("无法识别选择项：" + value);
            }

            return index;
        }

        private static void AddIndex(
            ISet<int> selected,
            int index,
            int itemCount)
        {
            if (index < 1 || index > itemCount)
            {
                throw new IndexOutOfRangeException(
                    "选择项 " + index + " 超出范围 1-" + itemCount + "。");
            }

            selected.Add(index);
        }
    }
}
