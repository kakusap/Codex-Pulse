using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms=System.Windows.Forms;

namespace CodexPulse {
    public static class MonitorDock {
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
        [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
        public static Forms.Screen Resolve(string device) {
            foreach(var screen in Forms.Screen.AllScreens) if(string.Equals(screen.DeviceName,device,StringComparison.OrdinalIgnoreCase)) return screen;
            return Forms.Screen.PrimaryScreen;
        }
        public static Forms.Screen Current(Window window) {
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            return hwnd==IntPtr.Zero?Forms.Screen.PrimaryScreen:Forms.Screen.FromHandle(hwnd);
        }
        public static double WorkHeight(Window window,string device) {
            return Resolve(device).WorkingArea.Height/VisualTreeHelper.GetDpi(window).DpiScaleY;
        }
        public static void TopCenter(Window window,string device) {
            var area=Resolve(device).WorkingArea;
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            if(hwnd==IntPtr.Zero) return;
            NativeRect bounds;
            if(!GetWindowRect(hwnd,out bounds)) return;
            // Both rectangles are physical pixels. Avoid mixing the primary
            // screen's WPF coordinates with another screen's scale or origin.
            int left=area.Left+(area.Width-(bounds.Right-bounds.Left))/2;
            if(!SetWindowPos(hwnd,IntPtr.Zero,left,area.Top,0,0,0x0001|0x0004|0x0010))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public static void PlaceBelow(Window popup,Window owner,FrameworkElement anchor,string device) {
            var area=Resolve(device).WorkingArea;IntPtr hwnd=new WindowInteropHelper(popup).Handle;NativeRect bounds;
            if(hwnd==IntPtr.Zero || !GetWindowRect(hwnd,out bounds)) return;
            Point start=anchor.PointToScreen(new Point(0,0)),bottom=owner.PointToScreen(new Point(0,56));
            int left=Math.Max(area.Left,Math.Min((int)Math.Round(start.X),area.Right-(bounds.Right-bounds.Left)));
            int top=Math.Max(area.Top,Math.Min((int)Math.Round(bottom.Y)+6,area.Bottom-(bounds.Bottom-bounds.Top)));
            if(!SetWindowPos(hwnd,new IntPtr(owner.Topmost?-1:0),left,top,0,0,0x0001|0x0010|0x0200)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
