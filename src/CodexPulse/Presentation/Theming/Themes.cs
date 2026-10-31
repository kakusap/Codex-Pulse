using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CodexPulse {
    public sealed class ThemePalette {
        public string Id,Name,Accent,Strong,Background,Dialog;
        public string[] Heat;
        public const string Muted="#CEC9C7",Text="#F2EFEC";
        public static readonly ThemePalette[] All={
            new ThemePalette { Id="pink",Name="莫兰迪粉",Accent="#D5AFBA",Strong="#B78D9D",Background="#372B33",Dialog="#32282E",Heat=new[]{"#22FFFFFF","#DDD0D5","#C5ACB7","#AB8597","#8C6278"} },
            new ThemePalette { Id="blue",Name="莫兰迪蓝",Accent="#AFBED2",Strong="#829BB8",Background="#273240",Dialog="#252F3D",Heat=new[]{"#22FFFFFF","#D0D8E1","#AFBFD0","#8A9FB9","#647F9F"} },
            new ThemePalette { Id="green",Name="莫兰迪绿",Accent="#B4C6B5",Strong="#8BA38F",Background="#2C362F",Dialog="#29332D",Heat=new[]{"#22FFFFFF","#D3DCD3","#B2C2B1","#92AA92","#708C74"} },
            new ThemePalette { Id="brown",Name="莫兰迪褐",Accent="#CDBBA9",Strong="#AA927A",Background="#382F2A",Dialog="#332C27",Heat=new[]{"#22FFFFFF","#E0D5CA","#C8B6A3","#AF957C","#8E725B"} }
        };
        public static ThemePalette Get(string id) { foreach(var theme in All) if(theme.Id==id) return theme;return All[2]; }
        public static SolidColorBrush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        public static SolidColorBrush Alpha(string hex,byte alpha) { Color c=(Color)ColorConverter.ConvertFromString(hex);c.A=alpha;return new SolidColorBrush(c); }
        public void Apply(Window window) {
            ApplyResources(window.Resources);
            window.Foreground=Brush(Text);
            DialogTheme.UpdateNative(window);
        }
        public void ApplyResources(ResourceDictionary resources) {
            resources["Theme.Id"]=Id;
            resources["Theme.Field"]=Brush(Background);
            resources["Theme.Accent"]=Brush(Accent);
            resources["Theme.Strong"]=Brush(Strong);
            resources["Theme.Text"]=Brush(Text);
            resources["Theme.Muted"]=Brush(Muted);
            resources["Theme.Soft"]=Alpha(Accent,36);
            resources["Theme.Card"]=Alpha(Accent,20);
            resources["Theme.Border"]=Alpha(Accent,55);
            resources["Theme.Edge"]=Alpha(Accent,120);
            resources["Theme.Button"]=Alpha(Accent,22);
            resources["Theme.Hover"]=Alpha(Accent,65);
            resources["Theme.Dialog"]=Brush(Dialog);
            resources["Theme.BackgroundColor"]=(Color)ColorConverter.ConvertFromString(Background);
        }
    }
}
