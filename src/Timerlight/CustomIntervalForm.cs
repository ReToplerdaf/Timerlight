namespace Timerlight;

/// <summary>Tiny prompt for entering an interval that is not one of the presets.</summary>
internal sealed class CustomIntervalForm : Form
{
    private readonly NumericUpDown _minutes;

    internal CustomIntervalForm(int currentMinutes)
    {
        Text = "Интервал Timerlight";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(296, 124);

        var label = new Label
        {
            Text = "Через сколько минут напоминать:",
            AutoSize = true,
            Location = new Point(14, 16),
        };

        _minutes = new NumericUpDown
        {
            Minimum = AppSettings.MinTargetMinutes,
            Maximum = AppSettings.MaxTargetMinutes,
            Value = Math.Clamp(currentMinutes, AppSettings.MinTargetMinutes, AppSettings.MaxTargetMinutes),
            Location = new Point(16, 44),
            Width = 104,
            TextAlign = HorizontalAlignment.Right,
        };

        var okButton = new Button
        {
            Text = "ОК",
            DialogResult = DialogResult.OK,
            Location = new Point(116, 84),
            Width = 80,
        };

        var cancelButton = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(202, 84),
            Width = 80,
        };

        Controls.AddRange([label, _minutes, okButton, cancelButton]);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    internal int Minutes => (int)_minutes.Value;
}
