# Mira System Monitor

独立的圆屏电脑状态监视器，目标设备为 800x800 Android 圆屏。

## 运行方式

1. 双击构建生成的 `desktop-agent/dist/MiraSystemMonitorAgent.exe`。
2. Windows 防火墙允许 PowerShell 在“专用网络”通信。
3. Android 设备和电脑连接到同一个局域网。当前设备是 Wi-Fi `wlan0`，USB 只用于安装 APK。

电脑端窗口中的“开机启动”使用 Windows 任务计划登录后启动；“后台保活”开启时关闭窗口会转入托盘，HTTP/UDP 服务和硬件采集继续运行。

电脑端 EXE 是独立程序，不依赖 Python、PowerShell 7 或 .NET SDK。源码修改后运行 `desktop-agent/build-exe.ps1` 即可重新生成。

电脑端服务：`http://电脑IP:8789/api/v1/status`

Android 端通过 UDP 广播自动发现电脑，不需要手动填写地址。以后可以再加手动地址、NAS 网关或 IPv6。

## 当前数据

- 下载速度、上传速度
- CPU 占用
- 内存占用和已用/总量
- 当前首选网络接口：`ETHERNET` 或 `WI-FI`
- 温度和风扇：Windows 通用接口不提供时显示 `--`，后续可按主板/硬件补充

## UI 约束

仪表盘按参考图使用 360dp 设计坐标：顶部蓝/红网络区，中部绿 CPU 与橙 RAM，底部温度、风扇和监控时间。整个 View 在圆屏内全屏铺满，不绘制参考图最外层装饰边框，也不使用普通矩形卡片布局。
