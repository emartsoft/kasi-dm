using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Kasi.App.Views;

/// <summary>
/// The tray icon's panel (KASI-UI-004, UIR-7): total speed with a chart, Pause all and Resume all, the speed limits, Open Kasi and Quit. It sits
/// above the corner of the work area the icon is in, takes focus so the keyboard works, and closes on Esc, on a button or when it loses focus.
/// </summary>
internal sealed partial class TrayFlyoutWindow : Window
{
    private const int EdgeGap = 12;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PlaceAtCorner();
        Deactivated += (_, _) => Close();
        // any button of the panel (Pause all, Open Kasi, Quit) is done once pressed; the limit list closes it when a choice is made
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button)
            {
                Close();
            }
        });
        Presets.SelectionChanged += (_, e) =>
        {
            if (e.RemovedItems.Count > 0)
            {
                Close();
            }
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void PlaceAtCorner()
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var size = PixelSize.FromSize(new Size(Width, Bounds.Height), scaling);
        Position = new PixelPoint(area.Right - size.Width - (int)(EdgeGap * scaling), area.Bottom - size.Height - (int)(EdgeGap * scaling));
        Activate();
    }
}
