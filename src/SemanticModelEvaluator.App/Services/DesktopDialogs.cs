using Microsoft.Win32;
using System.Windows;

namespace SemanticModelEvaluator.App.Services;

public interface IUserDialogs
{
    string? Open(string filter);
    string? Save(string filter, string extension);
    string? Folder();
    bool Confirm(string message);
}

public sealed class DesktopDialogs : IUserDialogs
{
    public string? Open(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? Save(string filter, string extension)
    {
        var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, AddExtension = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? Folder()
    {
        var dialog = new OpenFolderDialog();
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool Confirm(string message) => MessageBox.Show(message, Core.AppPaths.AppName,
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
