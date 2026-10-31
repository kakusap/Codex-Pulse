using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexPulse;

class DialogThemeVerification {
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out Rect rect);
    static List<string> report=new List<string>();
    class Row { public string Id { get; set; } public string Display { get; set; } }
    static void Check(bool value,string message) { if(!value) throw new Exception(message);report.Add("PASS "+message); }
    static IEnumerable<T> Controls<T>(DependencyObject parent) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var child=VisualTreeHelper.GetChild(parent,i);if(child is T) yield return (T)child;foreach(var value in Controls<T>(child)) yield return value; }
    }
    static Color ColorOf(Brush brush) { return ((SolidColorBrush)brush).Color; }
    static int Rgb(string color) { Color value=(Color)ColorConverter.ConvertFromString(color);return value.R|(value.G<<8)|(value.B<<16); }
    static void Pump(Window window) { window.UpdateLayout();window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(()=>{})); }
    static void Native(Window window,ThemePalette theme) {
        Rect bounds;GetWindowRect(new WindowInteropHelper(window).Handle,out bounds);
        Point client=window.PointToScreen(new Point(0,0));int x=bounds.Left+(bounds.Right-bounds.Left)*3/4,y=(bounds.Top+(int)client.Y)/2;
        // Caption colors are set-only DWM attributes. Verify pixels from the actual
        // native frame instead of RenderTargetBitmap, which excludes the titlebar.
        var expected=ColorOf(ThemePalette.Brush(theme.Dialog));bool matched=false;
        for(int attempt=0;attempt<10;attempt++) {
            Pump(window);DwmFlush();
            using(var bitmap=new System.Drawing.Bitmap(1,1)) {
                using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(x,y,0,0,bitmap.Size);
                var pixel=bitmap.GetPixel(0,0);matched=Math.Abs(pixel.R-expected.R)<3 && Math.Abs(pixel.G-expected.G)<3 && Math.Abs(pixel.B-expected.B)<3;
            }
            if(matched) break;System.Threading.Thread.Sleep(50);
        }
        Check(matched,theme.Id+" actual native caption pixels match selected theme");
    }
    static void Capture(Window window,string file) {
        Rect bounds;Check(GetWindowRect(new WindowInteropHelper(window).Handle,out bounds),"read full native window rectangle");
        using(var bitmap=new System.Drawing.Bitmap(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top)) {
            using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,bitmap.Size);
            bitmap.Save(file,System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    [STAThread] static int Main(string[] args) {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        Window owner=null,dialog=null;
        try {
            owner=new Window { Width=560,Height=56,Left=80,Top=60,ShowInTaskbar=false,ShowActivated=false,Topmost=true };
            using(var stream=typeof(Dashboard).Assembly.GetManifestResourceStream("MainWindow.xaml")) owner.Resources=((Window)System.Windows.Markup.XamlReader.Load(stream)).Resources;
            ThemePalette.Get("pink").Apply(owner);owner.Show();
            var settings=new Settings { PulseDb=Path.Combine(output,"fixture-pulse.db"),Theme="pink" };
            dialog=new IndependentAccountWindow(owner,settings,proposed=>{}).Window;dialog.Topmost=true;dialog.Show();Pump(dialog);
            var list=Controls<ListBox>(dialog).Single();list.ItemsSource=new[] { new Row { Id="a",Display="示例账号 A · active · 刚刚更新" },new Row { Id="b",Display="示例账号 B · active · 刚刚更新" } };list.SelectedIndex=0;Pump(dialog);
            var field=Controls<TextBox>(dialog).Single();field.Text="C:\\Example\\Codex";field.Select(0,4);
            Check(field.SelectionLength==4 && field.Template.FindName("PART_ContentHost",field) is ScrollViewer,"themed text field retains editable content host and selection");
            Check(Controls<Button>(dialog).Count(b=>!string.IsNullOrEmpty(Convert.ToString(b.ToolTip)))==8,"all eight account actions explain their effects on hover");
            foreach(var theme in ThemePalette.All) {
                theme.Apply(owner);theme.Apply(dialog);dialog.Activate();Pump(dialog);Capture(dialog,Path.Combine(output,"accounts-"+theme.Id+"-native.png"));Native(dialog,theme);
                Check(ColorOf(dialog.Background)==ColorOf(ThemePalette.Brush(theme.Dialog)),theme.Id+" content background updates live");
                Check(ColorOf(field.Background)==ColorOf(ThemePalette.Brush(theme.Background)) && ColorOf(list.Background)==ColorOf(field.Background),theme.Id+" text input and account list use theme field color");
                var item=(ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);item.ApplyTemplate();var chrome=(Border)item.Template.FindName("RowChrome",item);
                Check(ColorOf(chrome.Background)==ColorOf(ThemePalette.Alpha(theme.Accent,36)),theme.Id+" selected account uses theme highlight");
                owner.Activate();Pump(dialog);Native(dialog,theme);dialog.Activate();Pump(dialog);
                Capture(dialog,Path.Combine(output,"accounts-"+theme.Id+"-native.png"));
            }
            dialog.Close();dialog=null;
            File.WriteAllLines(Path.Combine(output,"report.txt"),report);Console.WriteLine(string.Join("\n",report));return 0;
        } catch(Exception e) { report.Add("FAIL "+e);File.WriteAllLines(Path.Combine(output,"report.txt"),report);Console.WriteLine(e);return 1; }
        finally { if(dialog!=null) dialog.Close();if(owner!=null) owner.Close(); }
    }
}
