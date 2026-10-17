namespace SVNRevDesk.Core.Svn;

public enum SvnProgressStage
{
    Started,
    Output,
    Transfer,
    Completed
}

// Update notifications describe completed items; svn.exe does not expose network byte totals.
public sealed record SvnProgress(string Command, SvnProgressStage Stage, string Message = "", long BytesTransferred = 0);
