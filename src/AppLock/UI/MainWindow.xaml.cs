using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppLock.Core;
using AppLock.Platform;

namespace AppLock.UI;

/// <summary>アプリロックの窓(ロックするアプリと設定)。閉じても隠すだけで、アプリロックは動き続ける。</summary>
public partial class MainWindow : Window
{
    private static readonly int[] MinuteChoices = [1, 2, 3, 5, 10, 15, 20, 30, 45, 60];

    private readonly AppController app;
    private readonly ObservableCollection<AppRow> rows = [];
    private bool unlockedForEditing;
    private bool updating;

    internal MainWindow(AppController app)
    {
        this.app = app;
        InitializeComponent();
        AppsList.ItemsSource = rows;
        VersionText.Text = $"アプリロック {AppController.Version}";
        app.StateChanged += Refresh;
        Refresh();
    }

    private bool CanEdit => !app.NeedsSelfAuthentication || unlockedForEditing;

    public void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        updating = true;
        try
        {
            var settings = app.Store.Current;
            var now = DateTimeOffset.Now;
            var states = settings.Apps.Select(a => (App: a, State: app.Coordinator.StateOf(a.Id))).ToList();
            SyncRows(states, now);

            var unlocked = states.Where(s => s.State.Kind == AppStateKind.Unlocked).ToList();
            Headline.Text = settings.Apps.Count == 0 ? "ロックしているアプリはありません"
                : unlocked.Count == 0 ? $"{settings.Apps.Count} 個のアプリをロック中"
                : $"{unlocked.Count} 個のアプリを解除中";
            var method = app.HelloAvailable ? "Windows Hello で" : "Windows のパスワードで";
            Subline.Text = unlocked.Count == 0 ? $"開くときに {method}確かめます" : string.Join("、", unlocked.Select(u => u.App.Name));
            EmptyText.Text = $"ロックしたアプリは、開くときに {method}確かめます。";
            HeaderGlyph.Text = unlocked.Count == 0 ? "" : "";
            RelockAllButton.Visibility = unlocked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = settings.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var canEdit = CanEdit;
            AuthBanner.Visibility = canEdit ? Visibility.Collapsed : Visibility.Visible;
            UpdateHelloBanner();

            AddButton.IsEnabled = canEdit;
            RelockRuleBox.SelectedIndex = (int)settings.RelockRule;
            RelockRuleBox.IsEnabled = canEdit;
            RelockNote.Visibility = settings.RelockRule == RelockRule.WhenAppQuits ? Visibility.Collapsed : Visibility.Visible;
            MinutesRow.Visibility = settings.RelockRule == RelockRule.AfterInterval ? Visibility.Visible : Visibility.Collapsed;
            FillMinutes(settings.RelockMinutes);
            MinutesBox.IsEnabled = canEdit;
            QuitOnFailureBox.IsChecked = settings.QuitAppOnFailure;
            QuitOnFailureBox.IsEnabled = canEdit;
            ProtectSelfBox.IsChecked = settings.ProtectSelf;
            ProtectSelfBox.IsEnabled = canEdit;
            StartupBox.IsChecked = app.StartupEnabled;
            StartupBox.IsEnabled = canEdit && !Testing.Enabled;
            foreach (var row in rows) row.CanEdit = canEdit;
        }
        finally
        {
            updating = false;
        }
    }

    /// <summary>1 秒ごと。解除の残り時間を書き直す</summary>
    public void Tick()
    {
        if (!IsVisible) return;
        var now = DateTimeOffset.Now;
        foreach (var row in rows) row.Update(app.Coordinator.StateOf(row.Id), now);
    }

    private void SyncRows(List<(LockedApp App, AppState State)> states, DateTimeOffset now)
    {
        var ids = states.Select(s => s.App.Id).ToList();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(rows[i].Id)) rows.RemoveAt(i);
        }
        for (var i = 0; i < states.Count; i++)
        {
            var (lockedApp, state) = states[i];
            var index = IndexOf(lockedApp.Id);
            AppRow row;
            if (index < 0)
            {
                row = new AppRow(lockedApp);
                rows.Insert(i, row);
                _ = LoadIconAsync(row);
            }
            else
            {
                row = rows[index];
                if (index != i) rows.Move(index, i);
                row.App = lockedApp;
            }
            row.Update(state, now);
            row.DividerVisibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id == id) return i;
        }
        return -1;
    }

    private async Task LoadIconAsync(AppRow row)
    {
        row.Icon = await app.Icons.GetAsync(IconCache.ParsingName(row.App));
    }

    private void FillMinutes(int minutes)
    {
        var choices = MinuteChoices.Contains(minutes) ? MinuteChoices : MinuteChoices.Append(minutes).Order().ToArray();
        if (MinutesBox.Items.Count != choices.Length)
        {
            MinutesBox.Items.Clear();
            foreach (var choice in choices) MinutesBox.Items.Add(new ComboBoxItem { Content = $"{choice} 分", Tag = choice });
        }
        MinutesBox.SelectedIndex = Array.IndexOf(choices, minutes);
    }

    private void UpdateHelloBanner()
    {
        switch (app.Hello)
        {
            case Native.HelloAvailability.NotConfiguredForUser:
            case Native.HelloAvailability.DeviceNotPresent:
                HelloTitle.Text = "Windows Hello が設定されていません";
                HelloText.Text = "ロックしたアプリを開くときは、Windows のパスワードで確かめます。PIN を設定すると、PIN・指紋・顔でも確かめられます。";
                HelloButton.Visibility = Visibility.Visible;
                HelloBanner.Visibility = Visibility.Visible;
                break;
            case Native.HelloAvailability.DisabledByPolicy:
                HelloTitle.Text = "Windows Hello は組織のポリシーで無効になっています";
                HelloText.Text = "ロックしたアプリを開くときは、Windows のパスワードで確かめます。";
                HelloButton.Visibility = Visibility.Collapsed;
                HelloBanner.Visibility = Visibility.Visible;
                break;
            default:
                HelloBanner.Visibility = Visibility.Collapsed;
                break;
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _ = app.RefreshHelloAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 閉じても動き続ける(終了は通知領域のアイコンから)
        e.Cancel = true;
        Hide();
        unlockedForEditing = false;
        Refresh();
        app.MainWindowClosed();
    }

    // ---- 操作 ----

    private void OnRelockAll(object sender, RoutedEventArgs e) => app.RelockAll();

    private async void OnAuthenticate(object sender, RoutedEventArgs e)
    {
        if (await app.AuthenticateAsync("アプリロックの設定を変更"))
        {
            unlockedForEditing = true;
            Refresh();
        }
    }

    private void OnOpenSignInOptions(object sender, RoutedEventArgs e) => AppController.OpenUri("ms-settings:signinoptions");

    private void OnAddApps(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAppsWindow(app) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selected.Count == 0) return;
        app.Store.Update(settings =>
        {
            foreach (var candidate in dialog.Selected)
            {
                if (settings.Apps.All(a => a.Id != candidate.Id)) settings.Apps.Add(candidate.ToLockedApp());
            }
            settings.Apps = settings.Apps.OrderBy(a => a.Name, AppCatalog.NameOrder).ToList();
        });
    }

    private void OnOpenApp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRow row) app.OpenApp(row.App);
    }

    private void OnRemoveApp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppRow row || !CanEdit) return;
        app.Store.Update(settings => settings.Apps = settings.Apps.Where(a => a.Id != row.Id).ToList());
    }

    private void OnRelockRuleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || RelockRuleBox.SelectedIndex < 0 || !CanEdit) return;
        var rule = (RelockRule)RelockRuleBox.SelectedIndex;
        app.Store.Update(settings => settings.RelockRule = rule);
    }

    private void OnMinutesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || MinutesBox.SelectedItem is not ComboBoxItem { Tag: int minutes } || !CanEdit) return;
        app.Store.Update(settings => settings.RelockMinutes = minutes);
    }

    private void OnQuitOnFailureChanged(object sender, RoutedEventArgs e)
    {
        if (updating || !CanEdit) return;
        var quit = QuitOnFailureBox.IsChecked == true;
        app.Store.Update(settings => settings.QuitAppOnFailure = quit);
    }

    private void OnProtectSelfChanged(object sender, RoutedEventArgs e)
    {
        if (updating || !CanEdit) return;
        var protect = ProtectSelfBox.IsChecked == true;
        // 自分でオフにしたあとも、この窓を閉じるまでは続けて変えられる
        unlockedForEditing = true;
        app.Store.Update(settings => settings.ProtectSelf = protect);
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (updating || !CanEdit) return;
        app.StartupEnabled = StartupBox.IsChecked == true;
        Refresh();
    }
}

