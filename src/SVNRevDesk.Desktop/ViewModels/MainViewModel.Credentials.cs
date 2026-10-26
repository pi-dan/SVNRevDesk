using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public void SetLogin(string username, string password, bool remember)
    {
        var target = Session?.Url ?? Address;
        var store = new SavedCredentials(Settings.StorageDirectory);
        if (remember && password.Length > 0) store.Save(target, new(username, password));
        else store.Forget(target);
        Runner.Username = username;
        Runner.Password = password.Length == 0 ? null : password;
    }

    private void RestoreLogin(string target)
    {
        if (Runner.Password != null) return;
        var login = new SavedCredentials(Settings.StorageDirectory).Load(target);
        if (login == null) return;
        Runner.Username = login.Username;
        Runner.Password = login.Password;
    }
}
