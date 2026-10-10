using System;
using System.IO;
using GeneralInventoryCount;
using GeneralMaterialOutbound;
using MaterialOutboundApi.Services;

namespace MaterialOutboundApi.Infrastructure
{
    internal static class AppRuntime
    {
        private static readonly object Sync = new object();

        public static SettingsStore Settings { get; private set; }
        public static StateStore State { get; private set; }
        public static U8OperationQueue U8Queue { get; private set; }
        public static MaterialOutboundService MaterialOutbound { get; private set; }
        public static ShapeConversionService ShapeConversion { get; private set; }
        public static string DataDirectory { get; private set; }

        public static void Initialize(string dataDirectory)
        {
            lock (Sync)
            {
                if (Settings != null)
                {
                    return;
                }

                DataDirectory = dataDirectory;
                Directory.CreateDirectory(dataDirectory);
                Directory.CreateDirectory(Path.Combine(dataDirectory, "Logs"));
                Settings = new SettingsStore(
                    Path.Combine(dataDirectory, "settings.json"));
                State = new StateStore(
                    Path.Combine(dataDirectory, "state.db"));
                U8Queue = new U8OperationQueue();
                MaterialOutbound = new MaterialOutboundService(
                    Settings,
                    State,
                    U8Queue,
                    AppDomain.CurrentDomain.BaseDirectory);
                ShapeConversion = new ShapeConversionService(
                    Settings,
                    State,
                    U8Queue,
                    AppDomain.CurrentDomain.BaseDirectory);
                GeneralMaterialOutboundRuntime.Initialize(
                    new GeneralMaterialOutboundGateway(
                        Settings,
                        State,
                        U8Queue,
                        AppDomain.CurrentDomain.BaseDirectory));
                GeneralInventoryCountRuntime.Initialize(
                    new GeneralInventoryCountGateway(
                        Settings,
                        State,
                        U8Queue));
            }
        }

        public static void Dispose()
        {
            lock (Sync)
            {
                U8Queue?.Dispose();
                State?.Dispose();
                U8Queue = null;
                State = null;
                MaterialOutbound = null;
                ShapeConversion = null;
                Settings = null;
                GeneralMaterialOutboundRuntime.Reset();
                GeneralInventoryCountRuntime.Reset();
            }
        }
    }
}
