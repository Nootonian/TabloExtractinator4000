using CommunityToolkit.Mvvm.ComponentModel;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.ViewModels;

public partial class ExportProgressViewModel : ObservableObject
{
    public ExportJob Job { get; }

    [ObservableProperty] private string  _status           = "Pending";
    [ObservableProperty] private double  _progressPct;
    [ObservableProperty] private string  _state            = "Pending";
    [ObservableProperty] private string  _bytesText        = "";
    [ObservableProperty] private string  _rateText         = "";
    [ObservableProperty] private string  _timeRemainingText = "";
    [ObservableProperty] private string  _elapsedText       = "";

    public string Title => Job.Recording.DisplayTitle;

    public ExportProgressViewModel(ExportJob job)
    {
        Job = job;
    }
}
