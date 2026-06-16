using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Shhhcribble.Core.Text;
using Shhhcribble.Core.Transcription;
using Shhhcribble.Windows.Input;

namespace Shhhcribble.Windows.UI;

public partial class SettingsWindow : Window
{
    public event Action<string>? HotkeyChanged;
    public event Action<string>? ModelChanged;
    public event Action? SettingsChanged;

    private sealed record ActivationOption(string Id, string Label);

    private static readonly ActivationOption[] Activations =
    {
        new("hybrid", "Hybrid (tap = toggle, hold = push)"),
        new("toggle", "Toggle"),
        new("pushToTalk", "Push-to-talk"),
    };

    private readonly ObservableCollection<DictionaryEntry> _dict;

    public SettingsWindow()
    {
        InitializeComponent();
        var s = SettingsStore.Current;

        ModelCombo.ItemsSource = ParakeetModels.All;
        ModelCombo.SelectedItem = ParakeetModels.ById(s.ModelId);

        HotkeyCombo.ItemsSource = HotkeyOptions.All;
        HotkeyCombo.SelectedItem = HotkeyOptions.ById(s.HotkeyId);

        ActivationCombo.ItemsSource = Activations;
        ActivationCombo.SelectedItem = Activations.FirstOrDefault(a => a.Id == s.ActivationMode) ?? Activations[0];

        FillerCheck.IsChecked = s.FillerFilterEnabled;
        PauseMusicCheck.IsChecked = s.PauseMusicEnabled;

        _dict = new ObservableCollection<DictionaryEntry>(
            s.Dictionary.Select(e => new DictionaryEntry(e.Phrase, e.Replacement, e.CaseSensitive)));
        DictGrid.ItemsSource = _dict;

        ModelCombo.SelectionChanged += OnModelChanged;
        HotkeyCombo.SelectionChanged += OnHotkeyChanged;
        ActivationCombo.SelectionChanged += OnActivationChanged;
        FillerCheck.Click += (_, _) => Persist(s => s.FillerFilterEnabled = FillerCheck.IsChecked == true);
        PauseMusicCheck.Click += (_, _) => Persist(s => s.PauseMusicEnabled = PauseMusicCheck.IsChecked == true);
        RemoveRowButton.Click += OnRemoveRow;
        SaveButton.Click += OnSave;
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is ParakeetModel m)
        {
            Persist(s => s.ModelId = m.Id);
            ModelChanged?.Invoke(m.Id);
        }
    }

    private void OnHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HotkeyCombo.SelectedItem is HotkeyOption h)
        {
            Persist(s => s.HotkeyId = h.Id);
            HotkeyChanged?.Invoke(h.Id);
        }
    }

    private void OnActivationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActivationCombo.SelectedItem is ActivationOption a)
            Persist(s => s.ActivationMode = a.Id);
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (DictGrid.SelectedItem is DictionaryEntry entry)
            _dict.Remove(entry);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DictGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SettingsStore.Current.Dictionary = _dict
            .Where(d => !string.IsNullOrWhiteSpace(d.Phrase))
            .ToList();
        SettingsStore.Save();
        SettingsChanged?.Invoke();
        Close();
    }

    private void Persist(Action<AppSettings> mutate)
    {
        mutate(SettingsStore.Current);
        SettingsStore.Save();
        SettingsChanged?.Invoke();
    }
}
