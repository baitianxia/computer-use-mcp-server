// Only the Forms host and the automation object are replaced. The test compiles
// this fixture in the same assembly as the configuration code from the bundle.
namespace System.Windows.Forms {
    public class AxHost {
        protected AxHost(string clsid) {
            if (new Guid(clsid) != new Guid("8B918B82-7985-4C24-89DF-C33AD2BBFBCD"))
                throw new ArgumentException("Unexpected RDP control CLSID.");
        }
        protected object GetOcx() {
            RdpTest.Trace.Record("GetOcx");
            return RdpTest.Trace.Control;
        }
    }
}

namespace RdpTest {
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    public static class Trace {
        public static readonly List<string> Calls = new List<string>();
        public static object Control;
        public static int PutResult;
        public static string FailureAt;
        public static int FailureCode;

        public static void Reset() {
            Calls.Clear();
            Control = new ChildControl();
            FailureAt = null;
            FailureCode = 0;
            PutResult = 0;
        }
        public static void Record(string name) {
            Calls.Add(name);
            if (FailureAt == name) throw new COMException("Injected RDP error.", FailureCode);
        }
        public static void RecordValue(string name, object expected, object actual) {
            Record(name);
            if (!Object.Equals(expected, actual))
                throw new ArgumentException("Wrong value for " + name + ": " + actual);
        }
    }

    public class Control {
        public string Server { set { Trace.RecordValue("Server", "localhost", value); } }
        public int DesktopWidth { set { Trace.RecordValue("DesktopWidth", 1024, value); } }
        public int DesktopHeight { set { Trace.RecordValue("DesktopHeight", 660, value); } }
        public AdvancedSettings AdvancedSettings7 { get { Trace.Record("AdvancedSettings7"); return new AdvancedSettings(); } }
        public void Connect() { Trace.Record("Connect"); }
        public int Connected { get { Trace.Record("Connected"); return 1; } }
        public int ExtendedDisconnectReason { get { Trace.Record("ExtendedDisconnectReason"); return 0; } }
        public void Disconnect() { Trace.Record("Disconnect"); }
    }

    public class AdvancedSettings {
        public bool EnableCredSspSupport {
            set { Trace.RecordValue("EnableCredSspSupport", true, value); }
        }
    }

    internal class ChildControl : Control, ParallelDesktop.IExtendedSettings {
        public int Put(string name, ref object value) {
            Trace.Record("Put");
            if (name != "ConnectToChildSession" || !Object.Equals(value, true))
                throw new ArgumentException("Expected child mode as a boxed Boolean.");
            return Trace.PutResult;
        }
    }
}
