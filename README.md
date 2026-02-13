# WowProxy（Windows）

这是一个 Windows 桌面代理客户端原型：通过启动 `sing-box.exe` 作为内核，在本机开启 `mixed` 入站（HTTP+SOCKS），并可一键切换系统代理与查看日志。

## 使用方式

1. 下载 sing-box（Windows amd64）并解压，得到 `sing-box.exe`。
2. 运行 WowProxy。
3. 在界面里选择 `sing-box.exe`，设置端口（默认 10808）。
4. 点击“启动”。需要的话勾选“系统代理”。

## 运行要求

- Windows 10/11（x64）

## 打不开怎么办

- 先确认系统是 64 位 Windows 10/11（32 位系统无法运行 `win-x64` 产物）
- 如果提示“Windows 已保护你的电脑”，点“更多信息”→“仍要运行”
- 如果双击完全没反应：用最新版 `dist\\WowProxy\\WowProxy.App.exe`，启动失败会弹窗并写入崩溃日志
- 崩溃日志位置：`%LocalAppData%\\WowProxy\\logs\\crash-*.log`

## 数据位置

配置与运行数据默认存放在：

- `%LocalAppData%\\WowProxy\\`
