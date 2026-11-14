# 乐舞胡旋 · Kinect 离线展项

Kinect 骨骼驱动展项。待机时，浏览器用 MediaPipe Selfie Multiclass 的衣物类别掩膜和肩髋限定的胸腹区域，从同帧 RGB 统计红、蓝、黑、白、灰。开场时锁定角色。对应唐俑出场、舞蹈和谢幕视频由角色表选择，背景音乐随播放阶段切换。

## 展机启动

1. 使用 Windows x64 和 Chrome 或 Edge。第一次部署时，Kinect v2 需用带供电的 PC 转接器和 USB 3.0 接口，预装 `drivers/KinectRuntime-v2.0_1409-Setup.exe`。Azure Kinect DK 用原配供电和 USB 3.0 数据线，采集程序随包携带 Sensor SDK、Body Tracking、DirectML 和骨骼模型。如需装 VC++ 运行时或用 Azure Viewer 检查设备，可使用 `drivers/` 里的微软安装程序。
2. 插入一台设备，双击 `Start-TangSancai.cmd`。页面自动打开 `http://127.0.0.1:8765/`。默认优先选 Azure Kinect DK，找不到时检查 Kinect v2。需要指定型号时，在命令行执行 `Start-TangSancai.cmd azure` 或 `Start-TangSancai.cmd v2`。
3. 右上角显示设备名和 RGB 预览。开机首轮如果出现「开启声音」，点击按钮即可播放声音。点击舞台也可手动触发开场、舞蹈和逐段换釉。设备掉线后程序会重试，右上角显示等待状态。采集日志在 `bridge.log`、`bridge-error.log`。
4. 无设备时双击 `Start-Simulation.cmd`。页面依次收到模拟在场、举手和换釉动作，识色用真实分割模型处理包内的红衣照片。重新打开页面前关闭旧模拟页面，以重启模拟时序。切换设备模式时先关闭当前启动窗口；也可用 PowerShell 脚本的 `-Port` 另开一个实例。

## 从源码打包

在联网的 Windows x64 构建机上安装 .NET SDK 10，运行 `Build-Offline.ps1 -OutputDirectory E:\TangSancai-Kinect-Offline`。首次构建从 NuGet 还原 Kinect SDK 和 .NET 运行时。把完整的输出文件夹复制到展机，附带官方 [v2 Runtime](https://www.microsoft.com/en-us/download/details.aspx?id=44559) 和 [Azure Kinect SDK](https://github.com/microsoft/Azure-Kinect-Sensor-SDK/blob/develop/docs/usage.md#installers) 安装包。展机启动使用已发布的 EXE、浏览器、本地资源和设备运行时。

## 数据流

- `kinect_bridge/` 提供两个采集后端和一个模拟源。骨骼统一为米、X 向右、Y 向下、Z 向前，并标记每个关节的跟踪状态。桥固定当前人体 ID，当前目标离场后再选下一个。
- 本地 `/kinect` WebSocket 每条消息依次携带 4 字节小端 JSON 长度、UTF-8 JSON 和可选 JPEG。JSON 带帧号、时间戳、人体 ID、关节和同帧 RGB 中的归一化投影。Azure 复用设备 MJPEG；v2 JPEG 在桥内生成一次。网页以二进制读取图像。
- `motion_capture.js` 从 Kinect 骨骼判断举手、转身、静止和离场。`clothing_color_worker.js` 用本地模型输出的第 4 类掩膜限制统计区域。`index.html` 汇总最多五帧颜色计数，沿用开场等待窗口和白釉默认角色。

## 现场实机验收

| 项目 | 通过条件 |
|---|---|
| 两代分别冷启动 | 在最终展机、指定 USB 口和电源上先跑官方骨骼查看器，再试一键启动。各自出现正确型号、RGB 画面和实时人体跟踪；记录系统、SDK、USB 口和 Azure 加速模式。 |
| 动作和多人 | 左右手分别举起、背身转一圈、静止和离场均触发相应动作。第二人从旁经过不抢当前角色。首段和第 1～4 段换釉后都能再次触发。 |
| 五色和舞台灯光 | 用红、蓝、黑、白、灰上衣分别开场，核对所选唐俑。加测白衣黑裤、深蓝、灰衣、条纹和侧身，统计误判和白釉回退次数。 |
| 音画及回合 | 举手进舞蹈，五段换釉后谢幕、祝福并回待机。确认 MP3 和彩蛋音轨、首轮「开启声音」及连续两轮的角色隔离。 |
| 掉线恢复 | 在待机和互动时拔线再插回，观察等待提示、旧画面清除、骨骼恢复和下一轮识色。两种设备分别测试。 |
| 离线和连续运行 | 展机断网后冷启动运行至少 30 分钟，确认素材和模型本地加载，记录跟踪掉帧、进场到拉帘耗时和声音。 |

模拟图取自 [Red shirt man.jpg](https://commons.wikimedia.org/wiki/File:Red_shirt_man.jpg)，作者 Bigboytony，公有领域，包内图片经缩放。MediaPipe Tasks Vision 1.2.3 与 [Selfie Multiclass](https://developers.google.com/edge/mediapipe/solutions/vision/image_segmenter#multi-class-selfie-segmentation-model)、Tailwind CSS 2.2.19 和 daisyUI 1.20.1 本地存放于 `assets/vendor/`。
