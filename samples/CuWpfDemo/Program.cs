using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CuWpfDemo;

internal static class Program
{
    [STAThread]
    private static void Main() => new Application().Run(new FixtureWindow());
}

internal sealed class FixtureWindow : Window
{
    private static T Id<T>(T control, string id) where T : FrameworkElement
    {
        AutomationProperties.SetAutomationId(control, id);
        return control;
    }

    public FixtureWindow()
    {
        Title = "CU WPF Controls"; Width = 820; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(20) };
        Content = root;
        root.Children.Add(new TextBlock { Text = "Windows Computer Use — WPF fixture", FontSize = 22, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(Id(new TextBox { Text = "Initial text", Margin = new Thickness(0, 0, 0, 8) }, "EditableText"));
        root.Children.Add(Id(new TextBox { Text = "Read only evidence", IsReadOnly = true }, "ReadOnlyText"));
        root.Children.Add(Id(new CheckBox { Content = "Enable option", Margin = new Thickness(0, 8, 0, 8) }, "OptionToggle"));
        var grid = new Grid { Height = 150 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(grid);
        var list = Id(new ListBox { SelectionMode = SelectionMode.Multiple, Margin = new Thickness(0, 0, 12, 0) }, "ChoiceList");
        VirtualizingStackPanel.SetIsVirtualizing(list, false);
        for (int i = 1; i <= 30; i++) list.Items.Add(Id(new ListBoxItem { Content = $"Choice {i:00}" }, $"Choice{i:00}"));
        grid.Children.Add(list);
        var tree = Id(new TreeView(), "FolderTree"); Grid.SetColumn(tree, 1); grid.Children.Add(tree);
        var parent = Id(new TreeViewItem { Header = "Orders folder" }, "OrdersFolder");
        parent.Items.Add(Id(new TreeViewItem { Header = "Pending orders" }, "PendingOrders")); tree.Items.Add(parent);
        root.Children.Add(new TextBlock { Text = "Range value (0..100)", Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(Id(new Slider { Minimum = 0, Maximum = 100, Value = 25, Height = 28 }, "AmountSlider"));
        var dynamic = new StackPanel { Orientation = Orientation.Horizontal, Height = 35 };
        root.Children.Add(dynamic);
        var delayed = Id(new Button { Content = "Delayed status", Width = 120 }, "DelayedButton"); dynamic.Children.Add(delayed);
        var status = Id(new TextBlock { Text = "Status: idle", Margin = new Thickness(12, 6, 0, 0) }, "DelayedStatus"); dynamic.Children.Add(status);
        delayed.Click += async (_, _) =>
        {
            delayed.IsEnabled = false; status.Text = "Status: loading";
            await Task.Delay(600); status.Text = "Status: complete"; delayed.IsEnabled = true;
        };
        root.Children.Add(new TextBlock { Text = "Native input area: move, double click, right click, wheel, drag", Margin = new Thickness(0, 8, 0, 6) });
        var surface = new Border { Height = 95, Background = Brushes.MediumSeaGreen, Focusable = true };
        root.Children.Add(surface);
        var pointer = Id(new TextBlock { Text = "Move=0 Double=0 Right=0 Wheel=0 Drag=0", Margin = new Thickness(0, 8, 0, 0) }, "PointerEvidence");
        root.Children.Add(pointer);
        int moves = 0, doubles = 0, rights = 0, wheel = 0, drags = 0;
        Point? down = null;
        void Update() => pointer.Text = $"Move={moves} Double={doubles} Right={rights} Wheel={wheel} Drag={drags}";
        surface.MouseMove += (_, _) => { moves++; Update(); };
        surface.MouseDown += (_, e) =>
        {
            surface.Focus();
            if (e.ChangedButton == MouseButton.Left) { down = e.GetPosition(surface); surface.CaptureMouse(); if (e.ClickCount == 2) doubles++; }
            if (e.ChangedButton == MouseButton.Right) rights++;
            Update();
        };
        surface.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && down is { } start)
            {
                if ((e.GetPosition(surface) - start).Length > 30) drags++;
                down = null; surface.ReleaseMouseCapture(); Update();
            }
        };
        surface.MouseWheel += (_, e) => { wheel += e.Delta; Update(); e.Handled = true; };
    }
}
