using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;

namespace GitWork.App;

internal sealed class PreparationRunRow(int number, string name) : INotifyPropertyChanged
{
    private readonly Stopwatch _timer = new();

    public int Number { get; } = number;

    public string Name { get; } = name;

    public string Status { get; private set; } = L10n.T("Ожидает");

    public string Duration { get; private set; } = "";

    public Brush StatusBrush { get; private set; } = Brushes.Gray;

    public bool Succeeded { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start(Brush brush)
    {
        this._timer.Restart();
        this.Update(L10n.T("Выполняется…"), brush);
    }

    public void Finish(string status, Brush brush, bool succeeded = false)
    {
        this._timer.Stop();
        this.Duration = this._timer.Elapsed.ToString(@"mm\:ss");
        this.Succeeded = succeeded;
        this.Update(status, brush);
    }

    private void Update(string status, Brush brush)
    {
        this.Status = status;
        this.StatusBrush = brush;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
