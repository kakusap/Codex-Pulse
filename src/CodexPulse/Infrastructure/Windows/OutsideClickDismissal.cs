using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace CodexPulse {
    // A passive hook also observes clicks when the dashboard never takes focus
    // (mouse-through mode). Always forward the click to its original target.
    public sealed class OutsideClickDismissal : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X,Y; }
        delegate IntPtr MouseHook(int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id,MouseHook callback,IntPtr module,uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("kernel32.dll",CharSet=CharSet.Auto)] static extern IntPtr GetModuleHandle(string name);
        readonly Window owner;
        readonly Func<bool> active;
        readonly Action dismiss;
        readonly MouseHook callback;
        readonly uint process=(uint)Process.GetCurrentProcess().Id;
        IntPtr hook;
        bool pending,disposed;

        public OutsideClickDismissal(Window owner,Func<bool> active,Action dismiss) {
            this.owner=owner;this.active=active;this.dismiss=dismiss;callback=OnMouse;
            hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0); // WH_MOUSE_LL
            if(hook==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        IntPtr OnMouse(int code,IntPtr message,IntPtr data) {
            int kind=message.ToInt32();
            if(code>=0 && !disposed && !pending && owner.IsVisible && active() &&
                (kind==0x0201 || kind==0x0204 || kind==0x0207 || kind==0x020B)) {
                var point=(NativePoint)Marshal.PtrToStructure(data,typeof(NativePoint));
                uint target;GetWindowThreadProcessId(WindowFromPoint(point),out target);
                // Includes account/settings windows, input islands and WPF menus.
                if(target!=process) {
                    pending=true;
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate {
                        pending=false;
                        if(!disposed && owner.IsVisible && active()) dismiss();
                    }));
                }
            }
            return CallNextHookEx(hook,code,message,data);
        }
        public void Dispose() {
            if(disposed) return;disposed=true;
            if(hook!=IntPtr.Zero) { UnhookWindowsHookEx(hook);hook=IntPtr.Zero; }
        }
    }
}
