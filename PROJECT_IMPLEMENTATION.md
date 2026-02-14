# WowProxy 项目技术实现详解

本文档详细记录了 WowProxy 项目的核心功能模块、代码位置以及关键实现流程。

## 1. 核心架构概述

WowProxy 是一个基于 **WPF (Windows Presentation Foundation)** 和 **sing-box** 内核的 Windows 代理客户端。它采用 MVVM (Model-View-ViewModel) 架构模式。

*   **UI 层 (View)**: `src\WowProxy.App\MainWindow.xaml`
*   **逻辑层 (ViewModel)**: `src\WowProxy.App\ViewModels\`
*   **模型层 (Model)**: `src\WowProxy.App\Models\` 及 `src\WowProxy.Core.Abstractions\Models\`
*   **基础设施 (Infrastructure)**: `src\WowProxy.Infrastructure\` (API 客户端、系统代理设置)
*   **内核适配 (Core)**: `src\WowProxy.Core.SingBox\` (配置生成、进程管理)

---

## 2. 功能实现详解

### 2.1 代理内核启动与配置生成
**功能**: 根据用户界面设置，生成 sing-box 的 `config.json` 并启动内核进程。

*   **核心代码**:
    *   [SingBoxConfigFactoryV2.cs](src/WowProxy.Core.SingBox/SingBoxConfigFactoryV2.cs): 负责生成 JSON 配置。
    *   [SingBoxCoreAdapter.cs](src/WowProxy.Core.SingBox/SingBoxCoreAdapter.cs): 负责管理 `sing-box.exe` 进程的启动、停止和日志捕获。
    *   [MainViewModel.cs](src/WowProxy.App/MainViewModel.cs) (StartAsync 方法): 协调启动流程。

*   **实现流程**:
    1.  用户在 UI 设置端口、开启 TUN、选择节点。
    2.  `MainViewModel` 收集这些参数，创建 `AppSettings` 对象。
    3.  `SingBoxConfigFactoryV2.Build()` 接收设置，构建 JSON 对象：
        *   **Inbounds**: 配置混合端口 (mixed) 和 TUN 接口 (tun)。
        *   **Outbounds**: 配置代理节点 (vless/vmess/trojan) 和直连规则 (direct)。
        *   **Route**: 配置路由规则 (geosite-cn, geoip-cn) 和 DNS 劫持。
        *   **Experimental**: **关键点**，这里强制开启了 `clash_api`，默认端口 9090，为后续的监控功能提供支持。
    4.  配置写入磁盘 (`config.json`)。
    5.  `SingBoxCoreAdapter` 启动子进程 `sing-box.exe run -c config.json`。

### 2.2 实时连接详情 (Connection Dashboard)
**功能**: 实时展示当前活动连接、速度、流量，并支持断开连接。

*   **核心代码**:
    *   [DashboardViewModel.cs](src/WowProxy.App/ViewModels/DashboardViewModel.cs): 核心逻辑控制器。
    *   [ClashApiClient.cs](src/WowProxy.Infrastructure/ClashApiClient.cs): 与内核 API 通信。
    *   [ConnectionModel.cs](src/WowProxy.App/Models/ConnectionModel.cs): 连接数据模型及格式化逻辑。
    *   [MainWindow.xaml](src/WowProxy.App/MainWindow.xaml): UI 展示 (DataGrid)。

*   **实现流程**:
    1.  **初始化**: `MainViewModel` 创建 `DashboardViewModel` 实例。
    2.  **定时轮询**: `DashboardViewModel` 启动一个 `DispatcherTimer`，每 1 秒触发一次 `OnTimerTick`。
    3.  **数据获取**: 
        *   调用 `ClashApiClient.GetConnectionsAsync()`。
        *   发送 HTTP GET 请求到 `http://127.0.0.1:9090/connections`。
        *   获取 JSON 响应，包含所有连接的源 IP、目标域名、已用流量、速率等。
    4.  **数据更新与差分**:
        *   `UpdateConnections()` 方法对比新旧数据。
        *   如果连接 ID 已存在，更新其 Upload/Download 字段。
        *   **速度计算**: `ConnectionModel.Update()` 中，计算 `(当前总量 - 上次总量) / 时间间隔` 得到实时速度。
    5.  **单位格式化**:
        *   `ConnectionModel` 中的 `ToHumanReadable()` 方法将字节数转换为 KB/MB/GB，绑定到 UI 的 `UploadSpeedText` 等属性。

### 2.3 高级搜索与过滤
**功能**: 在连接列表中根据关键字实时筛选显示内容。

*   **核心代码**:
    *   [DashboardViewModel.cs](src/WowProxy.App/ViewModels/DashboardViewModel.cs) (UpdateConnections 方法)
    *   [MainWindow.xaml](src/WowProxy.App/MainWindow.xaml) (TextBox 绑定)

