using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexPulse {
    public static class DialogTheme {
        static readonly DependencyProperty ManagedProperty=DependencyProperty.RegisterAttached("Managed",typeof(bool),typeof(DialogTheme),new PropertyMetadata(false));
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
        public static bool IsManaged(Window window) { return (bool)window.GetValue(ManagedProperty); }
        public static void Install(Window window,Window owner,ThemePalette theme) {
            if(!IsManaged(window)) {
                window.SetValue(ManagedProperty,true);
                foreach(Type type in new[] { typeof(Button),typeof(ScrollBar),typeof(ToolTip) }) {
                    object style=owner.TryFindResource(type);if(style!=null) window.Resources[type]=style;
                }
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("DialogTheme.xaml")) window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
                window.SetResourceReference(Control.BackgroundProperty,"Theme.Dialog");
                window.SetResourceReference(Control.ForegroundProperty,"Theme.Text");
                window.SourceInitialized+=delegate { UpdateNative(window); };
                window.Activated+=delegate { UpdateNative(window); };
                window.Deactivated+=delegate { UpdateNative(window); };
            }
            string ownerTheme=owner.TryFindResource("Theme.Id") as string;
            (string.IsNullOrEmpty(ownerTheme)?theme:ThemePalette.Get(ownerTheme)).Apply(window);
        }
        static int Rgb(Color color) { return color.R|(color.G<<8)|(color.B<<16); }
        public static void UpdateNative(Window window) {
            if(!IsManaged(window)) return;
            IntPtr handle=new WindowInteropHelper(window).Handle;if(handle==IntPtr.Zero) return;
            var caption=window.TryFindResource("Theme.Dialog") as SolidColorBrush;
            var text=window.TryFindResource("Theme.Text") as SolidColorBrush;
            int dark=1;DwmSetWindowAttribute(handle,20,ref dark,4);
            if(caption!=null) { int value=Rgb(caption.Color);DwmSetWindowAttribute(handle,35,ref value,4); }
            if(text!=null) { int value=Rgb(text.Color);DwmSetWindowAttribute(handle,36,ref value,4); }
            int noBorder=unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: retain the native rounded frame without an outline.
            DwmSetWindowAttribute(handle,34,ref noBorder,4);
            int rounded=2;DwmSetWindowAttribute(handle,33,ref rounded,4);
        }
    }
}
