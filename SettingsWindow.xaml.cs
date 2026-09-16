using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace LBA2LevelEditor;

public partial class SettingsWindow : Window
{
    public bool GameDirectoryChanged { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        GameDirectoryBox.Text = EditorSettings.Current.GameDirectory;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the LBA2 game folder",
            InitialDirectory = Directory.Exists(GameDirectoryBox.Text) ? GameDirectoryBox.Text : null,
        };
        if (dialog.ShowDialog(this) == true) GameDirectoryBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = GameDirectoryBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            ValidationText.Text = "Folder not found.";
            return;
        }
        if (!File.Exists(Path.Combine(path, "RESS.HQR")))
        {
            ValidationText.Text = "No RESS.HQR here — is this the right folder?";
            return;
        }
        var settings = EditorSettings.Current;
        GameDirectoryChanged = !string.Equals(settings.GameDirectory, path, StringComparison.OrdinalIgnoreCase);
        settings.GameDirectory = path;
        settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
