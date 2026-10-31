using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CodexPulse {
    public static class StartupRegistration {
        const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName="CodexPulse";
        public static string Command { get { return "\""+Process.GetCurrentProcess().MainModule.FileName+"\" --autostart"; } }
        public static string Read() { using(var key=Registry.CurrentUser.OpenSubKey(RunKey)) return key==null?null:key.GetValue(ValueName) as string; }
        public static bool IsEnabled(string command) { return string.Equals(command,Command,StringComparison.OrdinalIgnoreCase); }
        public static void Set(bool enabled) { Restore(enabled?Command:null); }
        public static void Restore(string command) {
            if(command==null) { using(var key=Registry.CurrentUser.OpenSubKey(RunKey,true)) if(key!=null) key.DeleteValue(ValueName,false); }
            else { using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue(ValueName,command,RegistryValueKind.String); }
        }
        // Exercise the real per-user startup key using a unique temporary name.
        // Never toggle the user's CodexPulse entry during developer verification.
        public static string Verify() {
            string testName="CodexPulse.Check."+Guid.NewGuid().ToString("N");
            string before=Read();
            using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) {
                try {
                    key.SetValue(testName,Command,RegistryValueKind.String);
                    if((string)key.GetValue(testName)!=Command) throw new IOException("Startup registration did not round trip");
                    key.DeleteValue(testName);
                    if(key.GetValue(testName)!=null) throw new IOException("Startup registration was not removed");
                } finally { key.DeleteValue(testName,false); }
            }
            if(Read()!=before) throw new IOException("Existing startup preference changed");
            return "PASS: per-user Run entry can be enabled and removed; existing preference preserved";
        }
    }
}
