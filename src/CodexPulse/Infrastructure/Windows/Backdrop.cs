using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexPulse {
    public static class Backdrop {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)] static extern int SetWindowLong(IntPtr hwnd,int index,int value);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd,int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
        [StructLayout(LayoutKind.Sequential)] struct CursorPoint { public int X,Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint point);
        const int ClickThroughStyles=0x00000020|0x08000000; // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
        public static bool IsClickThrough(Window window) {
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            return hwnd!=IntPtr.Zero && (GetWindowLong(hwnd,-20)&ClickThroughStyles)==ClickThroughStyles;
        }
        public static void SetClickThrough(Window window,bool enabled) {
            SetClickThrough(new WindowInteropHelper(window).Handle,enabled);
        }
        public static void SetClickThrough(IntPtr hwnd,bool enabled) {
            if(hwnd==IntPtr.Zero) return;
            int current=GetWindowLong(hwnd,-20);
            int next=enabled ? current|ClickThroughStyles : current&~ClickThroughStyles;
            if(current==next) return;
            // This layered window must be skipped by Windows hit testing across applications.
            // HTTRANSPARENT alone only forwards hits within the same UI thread.
            if(SetWindowLong(hwnd,-20,next)==0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!SetWindowPos(hwnd,IntPtr.Zero,0,0,0,0,0x0001|0x0002|0x0004|0x0010|0x0020)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public static bool TryGetCursorPosition(out Point point) {
            CursorPoint cursor;bool found=GetCursorPos(out cursor);
            point=new Point(cursor.X,cursor.Y);return found;
        }
        public static void SetPinned(Window window) {
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            if(hwnd!=IntPtr.Zero) SetWindowPos(hwnd,new IntPtr(window.Topmost?-1:-2),0,0,0,0,0x0001|0x0002|0x0010|0x0200);
        }
        public static void Reveal(Window window) {
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            ShowWindowAsync(hwnd,1);SetPinned(window);SetForegroundWindow(hwnd);
        }
        public static string Apply(Window window,int opacity) {
            IntPtr hwnd=new WindowInteropHelper(window).Handle;
            HwndSource source=HwndSource.FromHwnd(hwnd);
            if(source!=null) {
                source.CompositionTarget.BackgroundColor=Colors.Transparent;
                // Layered HWND alpha only affects painted background pixels.
                // Text stays opaque; the desktop behind it stays sharp.
                source.AddHook(delegate(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
                    if(message!=0x84 || window.WindowState!=WindowState.Normal) return IntPtr.Zero;
                    if(IsClickThrough(window)) { handled=true;return new IntPtr(-1); }
                    long packed=lParam.ToInt64();
                    Point point=window.PointFromScreen(new Point((short)(packed&0xffff),(short)((packed>>16)&0xffff)));
                    const double edge=6;
                    bool left=point.X<edge,right=point.X>=window.ActualWidth-edge,top=point.Y<edge,bottom=point.Y>=window.ActualHeight-edge;
                    int hit=top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:0;
                    if(hit==0) return IntPtr.Zero;
                    handled=true;return new IntPtr(hit);
                });
            }
            int none=1,dark=1;
            DwmSetWindowAttribute(hwnd,38,ref none,4);
            DwmSetWindowAttribute(hwnd,20,ref dark,4);
            return SetTint(window,opacity);
        }
        public static string SetTint(Window window,int opacity) {
            var surface=window.FindName("Surface") as System.Windows.Controls.Border;
            if(surface!=null) {
                byte alpha=(byte)(Math.Max(0,Math.Min(85,opacity))*255.0/100);
                object themeColor=window.TryFindResource("Theme.BackgroundColor");
                Color tint=themeColor is Color?(Color)themeColor:Color.FromRgb(18,28,43);
                var gradient=new LinearGradientBrush { StartPoint=new Point(0,0),EndPoint=new Point(1,1) };
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(alpha,tint.R,tint.G,tint.B),0));
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha*0.85),(byte)(tint.R*0.85),(byte)(tint.G*0.85),(byte)(tint.B*0.85)),0.55));
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(alpha,tint.R,tint.G,tint.B),1));
                surface.Background=gradient;
            }
            return "Transparent background / opacity="+opacity+"%";
        }
        public static int CurrentBackdrop(Window window) { int value;DwmGetWindowAttribute(new WindowInteropHelper(window).Handle,38,out value,4);return value; }
    }
}
