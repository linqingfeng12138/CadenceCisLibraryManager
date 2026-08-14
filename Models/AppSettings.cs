namespace CadenceCisLibraryManager.Models;

public sealed class AppSettings
{
    public string Server { get; set; } = "localhost";

    public uint Port { get; set; } = 3306;

    public string Database { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FootprintLibraryPath { get; set; } = string.Empty;

    public string SymbolLibraryPath { get; set; } = string.Empty;

    public string Model3DLibraryPath { get; set; } = string.Empty;

    public string PinLibraryPath { get; set; } = string.Empty;

    public bool StoreRelativeLibraryFileName { get; set; } = true;

    public int PartNumberIdWidth { get; set; } = 5;

    public List<string> PartNumberColumnNames { get; set; } = ["Part Number", "PartNumber", "Part_No", "PN", "编号", "料号"];

    public List<string> FootprintColumnNames { get; set; } = ["PCB Footprint", "Footprint", "Package", "封装"];

    public List<string> SymbolColumnNames { get; set; } = ["Schematic Symbol", "Symbol", "SchSymbol", "符号", "原理图符号"];

    public List<string> Model3DColumnNames { get; set; } = ["3D Model", "Model3D", "StepModel", "模型", "三维模型"];

    public Dictionary<string, string> TablePartNumberPrefixes { get; set; } = [];
}
