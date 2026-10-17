using System.Windows;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private double previewWidth = 320;

    private void UpdatePreviewLayout()
    {
        if (!model.Preview.IsEnabled && PreviewColumn.Width.Value > 0)
            previewWidth = PreviewColumn.ActualWidth >= 240 ? PreviewColumn.ActualWidth : PreviewColumn.Width.Value;
        PreviewColumn.MinWidth = model.Preview.IsEnabled ? 240 : 0;
        PreviewColumn.Width = new GridLength(model.Preview.IsEnabled ? Math.Max(240, previewWidth) : 0);
        PreviewSplitterColumn.Width = new GridLength(model.Preview.IsEnabled ? 10 : 0);
    }
}
