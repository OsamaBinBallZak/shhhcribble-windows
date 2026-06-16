using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Shhhcribble.Core.Text;
using Shhhcribble.Core.Transcription;
using Shhhcribble.Windows.Input;

namespace Shhhcribble.Windows.UI;

public partial class SettingsWindow : Window
{
    // ── Public events (shared contract) ─────────────────────────────────────
    public event Action<string>? HotkeyChanged;
    public event Action<string>? ModelChanged;
    public event Action? SettingsChanged;

    // ── Private types ────────────────────────────────────────────────────────
    private sealed record ActivationOption(string Id, string Label);

    private static readonly ActivationOption[] Activations =
    {
        new("hybrid",     "Hybrid (tap = toggle, hold = push)"),
        new("toggle",     "Toggle"),
        new("pushToTalk", "Push-to-talk"),
    };

    private readonly ObservableCollection<DictionaryEntry> _dict;

    // Timer for refreshing mic-permission display every ~2 s while window is open
    private readonly DispatcherTimer _permissionTimer;

    // ── Busy / model-status state ────────────────────────────────────────────
    private bool _isBusy;
    private string _modelStatusText = "";
    private bool _modelStatusIsError;
    private bool _modelStatusLoading;

    // ── Constructor ──────────────────────────────────────────────────────────
    public SettingsWindow()
    {
        InitializeComponent();
        var s = SettingsStore.Current;

        // Model picker
        ModelCombo.ItemsSource   = ParakeetModels.All;
        ModelCombo.SelectedItem  = ParakeetModels.ById(s.ModelId);

        // Hotkey picker
        HotkeyCombo.ItemsSource  = HotkeyOptions.All;
        HotkeyCombo.SelectedItem = HotkeyOptions.ById(s.HotkeyId);

        // Activation picker
        ActivationCombo.ItemsSource  = Activations;
        ActivationCombo.SelectedItem = Activations.FirstOrDefault(a => a.Id == s.ActivationMode) ?? Activations[0];

        // Toggles
        FillerCheck.IsChecked      = s.FillerFilterEnabled;
        PauseMusicCheck.IsChecked  = s.PauseMusicEnabled;

        // Dictionary
        _dict = new ObservableCollection<DictionaryEntry>(
            s.Dictionary.Select(e => new DictionaryEntry(e.Phrase, e.Replacement, e.CaseSensitive)));
        DictGrid.ItemsSource = _dict;
        _dict.CollectionChanged += (_, _) => RefreshDictEmptyState();
        RefreshDictEmptyState();

        // Wire DictGrid selection changes to enable/disable reorder + remove buttons
        DictGrid.SelectionChanged += OnDictSelectionChanged;

        // Event wiring
        ModelCombo.SelectionChanged      += OnModelChanged;
        HotkeyCombo.SelectionChanged     += OnHotkeyChanged;
        ActivationCombo.SelectionChanged += OnActivationChanged;
        FillerCheck.Click     += (_, _) => Persist(s => s.FillerFilterEnabled = FillerCheck.IsChecked == true);
        PauseMusicCheck.Click += (_, _) => Persist(s => s.PauseMusicEnabled   = PauseMusicCheck.IsChecked == true);

        // About section
        VersionLabel.Text        = BuildVersionString();
        AboutShortcutHint.Text   = BuildShortcutHint(s.HotkeyId);

        // Permissions
        RefreshMicPermission();
        _permissionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _permissionTimer.Tick += (_, _) => RefreshMicPermission();
        _permissionTimer.Start();

        Closed += (_, _) => _permissionTimer.Stop();
    }

    // ── Public API (shared contract) ─────────────────────────────────────────

