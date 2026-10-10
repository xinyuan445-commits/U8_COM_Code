using System;
using System.Globalization;
using System.IO;
using System.Linq;
using MaterialOutboundConsole.Configuration;
using MaterialOutboundConsole.ConsoleUi;
using MaterialOutboundConsole.Models;
using MaterialOutboundConsole.Services;

namespace MaterialOutboundConsole
{
    internal static class Program
    {
        private const string ConfirmFlag = "--confirm";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            try
            {
                var options = AppOptions.Load(ConsoleSecret.Read);
                var service = new MaterialOutboundService(options);
                var command = args[0].ToLowerInvariant();

                switch (command)
                {
                    case "select-add":
                        RequireArgumentCount(args, 1);
                        RunSelectAdd(service, options);
                        break;

                    case "preview-add":
                        RequireArgumentCount(args, 4);
                        var files = service.PreviewAdd(
                            args[1],
                            NormalizeAutomaticCode(args[2]),
                            null,
                            args[3],
                            null);
                        Console.WriteLine("新增 XML 已生成，未提交到 U8：");
                        foreach (var file in files)
                        {
                            Console.WriteLine(file);
                        }
                        break;

                    case "add":
                        RequireConfirmed(args, 5);
                        EnsureU8Password(options);
                        var addResult = service.Add(
                            args[1],
                            NormalizeAutomaticCode(args[2]),
                            null,
                            args[3],
                            null);
                        Console.WriteLine(
                            "新增成功，单据号：{0}，U8 单据 ID：{1}",
                            addResult.VoucherCode,
                            addResult.VoucherId);
                        break;

                    case "query":
                        RequireArgumentCount(args, 2);
                        EnsureU8Password(options);
                        Console.WriteLine("查询成功，XML 输出目录：" + service.Query(args[1]));
                        break;

                    case "export-shape-template":
                        RequireArgumentCount(args, 2);
                        EnsureU8Password(options);
                        Console.WriteLine(
                            "形态转换单模板导出成功，输出目录：" +
                            service.ExportShapeConversionTemplate(args[1]));
                        break;

                    case "update":
                        RequireConfirmed(args, 5);
                        EnsureU8Password(options);
                        int autoId;
                        decimal quantity;
                        if (!int.TryParse(args[2], out autoId))
                        {
                            throw new ArgumentException("AutoID 必须是整数。");
                        }
                        if (!decimal.TryParse(
                                args[3],
                                NumberStyles.Number,
                                CultureInfo.InvariantCulture,
                                out quantity))
                        {
                            throw new ArgumentException("新数量必须是数字，小数点请使用 .。");
                        }
                        service.UpdateQuantity(args[1], autoId, quantity);
                        Console.WriteLine("修改成功。");
                        break;

                    case "verify":
                        RequireConfirmed(args, 3);
                        EnsureU8Password(options);
                        service.Verify(args[1]);
                        Console.WriteLine("审核成功。");
                        break;

                    case "unverify":
                        RequireConfirmed(args, 3);
                        EnsureU8Password(options);
                        service.Unverify(args[1]);
                        Console.WriteLine("弃审成功。");
                        break;

                    case "delete":
                        RequireConfirmed(args, 3);
                        EnsureU8Password(options);
                        service.Delete(args[1]);
                        Console.WriteLine("删除成功。");
                        break;

                    default:
                        throw new ArgumentException("未知命令：" + args[0]);
                }

                return 0;
            }
            catch (Exception exception)
            {
                var logPath = WriteErrorLog(exception);
                Console.Error.WriteLine();
                Console.Error.WriteLine("执行失败：");
                Console.Error.WriteLine(exception);
                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("完整错误日志：" + logPath);
                }
                return 1;
            }
        }

        private static bool IsHelp(string value)
        {
            return value == "-h" || value == "--help" || value == "/?";
        }

        private static void RequireArgumentCount(string[] args, int count)
        {
            if (args.Length != count)
            {
                throw new ArgumentException("参数数量不正确，请使用 --help 查看示例。");
            }
        }

        private static void RequireConfirmed(string[] args, int count)
        {
            RequireArgumentCount(args, count);
            if (!args.Last().Equals(ConfirmFlag, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "该命令会修改 U8 数据，确认后请在命令末尾添加 " + ConfirmFlag);
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("用友 U8 材料出库单控制台示例");
            Console.WriteLine();
            Console.WriteLine("首次使用：先编辑 MaterialOutboundConsole.exe.config。");
            Console.WriteLine();
            Console.WriteLine("命令：");
            Console.WriteLine("  select-add  交互输入工单并选择明细");
            Console.WriteLine("  preview-add <生产订单号> <材料出库单号|auto> <仓库编码>");
            Console.WriteLine("  add         <生产订单号> <材料出库单号|auto> <仓库编码> --confirm");
            Console.WriteLine("  query       <材料出库单号>");
            Console.WriteLine("  export-shape-template <形态转换单号>");
            Console.WriteLine("  update      <材料出库单号> <表体AutoID> <新数量> --confirm");
            Console.WriteLine("  verify      <材料出库单号> --confirm");
            Console.WriteLine("  unverify    <材料出库单号> --confirm");
            Console.WriteLine("  delete      <材料出库单号> --confirm");
            Console.WriteLine();
            Console.WriteLine("所有写操作都必须显式添加 --confirm。");
        }

        private static void RunSelectAdd(
            MaterialOutboundService service,
            AppOptions options)
        {
            Console.Write("请输入工单号：");
            var productionOrderCode = (Console.ReadLine() ?? string.Empty).Trim();
            if (productionOrderCode.Length == 0)
            {
                throw new ArgumentException("工单号不能为空。");
            }

            var lines = service.GetWorkOrderLines(productionOrderCode);
            if (lines.Count == 0)
            {
                throw new InvalidOperationException("没有找到工单：" + productionOrderCode);
            }

            PrintWorkOrderLines(lines);
            Console.Write("选择记录（例如 1,3-5，全部输入 all）：");
            var indexes = SelectionParser.Parse(Console.ReadLine(), lines.Count);
            var selected = indexes.Select(index => lines[index - 1]).ToList();

            var withoutAllocation = selected
                .Where(item => item.AllocationCount <= 0)
                .ToList();
            if (withoutAllocation.Count > 0)
            {
                throw new InvalidOperationException(
                    "以下所选记录没有材料子件，不能生成材料出库单：" +
                    string.Join(", ", withoutAllocation.Select(item => item.SortSeq)));
            }

            var departments = selected
                .Select(item => item.DepartmentCode)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (departments.Count > 1)
            {
                throw new InvalidOperationException(
                    "所选记录包含多个部门（" + string.Join(", ", departments) +
                    "），请按部门分开选择。");
            }

            var warehouseCode = options.WarehouseCode;
            if (warehouseCode.Length == 0)
            {
                throw new ArgumentException("配置项 WarehouseCode 不能为空。");
            }
            Console.WriteLine("材料出库仓库编码（固定）：" + warehouseCode);

            var positionCode = options.DefaultPositionCode;
            if (positionCode.Length == 0)
            {
                throw new ArgumentException("配置项 DefaultPositionCode 不能为空。");
            }
            Console.WriteLine("货位编码（固定）：" + positionCode);
            Console.WriteLine("批号（固定）：" + options.DefaultBatchCode);

            Console.Write("请输入材料出库单号（直接回车则尝试U8自动编号）：");
            var outboundCode = (Console.ReadLine() ?? string.Empty).Trim();

            var selectedIds = selected.Select(item => item.MoDId).ToList();
            var preview = service.PreviewAdd(
                productionOrderCode,
                outboundCode,
                selectedIds,
                warehouseCode,
                positionCode);

            Console.WriteLine();
            Console.WriteLine("已选择 {0} 条工单明细，共 {1} 条材料子件。",
                selected.Count,
                selected.Sum(item => item.AllocationCount));
            Console.WriteLine("XML 预览：");
            foreach (var file in preview)
            {
                Console.WriteLine("  " + file);
            }

            Console.WriteLine();
            Console.Write("确认提交到 U8 请输入 YES；仅保留预览请直接回车：");
            var confirmation = (Console.ReadLine() ?? string.Empty).Trim();
            if (!confirmation.Equals("YES", StringComparison.Ordinal))
            {
                Console.WriteLine("未提交 U8，XML 预览已保留。");
                return;
            }

            EnsureU8Password(options);
            var result = service.Add(
                productionOrderCode,
                outboundCode,
                selectedIds,
                warehouseCode,
                positionCode);
            Console.WriteLine(
                "新增成功，单据号：{0}，U8 单据 ID：{1}",
                result.VoucherCode,
                result.VoucherId);
        }

        private static void PrintWorkOrderLines(
            System.Collections.Generic.IList<WorkOrderLine> lines)
        {
            Console.WriteLine();
            Console.WriteLine(
                "{0,-4} {1,-6} {2,-12} {3,-10} {4,-6} {5,-6} {6}",
                "序号",
                "行号",
                "数量",
                "部门",
                "状态",
                "子件",
                "产品编码");
            Console.WriteLine(new string('-', 88));

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                Console.WriteLine(
                    "{0,-4} {1,-6} {2,-12} {3,-10} {4,-6} {5,-6} {6}",
                    index + 1,
                    line.SortSeq,
                    line.Qty,
                    line.DepartmentCode ?? "-",
                    line.Status,
                    line.AllocationCount,
                    Truncate(line.InvCode, 38));
            }

            Console.WriteLine();
        }

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= length)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, length - 3) + "...";
        }

        private static void EnsureU8Password(AppOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Account.Password))
            {
                options.Account.Password = ConsoleSecret.Read("请输入 U8 操作员密码：");
            }
        }

        private static string NormalizeAutomaticCode(string value)
        {
            return string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : value;
        }

        private static string WriteErrorLog(Exception exception)
        {
            try
            {
                var directory = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(
                    directory,
                    "error-" + DateTime.Now.ToString(
                        "yyyyMMdd-HHmmss",
                        CultureInfo.InvariantCulture) + ".log");
                File.WriteAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    Environment.NewLine +
                    exception);
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
