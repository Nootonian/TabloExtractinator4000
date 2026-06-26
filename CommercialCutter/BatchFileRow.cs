using System.ComponentModel;

namespace CommercialCutter;

public class BatchFileRow : INotifyPropertyChanged
{
    private string _status = "Pending";
    private bool _included = true;

    public string FullPath { get; }
    public string RelativePath { get; }

    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    // Unticked by the compatibility check (wrong resolution, or the logo crop never matches
    // anywhere in the recording) or manually by the user — Start Batch skips anything unticked.
    public bool Included
    {
        get => _included;
        set { _included = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Included))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BatchFileRow(string fullPath, string relativePath)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
    }
}
