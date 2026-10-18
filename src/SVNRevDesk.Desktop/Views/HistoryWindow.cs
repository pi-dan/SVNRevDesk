using System.Windows;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Desktop.Views;

// The standalone browser and revision picker share paging, cancellation and filtering behavior.
public sealed class HistoryWindow : RevisionPickerWindow
{
    public HistoryWindow(Window owner, string scope,
        Func<long?, long?, CancellationToken, Task<HistoryPage>> loadPage, CancellationToken token)
        : base(owner, "提交历史", "查看目标的提交人、时间、修订号和完整说明。使用“加载更早”继续查询历史记录。",
            scope, "", false, loadPage, token, browseOnly: true) { }
}
