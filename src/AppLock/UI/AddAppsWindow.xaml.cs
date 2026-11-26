using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AppLock.Core;
using AppLock.Platform;
using Microsoft.Win32;

namespace AppLock.UI;

/// <summary>ロックするアプリを選ぶ(スタート メニュー・ストアのアプリ・動いているアプリ・実行ファイル)</summary>
public partial class AddAppsWindow : Window
{
    private readonly AppController app;
    private readonly ObservableCollection<CandidateRow> rows = [];
    private readonly ICollectionView view;

    internal AddAppsWindow(AppController app)
    {
        this.app = app;
        InitializeComponent();
        List.ItemsSource = rows;
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = Matches;
        Loaded += async (_, _) => await LoadAsync();
    }

    internal IReadOnlyList<Candidate> Selected { get; private set; } = [];

    private async Task LoadAsync()
    {
        var locked = app.Store.Current.LockedIds;
        List<Candidate> candidates;
        try
        {
            candidates = await StaWorker.Run(() => AppCatalog.Load(Log.Write));
        }
        catch (Exception e)
        {
            Log.Write($"アプリの一覧を作れない: {e.Message}");
            candidates = [];
        }
        foreach (var candidate in candidates.Where(c => !locked.Contains(c.Id) && rows.All(r => r.Candidate.Id != c.Id)))
        {
            rows.Add(NewRow(candidate, isChecked: false));
        }
        UpdateStatus();
        SearchBox.Focus();
    }

    private CandidateRow NewRow(Candidate candidate, bool isChecked)
    {
        var row = new CandidateRow(candidate) { IsChecked = isChecked };
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CandidateRow.IsChecked)) AddButton.IsEnabled = rows.Any(r => r.IsChecked);
        };
        _ = LoadIconAsync(row);
        return row;
    }

    private async Task LoadIconAsync(CandidateRow row)
    {
        var candidate = row.Candidate;
        row.Icon = await app.Icons.GetAsync(candidate.PackageFamilyName is not null && candidate.AppUserModelId is not null
            ? $@"shell:AppsFolder\{candidate.AppUserModelId}"
            : candidate.Path);
    }

    private void UpdateStatus()
    {
        var anyVisible = !view.IsEmpty;
        StatusText.Text = rows.Count == 0 ? "追加できるアプリが見つかりませんでした" : "一致するアプリはありません";
        StatusText.Visibility = anyVisible ? Visibility.Collapsed : Visibility.Visible;
        AddButton.IsEnabled = rows.Any(r => r.IsChecked);
    }

    private bool Matches(object item)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return true;
        var row = (CandidateRow)item;
        var compare = CultureInfo.GetCultureInfo("ja-JP").CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;
        return compare.IndexOf(row.Name, query, options) >= 0 || compare.IndexOf(row.Candidate.ExeName, query, options) >= 0;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        view.Refresh();
        UpdateStatus();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "ロックするアプリを選択",
            Filter = "アプリ (*.exe)|*.exe",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) != true) return;

        var rejected = new List<string>();
        var locked = app.Store.Current.LockedIds;
        foreach (var path in dialog.FileNames)
        {
            var candidate = AppCatalog.FromExecutable(path);
            if (!SystemApps.CanLock(candidate.ExeName))
            {
                rejected.Add(candidate.Name);
                continue;
            }
            if (locked.Contains(candidate.Id)) continue;
            var existing = rows.FirstOrDefault(r => r.Candidate.Id == candidate.Id);
            if (existing is not null)
            {
                existing.IsChecked = true;
                List.ScrollIntoView(existing);
                continue;
            }
            var row = NewRow(candidate, isChecked: true);
            rows.Insert(0, row);
            List.ScrollIntoView(row);
        }
        UpdateStatus();
        if (rejected.Count > 0)
        {
            MessageBox.Show(this, $"{string.Join("、", rejected)}はロックできません。\nロックすると Windows を操作できなくなるためです。",
                "アプリロック", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        Selected = rows.Where(r => r.IsChecked).Select(r => r.Candidate).ToList();
        DialogResult = true;
    }
}

internal sealed class CandidateRow(Candidate candidate) : INotifyPropertyChanged
{
    private bool isChecked;
    private ImageSource? icon;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Candidate Candidate { get; } = candidate;
    public string Name => Candidate.Name;
    public string Detail => Candidate.IsRunning ? $"{Candidate.ExeName}・実行中" : Candidate.ExeName;

    public bool IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value);
    }

    public ImageSource? Icon
    {
        get => icon;
        set
        {
            Set(ref icon, value);
            OnChanged(nameof(FallbackVisibility));
        }
    }

    public Visibility FallbackVisibility => icon is null ? Visibility.Visible : Visibility.Collapsed;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
