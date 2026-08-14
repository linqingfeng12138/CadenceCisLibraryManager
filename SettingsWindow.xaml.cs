using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CadenceCisLibraryManager.Models;
using CadenceCisLibraryManager.Services;

namespace CadenceCisLibraryManager;

public partial class SettingsWindow : Window
{
    private readonly DatabaseService _databaseService = new();
    private readonly AppSettings _settings;
    private readonly ObservableCollection<TablePrefixRow> _tablePrefixRows = [];

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        TablePrefixesGrid.ItemsSource = _tablePrefixRows;
        ApplySettingsToUi();
    }

    public AppSettings Settings => _settings;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsFromUi();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseFootprintPath_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolderInto(FootprintPathBox);
    }

    private void BrowseSymbolPath_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolderInto(SymbolPathBox);
    }

    private void BrowseModel3DPath_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolderInto(Model3DPathBox);
    }

    private void BrowsePinPath_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolderInto(PinPathBox);
    }

    private async void RefreshTablePrefixes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsFromUi();
            var existingPrefixes = ReadTablePrefixesFromGrid();
            var tables = await _databaseService.GetTablesAsync(_settings);
            _tablePrefixRows.Clear();
            foreach (var table in tables)
            {
                existingPrefixes.TryGetValue(table, out var prefix);
                _tablePrefixRows.Add(new TablePrefixRow { TableName = table, Prefix = prefix ?? string.Empty });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "刷新表名失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void BrowseFolderInto(TextBox target)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择库路径",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            target.Text = dialog.FolderName;
        }
    }

    private void ApplySettingsToUi()
    {
        ServerBox.Text = _settings.Server;
        PortBox.Text = _settings.Port.ToString();
        DatabaseBox.Text = _settings.Database;
        UserBox.Text = _settings.UserId;
        PasswordBox.Password = _settings.Password;
        FootprintPathBox.Text = _settings.FootprintLibraryPath;
        SymbolPathBox.Text = _settings.SymbolLibraryPath;
        Model3DPathBox.Text = _settings.Model3DLibraryPath;
        PinPathBox.Text = _settings.PinLibraryPath;
        StoreFileNameCheckBox.IsChecked = _settings.StoreRelativeLibraryFileName;
        PartNumberIdWidthBox.Text = Math.Max(1, _settings.PartNumberIdWidth).ToString();
        PartNumberColumnsBox.Text = JoinNames(_settings.PartNumberColumnNames);
        FootprintColumnsBox.Text = JoinNames(_settings.FootprintColumnNames);
        SymbolColumnsBox.Text = JoinNames(_settings.SymbolColumnNames);
        Model3DColumnsBox.Text = JoinNames(_settings.Model3DColumnNames);
        _tablePrefixRows.Clear();
        foreach (var pair in _settings.TablePartNumberPrefixes.OrderBy(pair => pair.Key))
        {
            _tablePrefixRows.Add(new TablePrefixRow { TableName = pair.Key, Prefix = pair.Value });
        }
    }

    private void ReadSettingsFromUi()
    {
        if (!uint.TryParse(PortBox.Text, out var port))
        {
            throw new InvalidOperationException("端口必须是数字。");
        }

        if (!int.TryParse(PartNumberIdWidthBox.Text, out var width) || width < 1)
        {
            throw new InvalidOperationException("ID 位数必须是大于 0 的数字。");
        }

        _settings.Server = ServerBox.Text.Trim();
        _settings.Port = port;
        _settings.Database = DatabaseBox.Text.Trim();
        _settings.UserId = UserBox.Text.Trim();
        _settings.Password = PasswordBox.Password;
        _settings.FootprintLibraryPath = FootprintPathBox.Text.Trim();
        _settings.SymbolLibraryPath = SymbolPathBox.Text.Trim();
        _settings.Model3DLibraryPath = Model3DPathBox.Text.Trim();
        _settings.PinLibraryPath = PinPathBox.Text.Trim();
        _settings.StoreRelativeLibraryFileName = StoreFileNameCheckBox.IsChecked == true;
        _settings.PartNumberIdWidth = width;
        _settings.PartNumberColumnNames = SplitNames(PartNumberColumnsBox.Text);
        _settings.FootprintColumnNames = SplitNames(FootprintColumnsBox.Text);
        _settings.SymbolColumnNames = SplitNames(SymbolColumnsBox.Text);
        _settings.Model3DColumnNames = SplitNames(Model3DColumnsBox.Text);
        _settings.TablePartNumberPrefixes = ReadTablePrefixesFromGrid();
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        return string.Join(", ", names);
    }

    private static List<string> SplitNames(string value)
    {
        return value.Split([',', ';', '，', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private Dictionary<string, string> ReadTablePrefixesFromGrid()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _tablePrefixRows)
        {
            var tableName = row.TableName.Trim();
            if (tableName.Length == 0)
            {
                continue;
            }

            result[tableName] = row.Prefix.Trim();
        }

        return result;
    }

    public sealed class TablePrefixRow
    {
        public string TableName { get; set; } = string.Empty;

        public string Prefix { get; set; } = string.Empty;
    }
}
