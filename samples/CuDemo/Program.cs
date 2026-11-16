namespace CuDemo;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new OrderForm());
    }
}

internal sealed class OrderForm : Form
{
    private readonly TextBox order = new() { Name = "OrderNumber", AccessibleName = "Order number", Width = 220 };
    private readonly Button query = new() { Name = "Query", Text = "Query", AutoSize = true };
    private readonly Label status = new() { Name = "QueryStatus", Text = "Idle - no query completed", AutoSize = true };
    private readonly Label result = new() { Name = "QueryResult", Text = "No result", AutoSize = true };
    private readonly Label canvasResult = new() { Name = "CanvasResult", Text = "Canvas selection: none", AutoSize = true };
    private int sequence;

    public OrderForm()
    {
        Name = "CuDemoMain";
        Text = "CU Demo Orders";
        ClientSize = new Size(760, 500);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true };
        layout.Controls.Add(new Label { Text = "Computer Use demo — local test data", AutoSize = true, Font = new Font(Font.FontFamily, 15, FontStyle.Bold) });
        layout.Controls.Add(new Label { Text = "Orders: A1001 (Ready), A1002 (Shipped). Results intentionally remain visible during loading.", AutoSize = true });
        var row = new FlowLayoutPanel { AutoSize = true };
        row.Controls.Add(order); row.Controls.Add(query); layout.Controls.Add(row);
        layout.Controls.Add(status); layout.Controls.Add(result);
        var popup = new Button { Name = "ShowDialog", Text = "Show dialog", AutoSize = true };
        popup.Click += (_, _) => MessageBox.Show(this, "This is a dynamic dialog. Close it and inspect the main window again.", "CU Demo Dialog", MessageBoxButtons.OK);
        layout.Controls.Add(popup);
        layout.Controls.Add(new Label { Text = "Visual fallback: the color targets below are painted, with no child buttons.", AutoSize = true });
        var canvas = new ColorCanvas { Name = "ColorCanvas", Size = new Size(650, 130) };
        canvas.Selected += name => canvasResult.Text = $"Canvas selection: {name}";
        layout.Controls.Add(canvas); layout.Controls.Add(canvasResult);
        Controls.Add(layout);
        AcceptButton = query;
        query.Click += async (_, _) =>
        {
            string requested = order.Text.Trim();
            int current = ++sequence;
            query.Enabled = false;
            status.Text = $"Loading query #{current}";
            await Task.Delay(750);
            if (IsDisposed) return;
            result.Text = requested switch
            {
                "A1001" => $"Order A1001 | Ready | Amount 128.00 | Query #{current}",
                "A1002" => $"Order A1002 | Shipped | Amount 256.50 | Query #{current}",
                _ => $"Order {requested} | Not found | Query #{current}"
            };
            status.Text = $"Completed query #{current}";
            query.Enabled = true;
        };
    }
}

internal sealed class ColorCanvas : Panel
{
    public event Action<string>? Selected;
    public ColorCanvas() { DoubleBuffered = true; BackColor = Color.WhiteSmoke; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.FillRectangle(Brushes.RoyalBlue, 20, 20, Width / 2 - 30, Height - 40);
        e.Graphics.FillRectangle(Brushes.DarkOrange, Width / 2 + 10, 20, Width / 2 - 30, Height - 40);
        e.Graphics.DrawString("Blue", Font, Brushes.White, 45, 50);
        e.Graphics.DrawString("Orange", Font, Brushes.Black, Width / 2 + 35, 50);
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Y >= 20 && e.Y < Height - 20 && e.X >= 20 && e.X < Width - 20)
            Selected?.Invoke(e.X < Width / 2 ? "Blue" : "Orange");
    }
}
