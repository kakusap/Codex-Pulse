using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace CodexPulse {
    public sealed class TokenHeatmap : Grid {
        readonly Canvas calendar=new Canvas();
        readonly TextBlock summary=new TextBlock { FontSize=9,Foreground=Paint("#D1DFEC") };
        readonly TextBlock note=new TextBlock { FontSize=9,Foreground=Paint("#B7C7DC"),TextWrapping=TextWrapping.Wrap,MaxHeight=30,TextTrimming=TextTrimming.CharacterEllipsis };
        readonly Dictionary<string,Border> cells=new Dictionary<string,Border>();
        readonly Dictionary<string,ToolTip> tips=new Dictionary<string,ToolTip>();
        readonly Dictionary<string,double[]> values=new Dictionary<string,double[]>();
        string[] colors=ThemePalette.Get("green").Heat;
        string hoverAccent=ThemePalette.Get("green").Accent;
        readonly List<Border> legendSwatches=new List<Border>();
        DateTime today,first,gridStart;
        string range="",error,dateBasis="北京时间";
        double max,undated,lastWidth;
        Border hoveredCell;
        ToolTip passiveTip;
        DateTime hoverStarted;
        bool passiveHover;
        const double weekday=24,gap=2,hoverPadding=4;
        readonly ScrollViewer scroll=new ScrollViewer { HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled };
        public TokenHeatmap() {
            RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            scroll.Content=calendar;Children.Add(scroll);
            var footer=new DockPanel { Margin=new Thickness(0,7,0,0) };SetRow(footer,1);Children.Add(footer);
            var legend=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
            legend.Children.Add(Label("少",9));
            foreach(string color in colors) { var swatch=new Border { Width=8,Height=8,CornerRadius=new CornerRadius(1.5),Background=Paint(color),Margin=new Thickness(3,0,0,0) };legendSwatches.Add(swatch);legend.Children.Add(swatch); }
            var more=Label("多",9);more.Margin=new Thickness(4,0,0,0);legend.Children.Add(more);
            DockPanel.SetDock(legend,Dock.Right);footer.Children.Add(legend);footer.Children.Add(summary);
            note.Margin=new Thickness(0,6,0,0);SetRow(note,2);Children.Add(note);
            SizeChanged += delegate { if(Math.Abs(ActualWidth-lastWidth)>0.5) LayoutCalendar(); };
        }
        public void SetTheme(ThemePalette theme) {
            colors=theme.Heat;hoverAccent=theme.Accent;
            for(int i=0;i<legendSwatches.Count;i++) legendSwatches[i].Background=Paint(colors[i]);
            foreach(var pair in cells) UpdateCell(pair.Key,pair.Value);
        }
        static SolidColorBrush Paint(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        static TextBlock Label(string text,double size) { return new TextBlock { Text=text,FontSize=size,Foreground=Paint("#C7D4E7"),VerticalAlignment=VerticalAlignment.Center }; }
        public static int Level(double used,double peak) { return used<=0||peak<=0?0:Math.Max(1,Math.Min(4,(int)Math.Ceiling(4*Math.Sqrt(used/peak)))); }
        static string Key(DateTime date) { return date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture); }
        public void Update(Snapshot snapshot,string selectedKey) {
            today=DataReader.ChinaToday;first=today.AddDays(-364);gridStart=first.AddDays(-(int)first.DayOfWeek);
            values.Clear();
            var known=new HashSet<string>(snapshot.Keys.Select(x=>x.Id));
            foreach(var item in snapshot.Daily.Where(x=>known.Contains(x.KeyId)&&(string.IsNullOrEmpty(selectedKey)||x.KeyId==selectedKey))) {
                double[] daily;if(!values.TryGetValue(item.Day,out daily)) { daily=new double[] {0,0,1};values[item.Day]=daily; }
                daily[0]+=item.Used;daily[1]+=item.Cost;if(!item.CostKnown) daily[2]=0;
            }
            var visible=values.Where(p=>string.CompareOrdinal(p.Key,Key(first))>=0 && string.CompareOrdinal(p.Key,Key(today))<=0).ToList();
            max=visible.Count==0?0:visible.Max(x=>x.Value[0]);
            undated=snapshot.Undated.Where(x=>known.Contains(x.KeyId)&&(string.IsNullOrEmpty(selectedKey)||x.KeyId==selectedKey)).Sum(x=>x.Used);
            error=snapshot.DataError??snapshot.HistoryError;dateBasis=snapshot.UsageDateBasis;
            if(range!=Key(today)) { range=Key(today);LayoutCalendar(); }
            foreach(var pair in cells) UpdateCell(pair.Key,pair.Value);
            string startKey=Key(first),endKey=Key(today);
            summary.Text=error!=null?"每日用量暂不可用":values.Where(p=>string.CompareOrdinal(p.Key,startKey)>=0 && string.CompareOrdinal(p.Key,endKey)<=0).Sum(p=>p.Value[0]).ToString("N0")+" Token / 近一年";
            summary.TextTrimming=TextTrimming.CharacterEllipsis;
            note.Text=error!=null?error:undated>0?"另有 "+undated.ToString("N0")+" Token 归档缺少日期，未计入此图":"悬停方块查看具体值 · 灰色表示无按日记录";
            if(!string.IsNullOrEmpty(snapshot.UsageNotice)) note.Text+="\n"+snapshot.UsageNotice;
            note.ToolTip=note.Text;
        }
        void LayoutCalendar() {
            if(today==default(DateTime)) return;
            ClearHover();
            lastWidth=ActualWidth;calendar.Children.Clear();cells.Clear();tips.Clear();
            int weeks=((today-gridStart).Days/7)+1;
            double available=Math.Max(0,ActualWidth-weekday-2*hoverPadding);
            double size=Math.Min(11,Math.Max(6,available/weeks-gap));
            double step=size+gap;
            calendar.Width=weekday+2*hoverPadding+weeks*step;calendar.Height=16+2*hoverPadding+7*step;
            for(int row=1;row<=5;row+=2) {
                var label=Label(row==1?"周一":row==3?"周三":"周五",8);
                Canvas.SetLeft(label,0);Canvas.SetTop(label,16+hoverPadding+row*step-1);calendar.Children.Add(label);
            }
            DateTime month=new DateTime(first.Year,first.Month,1);if(month<first) month=month.AddMonths(1);
            while(month<=today) {
                int column=(month-gridStart).Days/7;
                var label=Label(month.Month+"月",9);Canvas.SetLeft(label,weekday+hoverPadding+column*step);Canvas.SetTop(label,0);calendar.Children.Add(label);month=month.AddMonths(1);
            }
            for(int index=0;index<weeks*7;index++) {
                DateTime date=gridStart.AddDays(index);
                var cell=new Border { Width=size,Height=size,CornerRadius=new CornerRadius(1.5),Background=Paint(colors[0]),BorderBrush=Paint("#15FFFFFF"),BorderThickness=new Thickness(0.5),RenderTransformOrigin=new Point(0.5,0.5),RenderTransform=new ScaleTransform(1,1) };
                Canvas.SetLeft(cell,weekday+hoverPadding+(index/7)*step);Canvas.SetTop(cell,16+hoverPadding+(index%7)*step);calendar.Children.Add(cell);
                if(date<first || date>today) { cell.Opacity=0.2;cell.IsHitTestVisible=false;continue; }
                string day=Key(date);cells[day]=cell;
                var tip=new ToolTip { Placement=PlacementMode.Mouse,PlacementTarget=cell,MaxWidth=340,IsHitTestVisible=false };
                tip.Opened += delegate {
                    var source=PresentationSource.FromVisual(tip) as HwndSource;
                    if(source!=null) Backdrop.SetClickThrough(source.Handle,true);
                };
                tips[day]=tip;cell.ToolTip=tip;
                System.Windows.Automation.AutomationProperties.SetName(cell,day+" Token 用量");
                ToolTipService.SetInitialShowDelay(cell,80);ToolTipService.SetBetweenShowDelay(cell,0);ToolTipService.SetShowDuration(cell,60000);
                ToolTipService.SetIsEnabled(cell,!passiveHover);
                cell.MouseEnter += delegate(object sender,MouseEventArgs e) { if(!passiveHover) SetHoveredCell((Border)sender); };
                cell.MouseLeave += delegate(object sender,MouseEventArgs e) { if(!passiveHover && hoveredCell==sender) SetHoveredCell(null); };
                UpdateCell(day,cell);
            }
            // Compact windows keep a usable hover target and can scroll to older days.
            scroll.ScrollToRightEnd();
        }
        public void SetPassiveHover(bool enabled) {
            if(passiveHover==enabled) return;
            ClearHover();passiveHover=enabled;
            foreach(var cell in cells.Values) ToolTipService.SetIsEnabled(cell,!enabled);
        }
        public void UpdatePassiveHover(Point screenPoint) {
            if(!passiveHover || !IsVisible || PresentationSource.FromVisual(calendar)==null) { ClearHover();return; }
            Border cell=null;
            Point viewportPoint=scroll.PointFromScreen(screenPoint);
            if(new Rect(new Point(),scroll.RenderSize).Contains(viewportPoint)) {
                cell=calendar.InputHitTest(calendar.PointFromScreen(screenPoint)) as Border;
                if(cell!=null && !cells.Values.Contains(cell)) cell=null;
            }
            SetHoveredCell(cell);
            if(cell!=null && passiveTip!=null && !passiveTip.IsOpen && (DateTime.UtcNow-hoverStarted).TotalMilliseconds>=80) passiveTip.IsOpen=true;
        }
        public void ClearHover() { SetHoveredCell(null); }
        void SetHoveredCell(Border cell) {
            if(hoveredCell==cell) return;
            if(passiveTip!=null) { passiveTip.IsOpen=false;passiveTip=null; }
            var previous=hoveredCell;hoveredCell=cell;
            if(previous!=null) AnimateHover(previous,false);
            if(cell==null) return;
            AnimateHover(cell,true);
            if(passiveHover) {
                passiveTip=(ToolTip)cell.ToolTip;
                // WPF's last mouse position is stale while the HWND is transparent.
                // Anchor the hint to the actual date cell instead of PlacementMode.Mouse.
                passiveTip.Placement=PlacementMode.Bottom;passiveTip.PlacementTarget=cell;
                passiveTip.HorizontalOffset=6;passiveTip.VerticalOffset=5;
                hoverStarted=DateTime.UtcNow;
            } else {
                var tip=(ToolTip)cell.ToolTip;tip.Placement=PlacementMode.Mouse;tip.HorizontalOffset=0;tip.VerticalOffset=0;
            }
        }
        void AnimateHover(Border cell,bool hovered) {
            var scale=(ScaleTransform)cell.RenderTransform;
            if(hovered) {
                Panel.SetZIndex(cell,10);
                cell.BorderBrush=Brushes.White;cell.BorderThickness=new Thickness(0.8);
                cell.Effect=new DropShadowEffect { Color=(Color)ColorConverter.ConvertFromString(hoverAccent),BlurRadius=6,ShadowDepth=0,Opacity=0.6 };
            } else {
                cell.BorderBrush=Paint("#15FFFFFF");cell.BorderThickness=new Thickness(0.5);
            }
            // Render-only scaling keeps neighboring dates fixed and also works
            // while live values update. Replace animations on rapid re-entry.
            var animation=new DoubleAnimation(hovered?1.65:1,TimeSpan.FromMilliseconds(hovered?130:180)) {
                EasingFunction=new CubicEase { EasingMode=EasingMode.EaseOut }
            };
            if(!hovered) animation.Completed += delegate { if(cell!=hoveredCell) { Panel.SetZIndex(cell,0);cell.Effect=null; } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,animation,HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,animation,HandoffBehavior.SnapshotAndReplace);
        }
        void UpdateCell(string day,Border cell) {
            double[] daily;bool found=values.TryGetValue(day,out daily);
            double used=found?daily[0]:0,cost=found?daily[1]:0;
            cell.Background=Paint(colors[error==null?Level(used,max):0]);
            string exact=error!=null?"数据暂不可用":found?used.ToString("N0")+" Token":"无按日记录";
            tips[day].Content=day+"（"+dateBasis+"）\n"+exact+(error!=null || !found?"":daily[2]>0?"\n参考费用 $"+cost.ToString("N4"):"\n参考费用未提供");
        }
        public ToolTip ToolTipFor(DateTime date) { ToolTip tip;return tips.TryGetValue(Key(date),out tip)?tip:null; }
        public int DayCount { get { return cells.Count; } }
    }
}
