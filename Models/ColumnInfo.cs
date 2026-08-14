namespace CadenceCisLibraryManager.Models;

public sealed class ColumnInfo
{
    public required string Name { get; init; }

    public required string DataType { get; init; }

    public bool IsNullable { get; init; }

    public string? DefaultValue { get; init; }

    public bool IsAutoIncrement { get; init; }

    public bool IsPrimaryKey { get; init; }

    public bool IsGenerated { get; init; }
}
