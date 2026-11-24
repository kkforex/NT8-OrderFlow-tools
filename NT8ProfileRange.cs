// =====================================================================================
//  NT8ProfileRange.cs  —  NinjaTrader 8 drawing tool
//  A plain rectangle (inherits NT's built-in Rectangle, so move/resize/select/copy and
//  templates all work natively). It computes nothing: the NT8Profile indicator finds
//  every NT8ProfileRange on the chart and renders a profile inside it.
//  Appears in the chart toolbar's Drawing Tools menu as "NT8 Profile Range".
// =====================================================================================
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Gui;
#endregion

public enum NT8ProfileRangeType { UseIndicator, Volume, AskBid, Delta, DeltaAndTotal }

namespace NinjaTrader.NinjaScript.DrawingTools
{
    public class NT8ProfileRange : NinjaTrader.NinjaScript.DrawingTools.Rectangle
    {
        public override object Icon
        {
            get
            {
                Grid grid = new Grid { Width = 16, Height = 16, UseLayoutRounding = true, SnapsToDevicePixels = true };
                Brush stroke = Application.Current.TryFindResource("FontActionBrush") as Brush ?? Brushes.Gray;
                System.Windows.Shapes.Path path = new System.Windows.Shapes.Path
                {
                    Data            = Geometry.Parse("M1,1 H15 V15 H1 Z M3,4 H8 M3,7 H13 M3,10 H10 M3,13 H6"),
                    Stroke          = stroke,
                    StrokeThickness = 1.2
                };
                grid.Children.Add(path);
                return grid;
            }
        }

        protected override void OnStateChange()
        {
            base.OnStateChange();

            if (State == State.SetDefaults)
            {
                Name             = "NT8 Profile Range";
                Description      = "Custom-range profile. Requires the NT8Profile indicator on the same chart.";
                ProfileType      = NT8ProfileRangeType.UseIndicator;
                ClipToPriceRange = true;
                AreaBrush        = Brushes.SteelBlue;
                AreaOpacity      = 6;
                OutlineStroke    = new Stroke(Brushes.SteelBlue, DashStyleHelper.Dash, 1);
            }
        }

        [Display(Name = "Profile type", Description = "Override the indicator's profile type for this range.", Order = 1, GroupName = "NT8 Profile")]
        public NT8ProfileRangeType ProfileType { get; set; }

        [Display(Name = "Clip to rectangle price range", Order = 2, GroupName = "NT8 Profile")]
        public bool ClipToPriceRange { get; set; }
    }
}
