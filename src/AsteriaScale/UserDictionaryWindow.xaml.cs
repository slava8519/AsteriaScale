using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AsteriaScale.Services;

namespace AsteriaScale;

/// <summary>
/// Editor for the user's own dictionary layer plus the translation history
/// review panel with Anki export. Saving an entry reloads the dictionaries so
/// the next hover already uses it.
/// </summary>
public partial class UserDictionaryWindow : Window
{
    private readonly UserDictionaryService _userDictionary;
    private readonly TranslationHistoryService _history;
    private readonly Func<Task> _reloadDictionaries;
    private readonly ObservableCollection<UserDictionaryEntry> _entries = [];
    private readonly ObservableCollection<TranslationHistoryEntry> _historyEntries = [];
    private IReadOnlyList<UserDictionaryEntry> _allEntries = [];

    public UserDictionaryWindow(
        UserDictionaryService userDictionary,
        TranslationHistoryService history,
        Func<Task> reloadDictionaries)
    {
        InitializeComponent();
        _userDictionary = userDictionary;
        _history = history;
        _reloadDictionaries = reloadDictionaries;
        EntriesList.ItemsSource = _entries;
        HistoryList.ItemsSource = _historyEntries;
        Loaded += async (_, _) =>
        {
            await RefreshEntriesAsync();
            RefreshHistory();
        };
    }

    private async Task RefreshEntriesAsync()
    {
        _allEntries = await _userDictionary.LoadAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        _entries.Clear();
        foreach (UserDictionaryEntry entry in _allEntries)
        {
            if (query.Length == 0 ||
                entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Translation.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _entries.Add(entry);
            }
        }
    }

    private void RefreshHistory()
    {
        _historyEntries.Clear();
        foreach (TranslationHistoryEntry entry in _history.Snapshot())
        {
            _historyEntries.Add(entry);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (EntriesList.SelectedItem is UserDictionaryEntry entry)
        {
            KeyBox.Text = entry.Key;
            TranslationBox.Text = entry.Translation;
        }
    }

    private async void OnSaveEntryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string canonical = await _userDictionary.SaveEntryAsync(
                KeyBox.Text,
                TranslationBox.Text);
            await RefreshEntriesAsync();
            await _reloadDictionaries();
            KeyBox.Clear();
            TranslationBox.Clear();
            StatusText.Text = $"Сохранено: «{canonical}». Словари перечитаны.";
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void OnRemoveEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: UserDictionaryEntry entry })
        {
            return;
        }

        await _userDictionary.RemoveEntryAsync(entry.Key);
        await RefreshEntriesAsync();
        await _reloadDictionaries();
        StatusText.Text = $"Удалено: «{entry.Key}». Словари перечитаны.";
    }

    private void OnPromoteHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: TranslationHistoryEntry entry })
        {
            KeyBox.Text = entry.Source;
            TranslationBox.Text = entry.Translation;
            StatusText.Text = "Проверьте перевод и нажмите «Сохранить в мой словарь».";
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Экспорт истории для Anki",
            FileName = "asteria-anki.tsv",
            Filter = "Anki (TSV)|*.tsv|Все файлы|*.*",
            InitialDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            int exported = _history.ExportToAnki(dialog.FileName);
            StatusText.Text = exported == 0
                ? "История пуста — экспортировать нечего."
                : $"Экспортировано карточек: {exported}. В Anki: Файл → Импорт, разделитель «Табуляция».";
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException)
        {
            StatusText.Text = "Не удалось записать файл: " + exception.Message;
        }
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                this,
                "Очистить историю переводов?",
                "Asteria Scale",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _history.Clear();
        RefreshHistory();
        StatusText.Text = "История очищена.";
    }
}
