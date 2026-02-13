# WowProxy：项目如何打包成 exe（Windows）

本文说明如何把本仓库的 WPF 项目 **WowProxy.App** 打包成可直接运行的 `WowProxy.App.exe`，并输出到 `dist` 下的一个新文件夹里。

## 适用范围

- 项目类型：.NET（`net8.0-windows`）+ WPF
- 目标平台：Windows 10/11 x64
- 输出形态：自包含（SelfContained）+ 单文件（PublishSingleFile）

## 产物位置与命名习惯

仓库约定把发布产物放在：

- `dist\WowProxy\`（默认脚本输出）
- 也可以放在类似下面的目录（与你现有目录一致的风格）：
  - `dist\WowProxy-cn\`
  - `dist\WowProxy-next\`
  - `dist\WowProxy-next2\`
  - `dist\WowProxy-next3\`（示例：新打包目录）

每个发布目录内主要文件通常是：

- `WowProxy.App.exe`（可执行文件）
- 若干 `.pdb`（调试符号，可保留用于崩溃定位，也可删除以减小体积）

## 方式一：使用仓库自带发布脚本（推荐）

仓库根目录已有 `publish.ps1`，会把 `WowProxy.App` 以 **Release / win-x64 / 自包含 / 单文件** 的方式发布到：

- `dist\WowProxy\`

在仓库根目录执行：

```powershell
.\publish.ps1
```

执行成功后，检查：

```powershell
Test-Path .\dist\WowProxy\WowProxy.App.exe
```

## 方式二：发布到“一个新的 dist 子目录”（可自定义目录名）

当你不想覆盖 `dist\WowProxy\`，而是要输出到一个新的目录（例如 `dist\WowProxy-next3\`），可以直接用 `dotnet publish` 指定输出目录 `-o`。

说明：本仓库带了本地 .NET SDK（`.dotnet\dotnet.exe`），不依赖你机器全局安装的 dotnet。

在仓库根目录执行：

```powershell
$dotnet = Join-Path $PWD ".dotnet\dotnet.exe"
$project = Join-Path $PWD "src\WowProxy.App\WowProxy.App.csproj"
$outDir = Join-Path $PWD "dist\WowProxy-next3"

New-Item -ItemType Directory -Force $outDir | Out-Null

& $dotnet publish $project `
  -c Release `
  -r win-x64 `
  -o $outDir `
  /p:SelfContained=true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:PublishTrimmed=false `
  -- /m:1 /nr:false /p:BuildInParallel=false
```

发布完成后验证：

```powershell
Test-Path .\dist\WowProxy-next3\WowProxy.App.exe
```

如果你想用别的目录名，把 `WowProxy-next3` 换成你需要的即可。

## 参数解释（你只需要记住结论）

- `-c Release`：发布 Release 版本（体积更小、性能更好）
- `-r win-x64`：目标运行时为 Windows x64
- `/p:SelfContained=true`：自包含，用户机器无需安装 .NET Runtime
- `/p:PublishSingleFile=true`：尽量合并为单个 exe（会同时输出 pdb 等文件）
- `/p:IncludeNativeLibrariesForSelfExtract=true`：包含本地依赖库并按需自解压
- `/p:PublishTrimmed=false`：关闭裁剪，减少 WPF/反射相关风险

## 常见问题

### 1）PowerShell 里不要用 `&&`

本地 PowerShell（5.x）对 `&&` 支持不一致，建议用分号分隔命令：

```powershell
git status -sb; git log -1 --oneline --decorate
```

### 2）双击 exe 没反应/被拦截

- 确认系统是 64 位 Windows 10/11（`win-x64` 产物不支持 32 位系统）
- Windows SmartScreen 提示时：点“更多信息”→“仍要运行”
- 若仍异常，查看崩溃日志：`%LocalAppData%\WowProxy\logs\crash-*.log`

### 3）想“重打包但不覆盖旧目录”

直接用“方式二”，把输出目录改成新的 `dist\WowProxy-xxx\` 即可；旧目录不会被影响。

