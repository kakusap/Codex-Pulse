using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexPulse {
    public sealed class QuotaRing : FrameworkElement {
        public double? Remaining { get; private set; }
        Brush accent=ThemePalette.Brush("#D5AFBA");
        public QuotaRing() { Width=36;Height=36; }
        public void Update(double? used,string color) {
            Remaining=used.HasValue && !double.IsNaN(used.Value) && !double.IsInfinity(used.Value) ? Math.Max(0,Math.Min(100,100-used.Value)) : (double?)null;
            accent=ThemePalette.Brush(Remaining<=15?"#EDAA7C":Remaining<=35?"#E4C985":color);
            System.Windows.Automation.AutomationProperties.SetName(this,Remaining.HasValue?"剩余 "+Remaining.Value.ToString("0.#")+"%":"暂无额度快照");
            InvalidateVisual();
        }
        protected override void OnRender(DrawingContext d) {
            double size=Math.Min(ActualWidth,ActualHeight),radius=Math.Max(1,(size-4)/2);
            var center=new Point(ActualWidth/2,ActualHeight/2);
            var track=new SolidColorBrush(Color.FromArgb(40,213,175,186));
            d.DrawEllipse(null,new Pen(track,3),center,radius,radius);
            if(Remaining>=99.999) d.DrawEllipse(null,new Pen(accent,3),center,radius,radius);
            else if(Remaining>0) {
                double angle=2*Math.PI*Remaining.Value/100;
                var arc=new StreamGeometry();using(var geometry=arc.Open()) {
                    geometry.BeginFigure(new Point(center.X,center.Y-radius),false,false);
                    geometry.ArcTo(new Point(center.X+radius*Math.Sin(angle),center.Y-radius*Math.Cos(angle)),new Size(radius,radius),0,Remaining>50,SweepDirection.Clockwise,true,false);
                }
                var pen=new Pen(accent,3) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round };
                d.DrawGeometry(null,pen,arc);
            }
            string value=Remaining.HasValue?Remaining.Value.ToString("0",CultureInfo.InvariantCulture)+"%":"—";
            var text=new FormattedText(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal),12,ThemePalette.Brush(ThemePalette.Text),VisualTreeHelper.GetDpi(this).PixelsPerDip);
            d.DrawText(text,new Point(center.X-text.Width/2,center.Y-text.Height/2));
        }
    }
}
