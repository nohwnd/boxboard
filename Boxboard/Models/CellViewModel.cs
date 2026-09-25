using System.ComponentModel;
using System.Windows.Media;

namespace Boxboard.Models;

public sealed class CellViewModel : INotifyPropertyChanged
{
    public required SlotAssignment Slot { get; init; }
    public Guid DesktopId { get; init; }
    public required string MachineName { get; init; }
    public required string MachineDetails { get; init; }
    public bool IsAssigned => Slot.MachineId is not null;
    private bool _isDragTarget;
    public bool IsDragTarget
    {
        get => _isDragTarget;
        set { if (_isDragTarget != value) { _isDragTarget = value; PropertyChanged?.Invoke(this, new(nameof(IsDragTarget))); } }
    }
    public bool CanBind { get; init; }
    private bool _canConnect;
    public bool CanConnect
    {
        get => _canConnect;
        set { if (_canConnect != value) { _canConnect = value; PropertyChanged?.Invoke(this, new(nameof(CanConnect))); } }
    }
    public string Name => Slot.Name;
    public string ConnectName => $"{Slot.Name} Connect / Reconnect";
    public string BindName => $"{Slot.Name} bind existing window";
    private string _stateText = "Empty";
    public string StateText
    {
        get => _stateText;
        set { if (_stateText != value) { _stateText = value; PropertyChanged?.Invoke(this, new(nameof(StateText))); } }
    }
    private Brush _stateBrush = Brushes.Gray;
    public Brush StateBrush
    {
        get => _stateBrush;
        set { if (_stateBrush != value) { _stateBrush = value; PropertyChanged?.Invoke(this, new(nameof(StateBrush))); } }
    }
    public SessionWindow? SelectedWindow { get; set; }
    private string _status = "";
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    }
    private IReadOnlyList<SessionWindow> _windows = [];
    public IReadOnlyList<SessionWindow> Windows
    {
        get => _windows;
        set
        {
            if (_windows.SequenceEqual(value))
                return;
            var selected = SelectedWindow?.Identity;
            var replacement = value.SingleOrDefault(w => w.Identity == selected) ?? (value.Count == 1 ? value[0] : null);
            _windows = value;
            PropertyChanged?.Invoke(this, new(nameof(Windows)));
            SelectedWindow = replacement;
            PropertyChanged?.Invoke(this, new(nameof(SelectedWindow)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
