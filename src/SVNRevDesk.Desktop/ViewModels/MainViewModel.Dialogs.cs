namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private int userInputDepth;
    private bool operationDeclined;

    // Keep the operation locked while a modal dialog is open, without suggesting work is running.
    public bool IsOperationProgressVisible => IsBusy && userInputDepth == 0 && !operationDeclined;

    public T WaitForUserInput<T>(Func<T> showDialog, Func<T, bool>? wasCancelled = null)
    {
        userInputDepth++;
        operationTime.Stop();
        progressTimer.Stop();
        Notify(nameof(IsOperationProgressVisible));
        Notify(nameof(ProgressElapsedText));
        try
        {
            var result = showDialog();
            if (IsBusy && wasCancelled?.Invoke(result) == true) operationDeclined = true;
            return result;
        }
        finally
        {
            userInputDepth--;
            if (userInputDepth == 0 && IsBusy && !operationDeclined && !disposed)
            {
                operationTime.Start();
                progressTimer.Start();
            }
            Notify(nameof(IsOperationProgressVisible));
        }
    }

    public void BeginProgressPhase(string title, string target)
    {
        ProgressTitle = title;
        SetProgressTarget(target);
        ProgressMessage = "正在准备…";
        transferredBytes = null;
        Notify(nameof(ProgressTransferText));
        operationTime.Restart();
        Notify(nameof(ProgressElapsedText));
    }
}
