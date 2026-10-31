using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexPulse {
    // A tiny input island leaves the dashboard HWND fully click-through. Only
    // the existing pin button's rectangle accepts input, without taking focus.
    public sealed class PinInteraction : IDisposable {
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)] static extern int SetWindowLong(IntPtr hwnd,int index,int value);
        [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);

        readonly Window owner;
        readonly Button target;
        readonly Action click;
        readonly Func<bool> enabled;
        readonly Action drag;
        Window island;
        HwndSource source;
        bool syncing,disposed,hasBounds,lastTopmost;
        int lastLeft,lastTop,lastWidth,lastHeight;

        public PinInteraction(Window owner,Button target,Action click,Func<bool> enabled,Action drag=null) {
            if(owner==null) throw new ArgumentNullException("owner");
            if(target==null) throw new ArgumentNullException("target");
            if(click==null) throw new ArgumentNullException("click");
            if(enabled==null) throw new ArgumentNullException("enabled");
            this.owner=owner;this.target=target;this.click=click;this.enabled=enabled;this.drag=drag;
            owner.LocationChanged+=OnLayoutChanged;
            owner.SizeChanged+=OnSizeChanged;
            owner.IsVisibleChanged+=OnStateChanged;
            owner.IsEnabledChanged+=OnStateChanged;
            owner.StateChanged+=OnLayoutChanged;
            owner.Closed+=OnClosed;
            target.LayoutUpdated+=OnLayoutChanged;
            target.IsVisibleChanged+=OnStateChanged;
            target.IsEnabledChanged+=OnStateChanged;
            Sync();
        }

        public void Sync() {
            if(disposed || syncing) return;
            syncing=true;
            try {
                if(!enabled() || !owner.IsVisible || !owner.IsEnabled || owner.WindowState==WindowState.Minimized ||
                    !target.IsVisible || !target.IsEnabled || target.ActualWidth<=0 || target.ActualHeight<=0 ||
                    PresentationSource.FromVisual(target)==null) {
                    if(island!=null && island.IsVisible) island.Hide();
                    hasBounds=false;return;
                }
                Point first=target.PointToScreen(new Point(0,0));
                Point last=target.PointToScreen(new Point(target.ActualWidth,target.ActualHeight));
                int left=(int)Math.Floor(first.X),top=(int)Math.Floor(first.Y);
                int width=Math.Max(1,(int)Math.Ceiling(last.X)-left),height=Math.Max(1,(int)Math.Ceiling(last.Y)-top);
                EnsureIsland();
                bool showing=!island.IsVisible;
                if(showing) island.Show();
                if(showing || !hasBounds || left!=lastLeft || top!=lastTop || width!=lastWidth || height!=lastHeight || owner.Topmost!=lastTopmost) {
                    IntPtr handle=new WindowInteropHelper(island).Handle;
                    // PointToScreen gives physical pixels, so native placement also
                    // follows the owner correctly across monitors with different DPI.
                    if(!SetWindowPos(handle,new IntPtr(owner.Topmost?-1:0),left,top,width,height,0x0010|0x0200))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    lastLeft=left;lastTop=top;lastWidth=width;lastHeight=height;lastTopmost=owner.Topmost;hasBounds=true;
                }
            } finally { syncing=false; }
        }

        void EnsureIsland() {
            if(island!=null) return;
            // Alpha zero would also make the island transparent to native hit
            // testing. Alpha one is imperceptible but keeps this rectangle live.
            var background=new SolidColorBrush(Color.FromArgb(1,0,0,0));background.Freeze();
            var input=new Button { Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0),
                Focusable=false,IsTabStop=false,FocusVisualStyle=null,Cursor=drag==null?Cursors.Hand:Cursors.SizeAll,OverridesDefaultStyle=true };
            if(drag!=null) input.PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e) { e.Handled=true;if(!disposed && enabled()) drag(); };
            var visual=new FrameworkElementFactory(typeof(Border));
            visual.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            input.Template=new ControlTemplate(typeof(Button)) { VisualTree=visual };
            System.Windows.Automation.AutomationProperties.SetName(input,System.Windows.Automation.AutomationProperties.GetName(target));
            input.Click+=delegate { if(!disposed && enabled() && owner.IsVisible && target.IsEnabled) click(); };
            Point local=target.TranslatePoint(new Point(0,0),owner);
            island=new Window {
                Title=owner.Title+" · "+System.Windows.Automation.AutomationProperties.GetName(target),Owner=owner,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                AllowsTransparency=true,Background=background,Content=input,ShowInTaskbar=false,ShowActivated=false,
                WindowStartupLocation=WindowStartupLocation.Manual,Left=owner.Left+local.X,Top=owner.Top+local.Y,
                Width=Math.Max(1,target.ActualWidth),Height=Math.Max(1,target.ActualHeight)
            };
            island.SourceInitialized+=delegate {
                IntPtr handle=new WindowInteropHelper(island).Handle;
                int current=GetWindowLong(handle,-20);
                // Preserve WPF's layered style, add NOACTIVATE/TOOLWINDOW, and
                // explicitly keep this small HWND eligible for hit testing.
                int next=(current|0x08000000|0x00000080)&~0x00000020;
                if(current!=next && SetWindowLong(handle,-20,next)==0) throw new Win32Exception(Marshal.GetLastWin32Error());
                source=HwndSource.FromHwnd(handle);
                if(source!=null) source.AddHook(Hook);
            };
        }

        IntPtr Hook(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
            if(message==0x0021) { handled=true;return new IntPtr(3); } // MA_NOACTIVATE: deliver the click without focusing.
            if(message==0x02E0) {
                hasBounds=false;
                owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(Sync));
            }
            return IntPtr.Zero;
        }
        void OnLayoutChanged(object sender,EventArgs e) { Sync(); }
        void OnSizeChanged(object sender,SizeChangedEventArgs e) { Sync(); }
        void OnStateChanged(object sender,DependencyPropertyChangedEventArgs e) { Sync(); }
        void OnClosed(object sender,EventArgs e) { Dispose(); }

        public void Dispose() {
            if(disposed) return;
            disposed=true;
            owner.LocationChanged-=OnLayoutChanged;
            owner.SizeChanged-=OnSizeChanged;
            owner.IsVisibleChanged-=OnStateChanged;
            owner.IsEnabledChanged-=OnStateChanged;
            owner.StateChanged-=OnLayoutChanged;
            owner.Closed-=OnClosed;
            target.LayoutUpdated-=OnLayoutChanged;
            target.IsVisibleChanged-=OnStateChanged;
            target.IsEnabledChanged-=OnStateChanged;
            if(source!=null) { if(!source.IsDisposed) source.RemoveHook(Hook);source=null; }
            if(island!=null) { island.Close();island=null; }
        }
    }
}
