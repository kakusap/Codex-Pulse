using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace CodexPulse {
    public static class PriceTableWindow {
        static TextBlock Label(string text,double size,ThemePalette theme) {return new TextBlock {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=ThemePalette.Brush(ThemePalette.Text)};}
        static Grid Row(string[] cells,ThemePalette theme,bool heading) {
            var row=new Grid {Margin=new Thickness(0,0,0,heading?10:0)};
            row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(2.8,GridUnitType.Star)});
            for(int i=1;i<5;i++) row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
            for(int i=0;i<cells.Length;i++) {
                var text=Label(cells[i],11,theme);text.Margin=new Thickness(0,heading?0:7,8,heading?0:7);if(heading) text.FontWeight=FontWeights.SemiBold;
                Grid.SetColumn(text,i);row.Children.Add(text);
            }
            return row;
        }
        public static Window Create(Window owner,ThemePalette theme) {
            var book=PriceBook.Builtin();
            var window=new Window {Title="Codex Pulse · 内置模型价格表",Owner=owner,ShowInTaskbar=false,Width=Math.Min(780,SystemParameters.WorkArea.Width-40),Height=Math.Min(700,SystemParameters.WorkArea.Height-50),ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=owner.FontFamily,Topmost=owner.Topmost,Background=ThemePalette.Brush(theme.Dialog)};
            DialogTheme.Install(window,owner,theme);
            var grid=new Grid {Margin=new Thickness(22)};window.Content=grid;
            grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var title=new StackPanel();grid.Children.Add(title);title.Children.Add(Label("内置 OpenAI 模型参考价格",17,theme));
            var version=Label(book.CheckedOn+" 核对 · "+book.Models.Count+" 个模型 · 美元 / 百万 Token · Standard",11,theme);version.Margin=new Thickness(0,9,0,18);version.ToolTip=book.Version;title.Children.Add(version);
            var header=Row(new[] {"模型","输入","缓存读取","缓存写入¹","输出"},theme,true);Grid.SetRow(header,1);grid.Children.Add(header);
            var rows=new StackPanel();
            foreach(var price in book.Models) {
                var row=Row(new[] {price.Model,PriceBook.Rate(price.InputPerMillion),PriceBook.Rate(price.CachedInputPerMillion),PriceBook.Rate(price.CacheWritePerMillion),PriceBook.Rate(price.OutputPerMillion)},theme,false);
                row.ToolTip="价格来源："+(price.Model=="chat-latest" || price.Model=="gpt-5.3-codex"?book.Source:"https://developers.openai.com/api/docs/models/"+price.Model)+(price.LongContextThreshold>0?"\n输入超过 "+price.LongContextThreshold.ToString("N0")+" Token 时，输入与缓存 ×"+price.LongInputMultiplier+"，输出 ×"+price.LongOutputMultiplier:"");
                rows.Children.Add(new Border {Child=row,BorderThickness=new Thickness(0,0,0,0.5),BorderBrush=ThemePalette.Alpha(theme.Accent,30)});
            }
            var scroll=new ScrollViewer {Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,2);grid.Children.Add(scroll);
            var footer=new StackPanel {Margin=new Thickness(0,14,0,0)};Grid.SetRow(footer,3);grid.Children.Add(footer);
            footer.Children.Add(Label("¹ 缓存写入按默认 30 分钟单价。长上下文按模型规则调整；未含加速、区域或工具费用。",10,theme));
            footer.Children.Add(Label("用于 API 等值估算，不是 ChatGPT 订阅账单。旧模型仅用于历史估算，未知模型不套价。内置表随程序版本更新。",10,theme));
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};footer.Children.Add(buttons);
            var source=new Button {Content="官方价格来源",Margin=new Thickness(0,0,8,0)};source.Click+=delegate {try {Process.Start(new ProcessStartInfo(book.Source) {UseShellExecute=true});}catch {MessageBox.Show(window,"无法打开浏览器，请访问 "+book.Source);}};buttons.Children.Add(source);
            var close=new Button {Content="关闭"};close.Click+=delegate {window.Close();};buttons.Children.Add(close);
            return window;
        }
    }
}
