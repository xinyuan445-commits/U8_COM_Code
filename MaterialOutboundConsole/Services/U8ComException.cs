using System;
using System.Runtime.InteropServices;

namespace MaterialOutboundConsole.Services
{
    internal static class U8ComException
    {
        public static InvalidOperationException Wrap(
            string operation,
            COMException exception)
        {
            return new InvalidOperationException(
                operation + "发生 Automation/COM 错误，HRESULT=0x" +
                exception.ErrorCode.ToString("X8") + "，详情：" + exception.Message,
                exception);
        }
    }
}
