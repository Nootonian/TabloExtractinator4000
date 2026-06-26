using System.ComponentModel;

namespace CommercialCutter;

public class BatchFileRow : INotifyPropertyChanged
{
    private string _status = "Pending";

    public string FullPath { get; }
    public string RelativePath { get; }

    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BatchFileRow(string fullPath, string relativePath)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
    }
}
