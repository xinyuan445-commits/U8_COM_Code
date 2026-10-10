using System;
using System.Text;

namespace MaterialOutboundConsole.ConsoleUi
{
    internal static class ConsoleSecret
    {
        public static string Read(string prompt)
        {
            Console.Write(prompt);
            var value = new StringBuilder();

            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (value.Length > 0)
                    {
                        value.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    value.Append(key.KeyChar);
                    Console.Write('*');
                }
            }

            if (value.Length == 0)
            {
                throw new InvalidOperationException("密码不能为空。");
            }

            return value.ToString();
        }
    }
}
