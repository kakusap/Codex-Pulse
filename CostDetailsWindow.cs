using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CodexPulse {
    public static class CostDetailsWindow {
        static TextBlock Text(string value,double size) {return new TextBlock {Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap};}
        public static Window Create(Window owner,ThemePalette theme,LocalCostSummary costs,List<AccountUsage> accounts) {
            var window=new Window {Title="Codex Pulse · 本机参考费用",Owner=owner,ShowInTaskbar=false,Width=Math.Min(800,SystemParameters.WorkArea.Width-40),Height=Math.Min(740,SystemParameters.WorkArea.Height-50),ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=owner.FontFamily,Topmost=owner.Topmost};
            DialogTheme.Install(window,owner,theme);
            var grid=new Grid {Margin=new Thickness(22)};window.Content=grid;
            grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var heading=new StackPanel();grid.Children.Add(heading);heading.Children.Add(Text("本机参考费用 · "+costs.AmountText,23));
            var description=Text(costs.Description(),11);description.Margin=new Thickness(0,12,0,16);heading.Children.Add(description);
            var names=accounts.GroupBy(a=>a.AccountIdentity).ToDictionary(g=>g.Key,g=>g.First().Name);var rows=new StackPanel();
            if(costs.Slices.Count==0) rows.Children.Add(Text("没有可用于此范围的本机用量明细。未归属记录不会自动分配到当前账号。",12));
            foreach(var slice in costs.Slices.OrderByDescending(s=>s.Priced).ThenByDescending(s=>s.Cost).ThenBy(s=>s.Model)) {
                var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(2.3,GridUnitType.Star)});row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
                string accountName;if(!names.TryGetValue(slice.Identity,out accountName)) accountName=slice.Identity=="unassigned"?"未归属":"已移除账号";
                var model=new StackPanel();model.Children.Add(Text((string.IsNullOrEmpty(slice.Model)?"未记录模型":slice.Model)+" · "+accountName,12));
                var tokens=Text(slice.Records+" 条 · "+slice.Tokens.ToString("N0")+" Token · "+slice.FirstDay+" 至 "+slice.LastDay,10);tokens.Margin=new Thickness(0,5,8,0);model.Children.Add(tokens);row.Children.Add(model);
                var amount=Text(slice.Priced?"$"+slice.Cost.ToString("N6")+" USD":"未计价\n"+slice.Problem,11);amount.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(amount,1);row.Children.Add(amount);
                rows.Children.Add(new Border {Child=row,Background=ThemePalette.Alpha(theme.Accent,18),CornerRadius=new CornerRadius(7),Padding=new Thickness(10),Margin=new Thickness(0,0,0,7)});
            }
            var scroll=new ScrollViewer {Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,1);grid.Children.Add(scroll);
            var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};Grid.SetRow(footer,2);grid.Children.Add(footer);
            var prices=new Button {Content="查看内置价格表",Margin=new Thickness(0,0,8,0)};prices.Click+=delegate {PriceTableWindow.Create(window,theme).ShowDialog();};footer.Children.Add(prices);
            var close=new Button {Content="关闭"};close.Click+=delegate {window.Close();};footer.Children.Add(close);
            return window;
        }
    }
}
