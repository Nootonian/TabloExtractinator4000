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
    // Queued/Pending/terminal → dismiss the tile directly. A Pending job is still
    //   sitting unread in the orchestrator's channel — nothing is watching its
    //   token until a worker frees up, so waiting for that report would leave
    //   the tile stuck. Cancelling the token AND removing the tile immediately
    //   means if a worker does dequeue it moments later, it'll see the
    //   cancellation and skip it quietly with no tile left to confuse.
    // active (Discovering/Downloading/Verifying) → abort and wait for the
    //   orchestrator to report back before the tile can be dismissed.
    public bool   RemovesDirectly  => State is not ("Discovering" or "Downloading" or "Verifying");
    public string CancelButtonLabel => State switch
    {
        "Queued" or "Pending" => "✕ Cancel",
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
        Job.Cts.Cancel();   // harmless no-op if never submitted or already finished
        if (RemovesDirectly) RemoveRequested?.Invoke(this);
    }
}
