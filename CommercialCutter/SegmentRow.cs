using System.ComponentModel;

namespace CommercialCutter;

public class SegmentRow : INotifyPropertyChanged
{
    private bool _isCommercial;

    public double StartSeconds { get; }
    public double EndSeconds   { get; }

    public string Start    => System.TimeSpan.FromSeconds(StartSeconds).ToString(@"hh\:mm\:ss");
    public string End      => System.TimeSpan.FromSeconds(EndSeconds).ToString(@"hh\:mm\:ss");
    public string Duration => System.TimeSpan.FromSeconds(EndSeconds - StartSeconds).ToString(@"hh\:mm\:ss");

    public bool IsCommercial
    {
        get => _isCommercial;
        set { _isCommercial = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCommercial))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SegmentRow(Segment s)
    {
        StartSeconds = s.StartSeconds;
        EndSeconds   = s.EndSeconds;
        _isCommercial = s.IsCommercial;
    }

    public Segment ToSegment() => new(StartSeconds, EndSeconds, IsCommercial);
}
