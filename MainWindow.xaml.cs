using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CadenceCisLibraryManager.Models;
using CadenceCisLibraryManager.Services;

namespace CadenceCisLibraryManager;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly DatabaseService _databaseService = new();
    private readonly FileLibraryService _fileLibraryService = new();
    private readonly Dictionary<string, TextBox> _inputs = [];
    private readonly Dictionary<FileColumnKind, List<string>> _selectedFiles = [];
    private string? _sourceSymbolLibraryPath;
    private IReadOnlyList<ColumnInfo> _columns = [];
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        LoadTargetSymbolLibraries();
        SetStatus("设置已加载。请先浏览文件，再读取表并生成表单。");
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings = dialog.Settings;
            await _settingsService.SaveAsync(_settings);
            LoadTargetSymbolLibraries();
            ApplySelectedFilesToForm();
            SetStatus("设置已保存。");
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow { Owner = this };
        dialog.ShowDialog();
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _databaseService.TestConnectionAsync(_settings);
            SetStatus("MariaDB 连接成功。");
        }
        catch (Exception ex)
        {
            ShowError("连接失败：" + ex.Message);
        }
    }

    private async void RefreshTables_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tables = await _databaseService.GetTablesAsync(_settings);
            TableComboBox.ItemsSource = tables;
            if (tables.Count > 0)
            {
                TableComboBox.SelectedIndex = 0;
            }

            SetStatus($"已读取 {tables.Count} 张表。");
        }
        catch (Exception ex)
        {
            ShowError("读取表失败：" + ex.Message);
        }
    }

    private async void LoadColumns_Click(object sender, RoutedEventArgs e)
    {
        await LoadSelectedTableAsync();
    }

    private async void TableComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TableComboBox.SelectedItem is not null)
        {
            await LoadSelectedTableAsync();
        }
    }

    private async void GenerateNumber_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureColumnsLoadedAsync();
            var tableName = GetSelectedTableName();
            var partNumberColumn = _databaseService.FindPartNumberColumn(_columns, _settings);
            if (partNumberColumn is null || !_inputs.TryGetValue(partNumberColumn, out var input))
            {
                MessageBox.Show(this, "当前表未发现可自动编号的列，请在设置中配置 Part Number 字段名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            input.Text = await _databaseService.GenerateNextPartNumberAsync(_settings, tableName);
            SetStatus($"已更新编号列：{partNumberColumn}");
        }
        catch (Exception ex)
        {
            ShowError("更新编号失败：" + ex.Message);
        }
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_columns.Count == 0)
            {
                MessageBox.Show(this, "请先选择表并生成表单。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var tableName = GetSelectedTableName();
            await EnsurePartNumberValueAsync(tableName);

            if (_selectedFiles.TryGetValue(FileColumnKind.Pin, out var pinFiles))
            {
                await _fileLibraryService.CopyManyToLibraryAsync(_settings, FileColumnKind.Pin, pinFiles, ResolveFileConflict);
            }

            var values = new Dictionary<string, string?>();
            foreach (var column in _columns.Where(c => !c.IsAutoIncrement && !c.IsGenerated))
            {
                var kind = GetFileColumnKind(column.Name);
                if (kind == FileColumnKind.Symbol)
                {
                    values[column.Name] = GetSelectedTargetSymbolLibraryStoredValue();
                }
                else if (kind != FileColumnKind.None && _selectedFiles.TryGetValue(kind, out var files) && files.Count > 0)
                {
                    values[column.Name] = await CopySelectedFilesForDatabaseAsync(kind, files);
                }
                else if (_inputs.TryGetValue(column.Name, out var input))
                {
                    values[column.Name] = input.Text;
                }
            }

            await _databaseService.InsertRowAsync(_settings, tableName, values, _columns);
            ClearDynamicForm();
            SetStatus("器件已写入数据库，相关文件已入库。表单已清空，请重新生成表单后继续提交。");
            MessageBox.Show(this, "提交完成。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            SetStatus("提交已取消。");
        }
        catch (Exception ex)
        {
            ShowError("提交失败：" + ex.Message);
        }
    }

    private async Task EnsurePartNumberValueAsync(string tableName)
    {
        var partNumberColumn = _databaseService.FindPartNumberColumn(_columns, _settings);
        if (partNumberColumn is not null && _inputs.TryGetValue(partNumberColumn, out var input) && string.IsNullOrWhiteSpace(input.Text))
        {
            input.Text = await _databaseService.GenerateNextPartNumberAsync(_settings, tableName);
        }
    }

    private async Task<string?> CopySelectedFilesForDatabaseAsync(FileColumnKind kind, IReadOnlyList<string> files)
    {
        var databaseSourceFiles = GetDatabaseSourceFiles(files, kind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var databaseValues = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var sourcePath = files[i];
            var result = await _fileLibraryService.CopyToLibraryAsync(_settings, kind, sourcePath, ResolveFileConflict);
            if (databaseSourceFiles.Contains(sourcePath) && result.StoredValue is not null)
            {
                databaseValues.Add(RemoveFileExtension(result.StoredValue));
            }
        }

        return databaseValues.Count == 0 ? null : string.Join(",", databaseValues);
    }

    private void BrowseFootprintFiles_Click(object sender, RoutedEventArgs e)
    {
        BrowseFiles(FileColumnKind.Footprint, FootprintFilesBox, "选择 Allegro 封装文件", "Allegro 封装文件 (*.psm;*.dra)|*.psm;*.dra|所有文件 (*.*)|*.*", true);
    }

    private void BrowseSourceSymbolLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择源符号库",
            Filter = "OrCAD 符号库 (*.olb)|*.olb|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            _sourceSymbolLibraryPath = dialog.FileName;
            SourceSymbolLibraryBox.Text = Path.GetFileName(dialog.FileName);
            SourceSymbolLibraryBox.ToolTip = dialog.FileName;
        }
    }

    private void RefreshSymbolLibraries_Click(object sender, RoutedEventArgs e)
    {
        LoadTargetSymbolLibraries();
    }

    private void TargetSymbolLibrary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedFileToForm(FileColumnKind.Symbol);
    }

    private void OpenSymbolLibraryFolders_Click(object sender, RoutedEventArgs e)
    {
        var opened = false;
        if (!string.IsNullOrWhiteSpace(_sourceSymbolLibraryPath))
        {
            OpenFolder(Path.GetDirectoryName(_sourceSymbolLibraryPath));
            opened = true;
        }

        var targetPath = GetSelectedTargetSymbolLibraryPath();
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            OpenFolder(Path.GetDirectoryName(targetPath));
            opened = true;
        }

        if (!opened)
        {
            MessageBox.Show(this, "请先选择源符号库，并在目标符号库列表中选择一个 .olb 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void LoadTargetSymbolLibraries()
    {
        TargetSymbolLibraryComboBox.ItemsSource = null;
        if (string.IsNullOrWhiteSpace(_settings.SymbolLibraryPath) || !Directory.Exists(_settings.SymbolLibraryPath))
        {
            SetStatus("目标符号库目录未设置或不存在，无法检索 .olb 文件。");
            return;
        }

        var libraries = Directory.EnumerateFiles(_settings.SymbolLibraryPath, "*.olb", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .OrderBy(fileName => fileName)
            .ToList();
        TargetSymbolLibraryComboBox.ItemsSource = libraries;
        if (libraries.Count > 0)
        {
            TargetSymbolLibraryComboBox.SelectedIndex = 0;
            SetStatus($"已在目标符号库目录中找到 {libraries.Count} 个 .olb 文件。");
        }
        else
        {
            SetStatus("目标符号库目录中没有找到 .olb 文件。");
        }
    }

    private string? GetSelectedTargetSymbolLibraryPath()
    {
        var fileName = TargetSymbolLibraryComboBox.SelectedItem?.ToString();
        return string.IsNullOrWhiteSpace(fileName) ? null : Path.Combine(_settings.SymbolLibraryPath, fileName);
    }

    private static void OpenFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = folderPath,
            UseShellExecute = true
        });
    }

    private void BrowseModel3DFile_Click(object sender, RoutedEventArgs e)
    {
        BrowseFiles(FileColumnKind.Model3D, Model3DFileBox, "选择 3D 模型文件", "3D 模型文件 (*.step;*.stp;*.igs;*.iges;*.wrl)|*.step;*.stp;*.igs;*.iges;*.wrl|所有文件 (*.*)|*.*", false);
    }

    private void BrowsePinFile_Click(object sender, RoutedEventArgs e)
    {
        BrowseFiles(FileColumnKind.Pin, PinFileBox, "选择 Allegro 焊盘/引脚文件", "Allegro 焊盘/库文件 (*.pad;*.osm;*.bsm;*.fsm;*.ssm)|*.pad;*.osm;*.bsm;*.fsm;*.ssm|所有文件 (*.*)|*.*", true);
    }

    private void BrowseFiles(FileColumnKind kind, TextBox target, string title, string filter, bool multiselect)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = multiselect
        };

        if (dialog.ShowDialog(this) == true)
        {
            _selectedFiles[kind] = dialog.FileNames.ToList();
            target.Text = string.Join("; ", dialog.FileNames.Select(Path.GetFileName));
            target.ToolTip = string.Join(Environment.NewLine, dialog.FileNames);
            ApplySelectedFileToForm(kind);
        }
    }

    private async Task LoadSelectedTableAsync()
    {
        try
        {
            var tableName = GetSelectedTableName();
            _columns = await _databaseService.GetColumnsAsync(_settings, tableName);
            BuildDynamicForm(_columns);
            ApplySelectedFilesToForm();
            await GenerateInitialPartNumberAsync(tableName);
            SetStatus($"已按表 {tableName} 生成 {_inputs.Count} 个字段。");
        }
        catch (Exception ex)
        {
            ShowError("生成表单失败：" + ex.Message);
        }
    }

    private async Task EnsureColumnsLoadedAsync()
    {
        if (_columns.Count == 0)
        {
            await LoadSelectedTableAsync();
        }
    }

    private async Task GenerateInitialPartNumberAsync(string tableName)
    {
        var partNumberColumn = _databaseService.FindPartNumberColumn(_columns, _settings);
        if (partNumberColumn is not null && _inputs.TryGetValue(partNumberColumn, out var input) && string.IsNullOrWhiteSpace(input.Text))
        {
            input.Text = await _databaseService.GenerateNextPartNumberAsync(_settings, tableName);
        }
    }

    private void BuildDynamicForm(IReadOnlyList<ColumnInfo> columns)
    {
        FormPanel.Children.Clear();
        _inputs.Clear();

        foreach (var column in columns.Where(c => !c.IsAutoIncrement && !c.IsGenerated))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelText = column.Name + (column.IsNullable ? string.Empty : " *");
            var label = new TextBlock { Text = labelText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var textBox = new TextBox { MinWidth = 260, ToolTip = column.DataType };
            Grid.SetColumn(textBox, 1);
            row.Children.Add(textBox);
            _inputs[column.Name] = textBox;

            FormPanel.Children.Add(row);
        }
    }

    private void ClearDynamicForm()
    {
        FormPanel.Children.Clear();
        _inputs.Clear();
        _columns = [];
    }

    private void ApplySelectedFilesToForm()
    {
        ApplySelectedFileToForm(FileColumnKind.Footprint);
        ApplySelectedFileToForm(FileColumnKind.Symbol);
        ApplySelectedFileToForm(FileColumnKind.Model3D);
    }

    private void ApplySelectedFileToForm(FileColumnKind kind)
    {
        if (kind == FileColumnKind.Symbol)
        {
            ApplySelectedSymbolLibraryToForm();
            return;
        }

        if (kind == FileColumnKind.Pin || !_selectedFiles.TryGetValue(kind, out var files) || files.Count == 0)
        {
            return;
        }

        var columnName = _columns.Select(c => c.Name).FirstOrDefault(name => GetFileColumnKind(name) == kind);
        if (columnName is null || !_inputs.TryGetValue(columnName, out var input))
        {
            return;
        }

        var databaseFiles = GetDatabaseSourceFiles(files, kind);
        input.Text = string.Join(",", databaseFiles.Select(GetStoredPreviewValue));
        input.ToolTip = string.Join(Environment.NewLine, databaseFiles);
    }

    private void ApplySelectedSymbolLibraryToForm()
    {
        var targetPath = GetSelectedTargetSymbolLibraryPath();
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        var columnName = _columns.Select(c => c.Name).FirstOrDefault(name => GetFileColumnKind(name) == FileColumnKind.Symbol);
        if (columnName is null || !_inputs.TryGetValue(columnName, out var input))
        {
            return;
        }

        input.Text = GetSelectedTargetSymbolLibraryStoredValue() ?? string.Empty;
        input.ToolTip = targetPath;
    }

    private string? GetSelectedTargetSymbolLibraryStoredValue()
    {
        var targetPath = GetSelectedTargetSymbolLibraryPath();
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        var storedValue = _settings.StoreRelativeLibraryFileName ? Path.GetFileName(targetPath) : targetPath;
        return RemoveFileExtension(storedValue);
    }

    private static IReadOnlyList<string> GetDatabaseSourceFiles(IReadOnlyList<string> files, FileColumnKind kind)
    {
        if (kind == FileColumnKind.Footprint)
        {
            var psmFiles = files.Where(path => string.Equals(Path.GetExtension(path), ".psm", StringComparison.OrdinalIgnoreCase)).ToList();
            if (psmFiles.Count > 0)
            {
                return psmFiles;
            }
        }

        return files.Count == 0 ? [] : [files[0]];
    }

    private string GetStoredPreviewValue(string sourcePath)
    {
        var storedValue = _settings.StoreRelativeLibraryFileName ? Path.GetFileName(sourcePath) : sourcePath;
        return RemoveFileExtension(storedValue);
    }

    private static string RemoveFileExtension(string value)
    {
        return Path.ChangeExtension(value, null) ?? value;
    }

    private FileColumnKind GetFileColumnKind(string columnName)
    {
        if (MatchesConfiguredColumn(columnName, _settings.FootprintColumnNames))
        {
            return FileColumnKind.Footprint;
        }

        if (MatchesConfiguredColumn(columnName, _settings.SymbolColumnNames))
        {
            return FileColumnKind.Symbol;
        }

        if (MatchesConfiguredColumn(columnName, _settings.Model3DColumnNames))
        {
            return FileColumnKind.Model3D;
        }

        return FileColumnKind.None;
    }

    private static bool MatchesConfiguredColumn(string columnName, IEnumerable<string>? candidates)
    {
        return (candidates ?? []).Any(candidate => string.Equals(NormalizeColumnName(candidate), NormalizeColumnName(columnName), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeColumnName(string value)
    {
        return value.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
    }

    private string GetSelectedTableName()
    {
        return TableComboBox.SelectedItem?.ToString() ?? throw new InvalidOperationException("请先选择数据库表。");
    }

    private FileConflictAction ResolveFileConflict(string destinationPath)
    {
        var dialog = new Window
        {
            Title = "文件已存在",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "目标文件已存在：" + destinationPath, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "请选择处理方式。", Margin = new Thickness(0, 0, 0, 12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        AddConflictButton(buttons, dialog, "覆盖", FileConflictAction.Overwrite);
        AddConflictButton(buttons, dialog, "自动重命名", FileConflictAction.Rename);
        AddConflictButton(buttons, dialog, "跳过", FileConflictAction.Skip);
        AddConflictButton(buttons, dialog, "取消", FileConflictAction.Cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
        return dialog.Tag is FileConflictAction action ? action : FileConflictAction.Cancel;
    }

    private static void AddConflictButton(Panel panel, Window dialog, string text, FileConflictAction action)
    {
        var button = new Button { Content = text, MinWidth = 86, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) =>
        {
            dialog.Tag = action;
            dialog.DialogResult = true;
        };
        panel.Children.Add(button);
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void ShowError(string message)
    {
        SetStatus(message);
        MessageBox.Show(this, message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
