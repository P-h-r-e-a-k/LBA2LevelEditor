using System.Windows;
using System.Windows.Controls;

namespace LBAAssembler;

// The sound balance for playing scenes, per game: mute, and music / speech / effects levels (the games' own balance is off, the
// music drowns the speech). Shown on the PLAY tab; LBA2's engine reads its volumes when it starts, the LBA1 play view applies
// them at once. They are saved with the editor's settings.
public partial class MainWindow
{
    private bool audioSyncing;
    private bool audioDirty;

    private AudioLevels CurrentAudio => currentGame == GameKind.Lba1 ? EditorSettings.Current.Lba1Audio : EditorSettings.Current.Lba2Audio;

    private void SyncAudioControls()
    {
        audioSyncing = true;
        try
        {
            var audio = CurrentAudio;
            var name = currentGame == GameKind.Lba1 ? "LBA1" : "LBA2";
            AudioHeading.Text = $"Sound for {name}";
            AudioMuteCheck.IsChecked = audio.Mute;
            AudioMusicSlider.Value = audio.Music; AudioVoicesSlider.Value = audio.Voices; AudioEffectsSlider.Value = audio.Effects;
            ShowAudioValues(audio);
            AudioMusicSlider.IsEnabled = AudioVoicesSlider.IsEnabled = AudioEffectsSlider.IsEnabled = !audio.Mute;
            PlayEngineOptions.Visibility = currentGame == GameKind.Lba2 ? Visibility.Visible : Visibility.Collapsed;
            PlayIntroText.Text = currentGame == GameKind.Lba2
                ? "The Play scene button starts the scene that is open in the game (LBA2's own engine) inside this window. It plays what is saved on disk."
                : "The Play scene button plays the scene that is open in the LBA1 play view inside this window. It plays what is saved on disk.";
            AudioNote.Text = currentGame == GameKind.Lba2
                ? "The game reads its volumes when it starts: change them here, then play (or restart) the scene."
                : "These change the sound at once while a scene is playing.";
        }
        finally { audioSyncing = false; }
    }

    private void ShowAudioValues(AudioLevels audio)
    {
        AudioMusicValue.Text = audio.Music + "%";
        AudioVoicesValue.Text = audio.Voices + "%";
        AudioEffectsValue.Text = audio.Effects + "%";
    }

    private void AudioMute_Click(object sender, RoutedEventArgs e)
    {
        var audio = CurrentAudio;
        audio.Mute = AudioMuteCheck.IsChecked == true;
        AudioMusicSlider.IsEnabled = AudioVoicesSlider.IsEnabled = AudioEffectsSlider.IsEnabled = !audio.Mute;
        audioDirty = true;
        lba1Play?.ApplyAudio();
    }

    private void AudioSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (audioSyncing) return;
        var audio = CurrentAudio;
        audio.Music = (int)AudioMusicSlider.Value; audio.Voices = (int)AudioVoicesSlider.Value; audio.Effects = (int)AudioEffectsSlider.Value;
        ShowAudioValues(audio);
        audioDirty = true;
    }

    private void AudioReset_Click(object sender, RoutedEventArgs e)
    {
        var audio = CurrentAudio;
        var defaults = new AudioLevels();
        audio.Mute = false; audio.Music = defaults.Music; audio.Voices = defaults.Voices; audio.Effects = defaults.Effects;
        audioDirty = true;
        SyncAudioControls();
        lba1Play?.ApplyAudio();
    }

    private void SaveAudioIfDirty()
    {
        if (!audioDirty) return;
        audioDirty = false;
        try { EditorSettings.Current.Save(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { DebugLog.Log($"MainWindow: saving the sound balance failed: {error.Message}"); }
    }
}
