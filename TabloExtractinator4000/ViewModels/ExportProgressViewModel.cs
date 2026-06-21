using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.ViewModels;

public partial class ExportProgressViewModel : ObservableObject
{
    public ExportJob Job { get; }

    [ObservableProperty] private string  _status           = "Pending";
    [ObservableProperty] private double  _progressPct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CancelButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private string  _state            = "Pending";

    [ObservableProperty] private string  _bytesText        = "";
    [ObservableProperty] private string  _rateText         = "";
    [ObservableProperty] private string  _timeRemainingText = "";
    [ObservableProperty] private string  _elapsedText       = "";

    public string Title => Job.Recording.DisplayTitle;

    // Thin wrapper over Job.DeleteAfterExtraction — Job is the single source
    // of truth the orchestrator reads live, this just notifies the checkbox binding.
    public bool DeleteAfterExtraction
    {
        get => Job.DeleteAfterExtraction;
        set
        {
            if (Job.DeleteAfterExtraction == value) return;
            Job.DeleteAfterExtraction = value;
            OnPropertyChanged();
        }
    }

    // Pending tiles can be pulled out of the queue entirely; once a job has
    // started, the same button aborts the in-progress download instead.
    public bool   CanCancel        => State is "Pending" or "Discovering" or "Downloading" or "Verifying";
    public string CancelButtonLabel => State == "Pending" ? "✕ Cancel" : "⬛ Abort";

    public ExportProgressViewModel(ExportJob job)
    {
        Job = job;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => Job.Cts.Cancel();
}
