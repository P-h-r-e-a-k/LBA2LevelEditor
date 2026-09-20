using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace LBA2LevelEditor;

public partial class SettingsWindow : Window
{
    public bool GameDirectoryChanged { get; private set; }
    public bool Lba1DirectoryChanged { get; private set; }
    public bool ScriptNamesChanged { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        GameDirectoryBox.Text = EditorSettings.Current.GameDirectory;
        Lba1DirectoryBox.Text = EditorSettings.Current.Lba1Directory;
        LowercaseNamesCheck.IsChecked = EditorSettings.Current.LowercaseScriptNames;
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

    private void BrowseLba1_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the LBA1 game folder",
            InitialDirectory = Directory.Exists(Lba1DirectoryBox.Text) ? Lba1DirectoryBox.Text : null,
        };
        if (dialog.ShowDialog(this) == true) Lba1DirectoryBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = "";
        Lba1ValidationText.Text = "";

        // Either folder can stay blank (a user may own only one of the games); anything filled in must be the real thing.
        var path = GameDirectoryBox.Text.Trim();
        if (path.Length > 0 && !Lba2Folder.IsValid(path))
        {
            ValidationText.Text = "Not an LBA2 folder (needs RESS.HQR, SCENE.HQR and the .ILE islands).";
            return;
        }
        var lba1Path = Lba1DirectoryBox.Text.Trim();
        if (lba1Path.Length > 0 && !Lba1.Lba1Game.IsInstalled(lba1Path))
        {
            Lba1ValidationText.Text = "Not an LBA1 folder (needs SCENE, LBA_GRI, LBA_BLL, LBA_BRK, RESS).";
            return;
        }

        var settings = EditorSettings.Current;
        GameDirectoryChanged = !string.Equals(settings.GameDirectory, path, StringComparison.OrdinalIgnoreCase);
        settings.GameDirectory = path;
        Lba1DirectoryChanged = !string.Equals(settings.Lba1Directory, lba1Path, StringComparison.OrdinalIgnoreCase);
        settings.Lba1Directory = lba1Path;
        ScriptNamesChanged = settings.LowercaseScriptNames != (LowercaseNamesCheck.IsChecked == true);
        settings.LowercaseScriptNames = LowercaseNamesCheck.IsChecked == true;
        settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
