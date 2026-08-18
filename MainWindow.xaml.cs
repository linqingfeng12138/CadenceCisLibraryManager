using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using CadenceCisLibraryManager.Models;
using CadenceCisLibraryManager.Services;

namespace CadenceCisLibraryManager;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly DatabaseService _databaseService = new();
    private readonly FileLibraryService _fileLibraryService = new();
    private readonly Dictionary<string, Control> _inputs = [];
    private readonly Dictionary<FileColumnKind, List<string>> _selectedFiles = [];
    private string? _sourceSymbolLibraryPath;
    private IReadOnlyList<ColumnInfo> _columns = [];
    private string? _loadedTableName;
    private AppSettings _settings = new();
    private string? _editingKeyColumn;
    private string? _editingKeyValue;

    private sealed record RecordFileItem(string Title, string Path, bool Exists);

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var loadResult = await _settingsService.LoadWithStatusAsync();
        _settings = loadResult.Settings;
        LoadTargetSymbolLibraries();
        if (loadResult.PasswordDecryptionFailed)
        {
            MessageBox.Show(this, "已保存的数据库密码无法在当前 Windows 用户环境下解密，可能是配置文件来自其他用户或电脑。请打开“设置”重新输入数据库密码并保存。", "密码解密失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SetStatus("设置已加载。请先读取表并生成表单。");
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
            else
            {
                ClearDynamicForm();
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
            if (partNumberColumn is null || !_inputs.ContainsKey(partNumberColumn))
            {
                MessageBox.Show(this, "当前表未发现可自动编号的列，请在设置中配置 Part Number 字段名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetInputValue(partNumberColumn, await _databaseService.GenerateNextPartNumberAsync(_settings, tableName));
            SetStatus($"已更新编号列：{partNumberColumn}");
        }
        catch (Exception ex)
        {
            ShowError("更新编号失败：" + ex.Message);
        }
    }

    private async void FindRecord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var partNumberValue = PartNumberSearchBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(partNumberValue))
            {
                MessageBox.Show(this, "请输入要查找的 Part Number。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var resolvedTable = ResolveTableNameByPartNumber(partNumberValue);
            if (string.IsNullOrWhiteSpace(resolvedTable))
            {
                MessageBox.Show(this, "无法从 Part Number 前缀识别对应表，请检查设置中的表前缀。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await SelectTableAsync(resolvedTable);
            await EnsureColumnsLoadedAsync();

            var partNumberColumn = _databaseService.FindPartNumberColumn(_columns, _settings);
            if (partNumberColumn is null)
            {
                MessageBox.Show(this, "当前表未识别到 Part Number 字段，请在设置中配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var tableName = GetSelectedTableName();
            var row = await _databaseService.GetSingleRowByColumnAsync(_settings, tableName, partNumberColumn, partNumberValue, _columns);
            if (row is null)
            {
                ResetEditMode();
                MessageBox.Show(this, "未找到对应器件记录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var keyColumn = _columns.FirstOrDefault(c => c.IsPrimaryKey)?.Name;
            if (keyColumn is null || !row.TryGetValue(keyColumn, out var keyValue) || string.IsNullOrWhiteSpace(keyValue))
            {
                throw new InvalidOperationException("当前表缺少可用主键，无法进入编辑模式。");
            }

            foreach (var input in _inputs)
            {
                if (row.TryGetValue(input.Key, out var value))
                {
                    SetInputValue(input.Key, value ?? string.Empty);
                }
            }

            _editingKeyColumn = keyColumn;
            _editingKeyValue = keyValue;
            ShowRecordFiles(row);
            SetStatus($"已加载记录：{partNumberValue}（编辑模式）");
        }
        catch (Exception ex)
        {
            ShowError("查找记录失败：" + ex.Message);
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
                    values[column.Name] = GetSymbolDatabaseValue(column.Name);
                }
                else if (kind != FileColumnKind.None && _selectedFiles.TryGetValue(kind, out var files) && files.Count > 0)
                {
                    values[column.Name] = await CopySelectedFilesForDatabaseAsync(kind, files);
                }
                else if (_inputs.ContainsKey(column.Name))
                {
                    values[column.Name] = GetInputValue(column.Name);
                }
            }

            if (!string.IsNullOrWhiteSpace(_editingKeyColumn) && !string.IsNullOrWhiteSpace(_editingKeyValue))
            {
                await _databaseService.UpdateRowAsync(_settings, tableName, values, _columns, _editingKeyColumn, _editingKeyValue);
                MessageBox.Show(this, "更新完成。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                SetStatus("器件记录已更新。表单已清空，请重新生成表单后继续操作。");
            }
            else
            {
                await _databaseService.InsertRowAsync(_settings, tableName, values, _columns);
                MessageBox.Show(this, "提交完成。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                SetStatus("器件已写入数据库，相关文件已入库。表单已清空，请重新生成表单后继续提交。");
            }

            ClearDynamicForm();
            ClearSelectedFiles();
            ResetEditMode();
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
        if (partNumberColumn is not null && string.IsNullOrWhiteSpace(GetInputValue(partNumberColumn)))
        {
            SetInputValue(partNumberColumn, await _databaseService.GenerateNextPartNumberAsync(_settings, tableName));
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
            _loadedTableName = tableName;
            ResetEditMode();
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
        var selectedTable = GetSelectedTableName();
        if (_columns.Count == 0 || !string.Equals(_loadedTableName, selectedTable, StringComparison.OrdinalIgnoreCase))
        {
            await LoadSelectedTableAsync();
        }
    }

    private async Task GenerateInitialPartNumberAsync(string tableName)
    {
        var partNumberColumn = _databaseService.FindPartNumberColumn(_columns, _settings);
        if (partNumberColumn is not null && _inputs.ContainsKey(partNumberColumn) && string.IsNullOrWhiteSpace(GetInputValue(partNumberColumn)))
        {
            SetInputValue(partNumberColumn, await _databaseService.GenerateNextPartNumberAsync(_settings, tableName));
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
            if (ShouldEnableSuggestions(column.Name))
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(textBox, 1);
                row.Children.Add(textBox);

                var button = new Button
                {
                    Content = new TextBlock
                    {
                        Text = "\uE712",
                        Style = (Style)FindResource("SymbolTextStyle"),
                        Margin = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    Width = 36,
                    Height = 28,
                    Margin = new Thickness(8, 0, 0, 0),
                    Tag = column.Name,
                    ToolTip = "浏览候选值"
                };
                button.Click += BrowseCandidates_Click;
                Grid.SetColumn(button, 2);
                row.Children.Add(button);
            }
            else
            {
                Grid.SetColumn(textBox, 1);
                row.Children.Add(textBox);
            }

            _inputs[column.Name] = textBox;

            FormPanel.Children.Add(row);
        }
    }

    private bool ShouldEnableSuggestions(string columnName)
    {
        return MatchesConfiguredColumn(columnName, _settings.FootprintColumnNames)
            || MatchesConfiguredColumn(columnName, _settings.SymbolColumnNames)
            || MatchesConfiguredColumn(columnName, new[] { "器件类型", "Device Type", "Part Type", "PartType", "Type", "Category", "器件类别" });
    }

    private async void BrowseCandidates_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string columnName })
        {
            return;
        }

        try
        {
            var tableName = GetSelectedTableName();
            var values = await _databaseService.GetDistinctColumnValuesAsync(_settings, tableName, columnName);
            if (MatchesConfiguredColumn(columnName, _settings.FootprintColumnNames))
            {
                values = values
                    .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var selectedValues = GetInputValue(columnName)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var dialog = new CandidateSelectionWindow($"选择 {columnName}", values, selectedValues)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                SetInputValue(columnName, string.Join(",", dialog.SelectedValues));
            }
        }
        catch (Exception ex)
        {
            ShowError("读取候选列表失败：" + ex.Message);
        }
    }

    private string GetInputValue(string columnName)
    {
        return _inputs.TryGetValue(columnName, out var control) && control is TextBox textBox
            ? textBox.Text
            : string.Empty;
    }

    private void SetInputValue(string columnName, string value)
    {
        if (_inputs.TryGetValue(columnName, out var control) && control is TextBox textBox)
        {
            textBox.Text = value;
        }
    }

    private void ClearDynamicForm()
    {
        FormPanel.Children.Clear();
        _inputs.Clear();
        _columns = [];
        _loadedTableName = null;
        ClearRecordFiles();
    }

    private void ResetEditMode()
    {
        _editingKeyColumn = null;
        _editingKeyValue = null;
        ClearRecordFiles();
    }

    private void ShowRecordFiles(IReadOnlyDictionary<string, string?> row)
    {
        var symbolItems = GetRecordFileItems(row, FileColumnKind.Symbol, BuildSymbolFileItems).ToList();
        var footprintItems = GetRecordFileItems(row, FileColumnKind.Footprint, BuildFootprintFileItems).ToList();
        var model3DItems = GetRecordFileItems(row, FileColumnKind.Model3D, BuildModel3DFileItems).ToList();

        RenderRecordFileGroup(SymbolFilesGroupBox, SymbolFilesPanel, symbolItems);
        RenderRecordFileGroup(FootprintFilesGroupBox, FootprintFilesPanel, footprintItems);
        RenderRecordFileGroup(Model3DFilesGroupBox, Model3DFilesPanel, model3DItems);
    }

    private IEnumerable<RecordFileItem> GetRecordFileItems(
        IReadOnlyDictionary<string, string?> row,
        FileColumnKind kind,
        Func<string, IEnumerable<RecordFileItem>> builder)
    {
        foreach (var column in _columns)
        {
            if (GetFileColumnKind(column.Name) != kind || !row.TryGetValue(column.Name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var item in builder(value))
            {
                yield return item;
            }
        }
    }

    private void RenderRecordFileGroup(GroupBox groupBox, Panel panel, IReadOnlyList<RecordFileItem> items)
    {
        panel.Children.Clear();
        groupBox.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items)
        {
            panel.Children.Add(CreateRecordFileButton(item));
        }
    }

    private Button CreateRecordFileButton(RecordFileItem item)
    {
        var button = new Button
        {
            Content = item.Exists ? item.Title : $"{item.Title}（未找到）",
            Tag = item,
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 6, 10, 6),
            ToolTip = item.Path
        };

        if (!item.Exists)
        {
            button.Foreground = Brushes.Firebrick;
        }

        button.PreviewMouseLeftButtonDown += RecordFileButton_PreviewMouseLeftButtonDown;
        return button;
    }

    private IEnumerable<RecordFileItem> BuildSymbolFileItems(string value)
    {
        var normalized = value.Replace('/', '\\');
        var libraryPart = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(libraryPart) || string.IsNullOrWhiteSpace(_settings.SymbolLibraryPath))
        {
            yield break;
        }

        var fileName = libraryPart.EndsWith(".olb", StringComparison.OrdinalIgnoreCase) ? libraryPart : libraryPart + ".olb";
        var path = Path.Combine(_settings.SymbolLibraryPath, fileName);
        yield return new RecordFileItem($"符号库：{fileName}", path, File.Exists(path));
    }

    private IEnumerable<RecordFileItem> BuildFootprintFileItems(string value)
    {
        if (string.IsNullOrWhiteSpace(_settings.FootprintLibraryPath))
        {
            yield break;
        }

        var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in names)
        {
            foreach (var extension in new[] { ".psm", ".dra" })
            {
                var path = Path.Combine(_settings.FootprintLibraryPath, name + extension);
                yield return new RecordFileItem($"封装：{name}{extension}", path, File.Exists(path));
            }
        }
    }

    private IEnumerable<RecordFileItem> BuildModel3DFileItems(string value)
    {
        if (File.Exists(value))
        {
            yield return new RecordFileItem($"3D：{Path.GetFileName(value)}", value, true);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(_settings.Model3DLibraryPath) || !Directory.Exists(_settings.Model3DLibraryPath))
        {
            yield break;
        }

        var baseNames = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var baseName in baseNames)
        {
            var matchedFiles = Directory.EnumerateFiles(_settings.Model3DLibraryPath, baseName + ".*", SearchOption.TopDirectoryOnly).ToList();
            if (matchedFiles.Count == 0)
            {
                yield return new RecordFileItem($"3D：{baseName}", Path.Combine(_settings.Model3DLibraryPath, baseName), false);
                continue;
            }

            foreach (var file in matchedFiles)
            {
                yield return new RecordFileItem($"3D：{Path.GetFileName(file)}", file, true);
            }
        }
    }

    private void ClearSelectedFiles()
    {
        _selectedFiles.Clear();
        _sourceSymbolLibraryPath = null;
        FootprintFilesBox.Clear();
        FootprintFilesBox.ToolTip = null;
        SourceSymbolLibraryBox.Clear();
        SourceSymbolLibraryBox.ToolTip = null;
        Model3DFileBox.Clear();
        Model3DFileBox.ToolTip = null;
        PinFileBox.Clear();
        PinFileBox.ToolTip = null;
    }

    private void ClearRecordFiles()
    {
        SymbolFilesPanel.Children.Clear();
        FootprintFilesPanel.Children.Clear();
        Model3DFilesPanel.Children.Clear();
        SymbolFilesGroupBox.Visibility = Visibility.Collapsed;
        FootprintFilesGroupBox.Visibility = Visibility.Collapsed;
        Model3DFilesGroupBox.Visibility = Visibility.Collapsed;
    }

    private void RecordFileButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: RecordFileItem item })
        {
            return;
        }

        try
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                OpenRecordFilePath(item.Path);
                e.Handled = true;
                return;
            }

            if (e.ClickCount >= 2)
            {
                OpenRecordFile(item.Path);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            ShowError("打开文件失败：" + ex.Message);
        }
    }

    private void OpenRecordFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            OpenRecordFilePath(path);
            return;
        }

        MessageBox.Show(this, "找不到对应文件或目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenRecordFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = File.Exists(path) ? Path.GetDirectoryName(path) : Path.GetDirectoryName(path) ?? path;
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            var arguments = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{directory}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
            return;
        }

        try
        {
            MessageBox.Show(this, "找不到对应文件或目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError("打开文件失败：" + ex.Message);
        }
    }

    private void ApplySelectedFilesToForm()
    {
        ApplySelectedFileToForm(FileColumnKind.Footprint);
        ApplySelectedFileToForm(FileColumnKind.Model3D);
    }

    private void ApplySelectedFileToForm(FileColumnKind kind)
    {
        if (kind == FileColumnKind.Pin || !_selectedFiles.TryGetValue(kind, out var files) || files.Count == 0)
        {
            return;
        }

        var columnName = _columns.Select(c => c.Name).FirstOrDefault(name => GetFileColumnKind(name) == kind);
        if (columnName is null || !_inputs.ContainsKey(columnName))
        {
            return;
        }

        var databaseFiles = GetDatabaseSourceFiles(files, kind);
        SetInputValue(columnName, string.Join(",", databaseFiles.Select(GetStoredPreviewValue)));

        if (_inputs.TryGetValue(columnName, out var input) && input is TextBox textBox)
        {
            textBox.ToolTip = string.Join(Environment.NewLine, databaseFiles);
        }
    }

    private string? GetSymbolDatabaseValue(string columnName)
    {
        if (!_inputs.ContainsKey(columnName))
        {
            return null;
        }

        var symbolName = GetInputValue(columnName).Trim();
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return null;
        }

        if (symbolName.Contains('\\') || symbolName.Contains('/'))
        {
            return symbolName.Replace('/', '\\');
        }

        var targetPath = GetSelectedTargetSymbolLibraryPath();
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return symbolName;
        }

        var libraryName = RemoveFileExtension(Path.GetFileName(targetPath));
        return string.IsNullOrWhiteSpace(libraryName) ? symbolName : $"{libraryName}\\{symbolName}";
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

    private string? ResolveTableNameByPartNumber(string partNumber)
    {
        var matched = _settings.TablePartNumberPrefixes
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .OrderByDescending(pair => pair.Value.Length)
            .FirstOrDefault(pair => partNumber.StartsWith(pair.Value, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(matched.Key) ? null : matched.Key;
    }

    private async Task SelectTableAsync(string tableName)
    {
        if (TableComboBox.ItemsSource is null)
        {
            await RefreshTablesAsync();
        }

        if (TableComboBox.ItemsSource is IEnumerable<string> tables)
        {
            var matched = tables.FirstOrDefault(item => string.Equals(item, tableName, StringComparison.OrdinalIgnoreCase));
            if (matched is null)
            {
                throw new InvalidOperationException($"在当前数据库中找不到表：{tableName}");
            }

            if (!string.Equals(TableComboBox.SelectedItem?.ToString(), matched, StringComparison.OrdinalIgnoreCase))
            {
                TableComboBox.SelectedItem = matched;
            }
        }
    }

    private async Task RefreshTablesAsync()
    {
        var tables = await _databaseService.GetTablesAsync(_settings);
        TableComboBox.ItemsSource = tables;
        if (tables.Count > 0)
        {
            TableComboBox.SelectedIndex = 0;
        }
        else
        {
            ClearDynamicForm();
        }
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