    /// <summary>
    /// Disable the model picker and show an amber inline warning while a
    /// recording is in progress. Mirrors macOS <c>.disabled(transcriptionEngine.isBusy)</c>
    /// + <c>InlineWarning("Stop the current recording…")</c>.
    /// </summary>
    public void SetBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetBusy(busy));
            return;
        }
        _isBusy              = busy;
        ModelCombo.IsEnabled = !busy;
        BusyWarningBorder.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Show a loading / ready / failed status line under the model picker.
    /// Pass <c>isError = false</c> for loading/ready and <c>true</c> for failed.
    /// </summary>
    public void SetModelStatus(string text, bool isError)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetModelStatus(text, isError));
            return;
        }

        _modelStatusText    = text;
        _modelStatusIsError = isError;

        // Decide whether the text looks like a loading message (contains "Loading" or "Downloading")
        _modelStatusLoading = !isError &&
            (text.Contains("Loading", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Downloading", StringComparison.OrdinalIgnoreCase));

        ApplyModelStatusUI();
    }

    // ── Model status UI helpers ──────────────────────────────────────────────

    private void ApplyModelStatusUI()
    {
        bool hasText = !string.IsNullOrWhiteSpace(_modelStatusText);

        ModelStatusRow.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        if (!hasText)
        {
            ModelStatusLoading.Visibility    = Visibility.Collapsed;
            ModelStatusTextSimple.Visibility = Visibility.Collapsed;
            ModelStatusError.Visibility      = Visibility.Collapsed;
            return;
        }

        if (_modelStatusIsError)
        {
            ModelStatusLoading.Visibility    = Visibility.Collapsed;
            ModelStatusTextSimple.Visibility = Visibility.Collapsed;
            ModelStatusError.Visibility      = Visibility.Visible;
            ModelStatusErrorText.Text        = _modelStatusText;
        }
        else if (_modelStatusLoading)
        {
            ModelStatusLoading.Visibility    = Visibility.Visible;
            ModelStatusText.Text             = _modelStatusText;
            ModelStatusTextSimple.Visibility = Visibility.Collapsed;
            ModelStatusError.Visibility      = Visibility.Collapsed;
        }
        else
        {
            ModelStatusLoading.Visibility    = Visibility.Collapsed;
            ModelStatusTextSimple.Visibility = Visibility.Visible;
            ModelStatusTextSimple.Text       = _modelStatusText;
            ModelStatusError.Visibility      = Visibility.Collapsed;
        }
    }

    // ── ComboBox / checkbox handlers ─────────────────────────────────────────

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
            // Keep About shortcut hint in sync
            AboutShortcutHint.Text = BuildShortcutHint(h.Id);
        }
    }

    private void OnActivationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActivationCombo.SelectedItem is ActivationOption a)
            Persist(s => s.ActivationMode = a.Id);
    }

    // ── Dictionary: persist on every mutation ────────────────────────────────

    /// <summary>
    /// Called when a DataGrid row edit ends. Validates, trims, then persists
    /// immediately — matching macOS <c>saveDictionary()</c> on every add/edit.
    /// </summary>
    private void OnRowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        // Commit is still pending at this point — schedule the persist for the
        // next dispatcher frame once the binding has written back.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            TrimAndValidateDict();
            PersistDict();
        });
    }

    /// <summary>
    /// Called when a single cell edit ends. Validates + persists immediately.
    /// </summary>
    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            TrimAndValidateDict();
            PersistDict();
        });
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (DictGrid.SelectedItem is DictionaryEntry entry)
        {
            _dict.Remove(entry);
            PersistDict();
        }
    }

    // ── Dictionary reorder ───────────────────────────────────────────────────

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        int idx = DictGrid.SelectedIndex;
        if (idx <= 0 || idx >= _dict.Count) return;
        _dict.Move(idx, idx - 1);
        DictGrid.SelectedIndex = idx - 1;
        PersistDict();
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        int idx = DictGrid.SelectedIndex;
        if (idx < 0 || idx >= _dict.Count - 1) return;
        _dict.Move(idx, idx + 1);
        DictGrid.SelectedIndex = idx + 1;
        PersistDict();
    }

    private void OnDictSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx   = DictGrid.SelectedIndex;
        int count = _dict.Count;

        MoveUpButton.IsEnabled   = idx > 0;
        MoveDownButton.IsEnabled = idx >= 0 && idx < count - 1;
        RemoveRowButton.IsEnabled = idx >= 0;
    }

    // ── Dictionary helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Trims both fields and removes rows where either field is empty/whitespace.
    /// Matches macOS DictionaryEntryEditor validation (both non-empty + trimmed).
    /// </summary>
    private void TrimAndValidateDict()
    {
        var toRemove = _dict
            .Where(d => string.IsNullOrWhiteSpace(d.Phrase) || string.IsNullOrWhiteSpace(d.Replacement))
            .ToList();

        foreach (var entry in toRemove)
            _dict.Remove(entry);

        foreach (var entry in _dict)
        {
            entry.Phrase      = entry.Phrase?.Trim() ?? "";
            entry.Replacement = entry.Replacement?.Trim() ?? "";
        }
    }

    /// <summary>
    /// Write the current dictionary collection to <see cref="SettingsStore"/>,
    /// save to disk, and fire <see cref="SettingsChanged"/>. Matches macOS
    /// <c>saveDictionary()</c> called from every mutation path.
    /// </summary>
    private void PersistDict()
    {
        SettingsStore.Current.Dictionary = _dict
            .Where(d => !string.IsNullOrWhiteSpace(d.Phrase) && !string.IsNullOrWhiteSpace(d.Replacement))
            .Select(d => new DictionaryEntry(d.Phrase.Trim(), d.Replacement.Trim(), d.CaseSensitive))
            .ToList();
        SettingsStore.Save();
        SettingsChanged?.Invoke();
        RefreshDictEmptyState();
    }

    private void RefreshDictEmptyState()
    {
        bool empty = _dict.Count == 0 ||
                     _dict.All(d => string.IsNullOrWhiteSpace(d.Phrase) && string.IsNullOrWhiteSpace(d.Replacement));
        DictEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RefreshReorderButtons();
    }

    private void RefreshReorderButtons()
    {
        int idx   = DictGrid.SelectedIndex;
        int count = _dict.Count;
        MoveUpButton.IsEnabled    = idx > 0;
        MoveDownButton.IsEnabled  = idx >= 0 && idx < count - 1;
        RemoveRowButton.IsEnabled = idx >= 0;
    }

    // ── Permissions ──────────────────────────────────────────────────────────

    private void RefreshMicPermission()
    {
        bool granted = CheckMicPermission();
        if (granted)
        {
            MicGrantedLabel.Visibility  = Visibility.Visible;
            MicSettingsButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            MicGrantedLabel.Visibility  = Visibility.Collapsed;
            MicSettingsButton.Visibility = Visibility.Visible;
        }
    }

    private static bool CheckMicPermission()
    {
        // Try the WinRT AppCapability API (Windows 10 1809+) to check mic access.
        // Fall back to "assumed granted" on failure (older OS or API unavailable).
        try
        {
            // Use reflection to avoid a hard compile-time dependency on WinRT types
            // that may not resolve cleanly on all build environments.
            var type = Type.GetType(
                "Windows.Security.Authorization.AppCapabilityAccess.AppCapability, Windows, " +
                "Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, " +
                "ContentType=WindowsRuntime");
            if (type == null) return true;

            var createMethod = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            if (createMethod == null) return true;

            var cap = createMethod.Invoke(null, new object[] { "microphone" });
            if (cap == null) return true;

            var checkMethod = cap.GetType().GetMethod("CheckAccess");
            if (checkMethod == null) return true;

            var result = checkMethod.Invoke(cap, null);
            // AppCapabilityAccessStatus.Allowed == 1
            return result is int i && i == 1;
        }
        catch
        {
            return true;
        }
    }

    private void OnOpenMicSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "ms-settings:privacy-microphone",
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    // ── About ────────────────────────────────────────────────────────────────

    private static string BuildVersionString()
    {
        var asm  = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var ver  = attr?.InformationalVersion;

        // Strip metadata suffix (e.g. "+abc1234") if present
        if (ver != null)
        {
            int plus = ver.IndexOf('+');
            if (plus >= 0) ver = ver[..plus];
        }

        return $"v{ver ?? "?"}";
    }

    private static string BuildShortcutHint(string hotkeyId)
    {
        var option = HotkeyOptions.ById(hotkeyId);
        return $"Tap {option.Label} to start recording and tap again to stop, " +
               "or hold it and release — text pastes into any field.";
    }

    // ── Shared persist helper ────────────────────────────────────────────────

    private void Persist(Action<AppSettings> mutate)
    {
        mutate(SettingsStore.Current);
        SettingsStore.Save();
        SettingsChanged?.Invoke();
    }
}