/// <summary>ロックするアプリの 1 行</summary>
internal sealed class AppRow(LockedApp app) : INotifyPropertyChanged
{
    private LockedApp app = app;
    private ImageSource? icon;
    private string stateText = "";
    private string stateGlyph = "";
    private Brush? stateBrush;
    private bool canEdit;
    private Visibility dividerVisibility;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LockedApp App
    {
        get => app;
        set
        {
            if (app == value) return;
            app = value;
            OnChanged();
            OnChanged(nameof(Name));
            OnChanged(nameof(RemoveLabel));
        }
    }

    public string Id => app.Id;
    public string Name => app.Name;
    public string RemoveLabel => $"{app.Name}のロックをやめる";

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

    public string StateText
    {
        get => stateText;
        private set => Set(ref stateText, value);
    }

    public string StateGlyph
    {
        get => stateGlyph;
        private set => Set(ref stateGlyph, value);
    }

    public Brush? StateBrush
    {
        get => stateBrush;
        private set => Set(ref stateBrush, value);
    }

    public bool CanEdit
    {
        get => canEdit;
        set => Set(ref canEdit, value);
    }

    public Visibility DividerVisibility
    {
        get => dividerVisibility;
        set => Set(ref dividerVisibility, value);
    }

    public void Update(AppState state, DateTimeOffset now)
    {
        switch (state.Kind)
        {
            case AppStateKind.Authenticating:
                StateText = "認証中";
                StateGlyph = "";
                StateBrush = Resource("AccentTextFillColorPrimaryBrush");
                break;
            case AppStateKind.Unlocked:
                StateText = state.Lifetime is { Kind: LifetimeKind.Until } lifetime
                    ? $"解除中・あと {Math.Max(1, (int)Math.Ceiling((lifetime.End - now).TotalMinutes))} 分"
                    : "解除中";
                StateGlyph = "";
                StateBrush = Resource("SystemFillColorSuccessBrush");
                break;
            default:
                StateText = "ロック中";
                StateGlyph = "";
                StateBrush = Resource("LockBrush");
                break;
        }
    }

    private static Brush? Resource(string key) => Application.Current.TryFindResource(key) as Brush;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
