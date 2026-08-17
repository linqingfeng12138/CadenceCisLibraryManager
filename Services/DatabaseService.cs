using CadenceCisLibraryManager.Models;
using MySqlConnector;

namespace CadenceCisLibraryManager.Services;

public sealed class DatabaseService
{
    public async Task<IReadOnlyList<string>> GetTablesAsync(AppSettings settings)
    {
        await using var connection = await OpenConnectionAsync(settings);
        const string sql = "select table_name from information_schema.tables where table_schema = database() and table_type = 'BASE TABLE' order by table_name";
        await using var command = new MySqlCommand(sql, connection);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(AppSettings settings, string tableName)
    {
        await using var connection = await OpenConnectionAsync(settings);
        const string sql = @"select c.column_name, c.data_type, c.is_nullable, c.column_default, c.extra, case when k.column_name is null then 0 else 1 end as is_primary_key from information_schema.columns c left join information_schema.key_column_usage k on k.table_schema = c.table_schema and k.table_name = c.table_name and k.column_name = c.column_name and k.constraint_name = 'PRIMARY' where c.table_schema = database() and c.table_name = @tableName order by c.ordinal_position";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        var columns = new List<ColumnInfo>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var extra = reader.GetString(4);
            columns.Add(new ColumnInfo
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                DefaultValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsAutoIncrement = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                IsGenerated = extra.Contains("generated", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey = reader.GetInt32(5) == 1
            });
        }

        return columns;
    }

    public async Task<bool> TestConnectionAsync(AppSettings settings)
    {
        await using var connection = await OpenConnectionAsync(settings);
        return connection.State == System.Data.ConnectionState.Open;
    }

    public string? FindPartNumberColumn(IEnumerable<ColumnInfo> columns, AppSettings settings)
    {
        return columns.Select(c => c.Name).FirstOrDefault(name => MatchesConfiguredColumn(name, settings.PartNumberColumnNames));
    }

    public async Task<string> GenerateNextPartNumberAsync(AppSettings settings, string tableName)
    {
        await using var connection = await OpenConnectionAsync(settings);
        const string sql = "select auto_increment from information_schema.tables where table_schema = database() and table_name = @tableName";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@tableName", tableName);
        var value = await command.ExecuteScalarAsync();
        if (value is null || value == DBNull.Value)
        {
            throw new InvalidOperationException($"表 {tableName} 没有可用的 AUTO_INCREMENT ID，无法生成 Part Number。");
        }

        var nextId = Convert.ToInt64(value);
        var width = Math.Max(1, settings.PartNumberIdWidth);
        return GetTablePrefix(settings, tableName) + nextId.ToString(new string('0', width));
    }

    private static bool MatchesConfiguredColumn(string columnName, IEnumerable<string>? candidates)
    {
        return (candidates ?? []).Any(candidate => string.Equals(NormalizeName(candidate), NormalizeName(columnName), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string value)
    {
        return value.Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
    }

    private static string GetTablePrefix(AppSettings settings, string tableName)
    {
        var configured = settings.TablePartNumberPrefixes.FirstOrDefault(pair => string.Equals(pair.Key, tableName, StringComparison.OrdinalIgnoreCase));
        return configured.Value ?? string.Empty;
    }

    public async Task InsertRowAsync(AppSettings settings, string tableName, IReadOnlyDictionary<string, string?> values, IReadOnlyList<ColumnInfo> columns)
    {
        var writableColumns = columns.Where(c => !c.IsAutoIncrement && !c.IsGenerated && values.ContainsKey(c.Name)).ToList();
        if (writableColumns.Count == 0)
        {
            throw new InvalidOperationException("没有可写入的列。");
        }

        await using var connection = await OpenConnectionAsync(settings);
        var columnList = string.Join(", ", writableColumns.Select(c => EscapeIdentifier(c.Name)));
        var parameterList = string.Join(", ", writableColumns.Select((_, index) => "@p" + index));
        var sql = $"insert into {EscapeIdentifier(tableName)} ({columnList}) values ({parameterList})";
        await using var command = new MySqlCommand(sql, connection);
        for (var i = 0; i < writableColumns.Count; i++)
        {
            var column = writableColumns[i];
            var value = values[column.Name];
            command.Parameters.AddWithValue("@p" + i, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyDictionary<string, string?>?> GetSingleRowByColumnAsync(AppSettings settings, string tableName, string filterColumn, string filterValue, IReadOnlyList<ColumnInfo> columns)
    {
        await using var connection = await OpenConnectionAsync(settings);
        var selectColumns = string.Join(", ", columns.Select(c => EscapeIdentifier(c.Name)));
        var sql = $"select {selectColumns} from {EscapeIdentifier(tableName)} where {EscapeIdentifier(filterColumn)} = @filterValue limit 1";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@filterValue", filterValue);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            values[columns[i].Name] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
        }

        return values;
    }

    public async Task UpdateRowAsync(AppSettings settings, string tableName, IReadOnlyDictionary<string, string?> values, IReadOnlyList<ColumnInfo> columns, string keyColumnName, string? keyValue)
    {
        if (string.IsNullOrWhiteSpace(keyValue))
        {
            throw new InvalidOperationException("主键值为空，无法更新记录。");
        }

        var writableColumns = columns.Where(c => !c.IsAutoIncrement && !c.IsGenerated && !c.IsPrimaryKey && values.ContainsKey(c.Name)).ToList();
        if (writableColumns.Count == 0)
        {
            throw new InvalidOperationException("没有可更新的列。");
        }

        await using var connection = await OpenConnectionAsync(settings);
        var setClause = string.Join(", ", writableColumns.Select((c, index) => $"{EscapeIdentifier(c.Name)} = @p{index}"));
        var sql = $"update {EscapeIdentifier(tableName)} set {setClause} where {EscapeIdentifier(keyColumnName)} = @keyValue";
        await using var command = new MySqlCommand(sql, connection);
        for (var i = 0; i < writableColumns.Count; i++)
        {
            var column = writableColumns[i];
            var value = values[column.Name];
            command.Parameters.AddWithValue("@p" + i, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);
        }

        command.Parameters.AddWithValue("@keyValue", keyValue);
        var affected = await command.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            throw new InvalidOperationException("未找到需要更新的记录，可能已被删除。");
        }
    }

    private static async Task<MySqlConnection> OpenConnectionAsync(AppSettings settings)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = settings.Server,
            Port = settings.Port,
            Database = settings.Database,
            UserID = settings.UserId,
            Password = settings.Password,
            AllowUserVariables = true
        };

        var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string EscapeIdentifier(string identifier)
    {
        return "`" + identifier.Replace("`", "``") + "`";
    }
}

