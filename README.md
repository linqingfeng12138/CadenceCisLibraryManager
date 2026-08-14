# OrCAD CIS 库管理器

OrCAD CIS 库管理器是一个面向 OrCAD CIS / Cadence Allegro 库管理流程的 Windows 桌面工具，用于辅助器件信息入库、库文件归档、Part Number 自动生成以及符号/封装/3D/焊盘文件路径管理。

## 功能特性

- MariaDB 数据库连接与表结构读取。
- 根据数据库表动态生成器件录入表单。
- 支持按表配置 Part Number 前缀，并基于自增 ID 生成编号。
- 支持封装文件入库：
  - `.psm`：Allegro package / part symbol 库文件。
  - `.dra`：Symbols 或 Pad 的可编辑源文件。
  - PCB Footprint 字段可根据多个 `.psm` 文件自动填充，使用英文逗号分隔且不带文件后缀。
- 支持 3D 模型文件入库，数据库字段保存时不带文件后缀。
- 支持焊盘/引脚相关文件上传：`.pad`、`.osm`、`.bsm`、`.fsm`、`.ssm`。
- 支持符号库辅助选择：
  - 选择源 `.olb` 符号库。
  - 扫描目标符号库目录中的 `.olb` 文件。
  - 可同时打开源符号库与目标符号库所在文件夹。
- 独立设置窗口：
  - MariaDB 连接信息。
  - 封装、符号、3D、焊盘/引脚库路径。
  - 数据库字段名候选配置。
  - 表级 Part Number 前缀表格配置。
- 关于页面显示软件名称、版本、作者和开源组件信息。

## 运行环境

- Windows
- .NET 10 Desktop Runtime
- MariaDB 或兼容 MySQL 协议的数据库

## 从源码构建

需要安装 .NET 10 SDK。

```powershell
dotnet restore
dotnet build -c Release
```

## 发布打包

```powershell
dotnet publish CadenceCisLibraryManager.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\publish\win-x64
Compress-Archive -Path .\artifacts\publish\win-x64\* -DestinationPath .\artifacts\CadenceCisLibraryManager-v0.1.0-win-x64.zip -Force
```

## GitHub 首次开源发布参考命令

如果本机已安装 Git 与 GitHub CLI，并且已执行 `gh auth login`，可在仓库根目录执行：

```powershell
git init
git add .
git commit -m "Initial open source release"
gh repo create CadenceCisLibraryManager --public --source . --remote origin --push
git tag v0.1.0
git push origin v0.1.0
gh release create v0.1.0 .\artifacts\CadenceCisLibraryManager-v0.1.0-win-x64.zip --title "v0.1.0" --notes "首个开源发布版本。"
```

## 开源组件与许可证

本项目使用以下开源组件或平台技术：

- .NET / WPF：Microsoft 提供的应用程序框架和桌面 UI 技术。
- MySqlConnector 2.4.0：用于连接 MariaDB/MySQL 数据库的开源 .NET 数据库驱动，许可证为 MIT License。

各组件版权与许可证归其原作者或维护者所有。

## 许可证

本项目使用 MIT License 开源，详见 [LICENSE](LICENSE)。

## 作者

linqingfeng
