using System;
using System.Runtime.InteropServices;
using MaterialOutboundConsole.Models;
using USERPCO;

namespace MaterialOutboundConsole.Services
{
    internal sealed class U8CoSession : IDisposable
    {
        public U8Login.clsLogin Login { get; private set; }
        public VoucherCO Voucher { get; private set; }

        public U8CoSession(U8Account account)
        {
            try
            {
                Login = new U8Login.clsLogin();
            }
            catch (COMException exception)
            {
                throw U8ComException.Wrap("创建 U8Login.clsLogin 组件时", exception);
            }

            bool loggedIn;
            try
            {
                loggedIn = Login.Login(
                    account.SubId,
                    account.AccId,
                    account.Year,
                    account.UserId,
                    account.Password,
                    account.Date,
                    account.Server,
                    account.Serial);
            }
            catch (COMException exception)
            {
                Dispose();
                throw U8ComException.Wrap(
                    "调用 U8Login.Login（账套登录）时",
                    exception);
            }

            if (!loggedIn)
            {
                string detail = Login.ShareString;
                Dispose();
                throw new InvalidOperationException("U8 登录失败：" + detail);
            }

            try
            {
                Voucher = new VoucherCOClass();
            }
            catch (COMException exception)
            {
                Dispose();
                throw U8ComException.Wrap(
                    "创建库存管理 VoucherCO 组件时",
                    exception);
            }

            var error = string.Empty;
            bool initialized;
            try
            {
                initialized = Voucher.IniLogin(Login, ref error);
            }
            catch (COMException exception)
            {
                Dispose();
                throw U8ComException.Wrap(
                    "调用 VoucherCO.IniLogin（库存模块初始化）时",
                    exception);
            }

            if (!initialized)
            {
                Dispose();
                throw new InvalidOperationException("库存管理 CO 初始化失败：" + error);
            }
        }

        public void Dispose()
        {
            if (Login != null)
            {
                try
                {
                    Login.ShutDown();
                }
                catch
                {
                    // 注销失败时仍须释放 COM，避免 IIS 工作进程长期占用 U8 登录。
                }
            }
            Release(Voucher);
            Release(Login);
            Voucher = null;
            Login = null;
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
    }
}