*   **实现流程**:
    1.  用户在 UI 的搜索框输入文本，绑定到 ViewModel 的 `FilterText` 属性。
    2.  `FilterText` 的 setter 触发 `OnPropertyChanged`，虽未直接触发过滤，但在下一次 `OnTimerTick` 数据更新时生效。
    3.  在 `UpdateConnections` 方法的第一步，执行 LINQ 查询过滤：
        ```csharp
        filtered = allConnections.Where(c => 
            c.Metadata.Host.Contains(key) || 
            c.Metadata.Process.Contains(key) || 
            c.Metadata.Network.Contains(key) || 
            ... 
        )
        ```
    4.  支持多字段匹配：同时检查主机名、IP、进程名、端口、网络类型 (TCP/UDP)、策略链 (Proxy/Direct) 等。

### 2.4 节点订阅与管理
**功能**: 解析 v2rayN 格式的订阅链接，导入节点。

*   **核心代码**:
    *   [NodeImport.cs](src/WowProxy.Domain/NodeImport.cs): 节点解析逻辑。
    *   [MainViewModel.cs](src/WowProxy.App/MainViewModel.cs) (UpdateSubscriptionAsync 方法)。

*   **实现流程**:
    1.  下载订阅 URL 的内容（通常是 Base64 编码的文本）。
    2.  Base64 解码，得到一行行的 `vmess://...`, `vless://...` 链接。
    3.  `NodeImport.ParseText()` 解析这些链接：
        *   对于 VMess，解析 JSON 结构。
        *   对于 VLESS/Trojan，解析 URL 参数 (uuid, host, sni, fp 等)。
    4.  转换为内部的 `ProxyNode` 对象，存入 `ObservableCollection<ProxyNodeModel>` 供 UI 显示。

### 2.5 真延迟与速度测试
**功能**: 测试节点的真实连通性和带宽。

*   **核心代码**:
    *   [NodeTester.cs](src/WowProxy.Domain/NodeTester.cs)

*   **实现流程**:
    *   **真延迟 (TCP Ping)**: 不使用 ICMP Ping，而是建立一个真实的 TCP 连接到目标节点的端口。记录 `ConnectAsync` 耗时。这能反映代理服务器是否真的在监听端口。
    *   **速度测试**: 
        1.  临时启动一个 sing-box 实例，仅包含该节点。
        2.  通过该代理下载一个测速文件（如 Google 的 10MB 文件）。
        3.  计算下载耗时和流量，得出带宽。

### 2.6 系统集成
**功能**: 设置 Windows 系统代理，打包发布。

*   **核心代码**:
    *   [WindowsSystemProxy.cs](src/WowProxy.Infrastructure/WindowsSystemProxy.cs): 调用 WinINET API 设置 IE 代理。
    *   [publish.ps1](publish.ps1): 自动化构建脚本。

*   **实现流程**:
    *   **系统代理**: 使用 P/Invoke 调用 `InternetSetOption`，即时修改系统的 HTTP 代理设置。
    *   **单文件发布**: csproj 中配置了 `<PublishSingleFile>true</PublishSingleFile>`，将所有 DLL 依赖和 .NET 运行时打包进一个 EXE 文件。

## 3. 项目文件结构映射

```text
g:\trae-project\omini-proxy\
├── src\
│   ├── WowProxy.App\               # UI 主程序
│   │   ├── Models\                 # UI 数据模型 (ConnectionModel)
│   │   ├── ViewModels\             # 视图模型 (MainViewModel, DashboardViewModel)
│   │   ├── MainWindow.xaml         # 主界面布局
│   │   └── App.xaml                # 程序入口
│   ├── WowProxy.Core.SingBox\      # sing-box 内核适配
│   │   ├── SingBoxConfigFactoryV2.cs # 核心配置生成逻辑
│   │   └── SingBoxCoreAdapter.cs   # 进程管理
│   ├── WowProxy.Infrastructure\    # 基础设施
│   │   ├── ClashApiClient.cs       # Clash API 客户端
│   │   └── WindowsSystemProxy.cs   # 系统代理设置
│   └── WowProxy.Domain\            # 领域模型
│       └── NodeImport.cs           # 节点解析逻辑
├── dist\                           # 构建产出目录
└── publish.ps1                     # 构建脚本
```

## 4. UI 交互与体验优化 (v18+)

### 4.1 节点列表交互逻辑
为了解决 "选中" 与 "活动" 状态混淆的问题，UI 进行了深度优化，逻辑参考了 v2rayN：

*   **状态分离**:
    *   **选中状态 (Selected)**: 仅表示用户当前的焦点行，用于查看详情或执行右键操作。显示为系统默认选中色（灰色 #808080）。
    *   **活动状态 (Active)**: 表示当前内核正在使用的代理节点。始终显示为 **绿色背景 (#2E8B57)**。
*   **样式优先级**: 
    *   通过 `MultiDataTrigger` 和 `DataGrid.CellStyle` 强制样式优先级：**活动状态 > 选中状态**。
    *   即使用户点击选中了活动节点，它依然保持绿色，不会变灰，确保用户能随时一眼识别当前使用的节点。

### 4.2 界面重构
*   **三层布局**: 顶部状态栏（内核/系统代理） + 中部 TabControl（仪表盘/连接详情/设置） + 底部简略信息栏。
*   **设置分离**: 将复杂的端口、路径、日志级别设置移至独立的 "设置" 标签页，保持首页清爽。
*   **功能按钮优化**: "清空节点" 调整为 "移除节点"，仅删除当前选中的单个节点，防止误操作。
