using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.ViewModels;

public partial class ExportProgressViewModel : ObservableObject
{
    public ExportJob Job { get; }

    [ObservableProperty] private string  _status           = "Queued";
    [ObservableProperty] private double  _progressPct;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemovesDirectly))]
    [NotifyPropertyChangedFor(nameof(CancelButtonLabel))]
    private string  _state            = "Queued";

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

    // The button is always present, but its action changes with state:
    // Queued/terminal → dismiss the tile directly (nothing submitted/active to cancel)
    // Pending         → pull it out of the orchestrator's queue
    // active          → abort the in-progress job
    public bool   RemovesDirectly  => State is "Queued" or "Cancelled" or "Failed" or "Verified" or "DeletedFromTablo";
    public string CancelButtonLabel => State switch
    {
        "Queued"  => "✕ Remove",
        "Pending" => "✕ Cancel",
        "Discovering" or "Downloading" or "Verifying" => "⬛ Abort",
        _ => "✕ Remove"
    };

    // Fired when the user dismisses a tile that has nothing left to cancel —
    // the owner must remove it from the visible list itself.
    public event Action<ExportProgressViewModel>? RemoveRequested;

    public ExportProgressViewModel(ExportJob job)
    {
        Job = job;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (RemovesDirectly) RemoveRequested?.Invoke(this);
        else                 Job.Cts.Cancel();
    }
}
