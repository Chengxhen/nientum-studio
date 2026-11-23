# NientumStudio 谱面编辑器

English: [README.en.md](README.en.md)

《Nientum - Op.ZERO》自制谱面工具链：WPF 编辑器 + BepInEx 5 游戏内加载 Mod。

本仓库只包含代码。游戏本体、官方谱面、音乐、CG、字体等资源均不包含；游戏内容版权归 QueseraGames 所有。

## 目录

- `src/NientumStudio/` — 编辑器源码（.NET 10 WPF，Windows）
- `mod/NientumCustomCharts/` — 游戏内 Mod（BepInEx 5，netstandard2.1）
- `install/` — 发布包使用的安装/卸载脚本与说明（`install.ps1`、`uninstall.ps1`、两个 bat）

## 环境要求

- Windows x64
- .NET 10 SDK
- 已安装《Nientum - Op.ZERO》（构建需要读取游戏目录里的 DLL）
- BepInEx 5（运行 Mod 需要）

## 构建

编辑器需要从游戏目录引用 `Newtonsoft.Json.dll` 和 `fmodstudio.dll`，用 `NientumGameDir` 指定游戏根目录（含 `Nientum.exe` 的那层）：

```bat
dotnet build src\NientumStudio\NientumStudio.csproj -c Release ^
  -p:NientumGameDir="D:\Games\Liora"
```

Mod 用 `GameDir` 指定同一目录，构建成功后会自动复制到 `BepInEx\plugins`：

```bat
dotnet build mod\NientumCustomCharts\NientumCustomCharts.csproj -c Release ^
  -p:GameDir="D:\Games\Liora"
```

不传路径时构建会直接报错并给出示例，不会再写死某台机器的路径。

## 谱面数据格式

每个谱面是一个文件夹，放在游戏的 `CustomCharts\` 下：

```
CustomCharts\<曲目名>\
  meta.json      曲目信息（标题、BPM、音频文件名、封面等）
  normal.json    谱面
  <音频文件>     自备，版权自负
  <封面图片>
```

编辑器负责生成/编辑 `meta.json` 和 `normal.json`；Mod 启动时扫描该目录并注册曲目。

## 已知限制

- 只支持 Windows；编辑器基于 WPF 与 FMOD，不能在 Linux/macOS 构建运行。
- 官方曲目库与试听来自游戏本体，未安装游戏时只能编辑自制谱面。
- 谱面判定/音押手感随游戏版本变化，Mod 针对 BepInEx 5 + 当前游戏版本编译。

## 发布包

Release 提供 `NientumStudio_Exp-v1_2026.11.22_rev2.zip`：自包含编辑器（内置 .NET 运行时）+ 谱面 Mod + BepInEx 框架 + 安装/卸载脚本，解压后运行 `安装.bat` 即可。rev2 同步了斜轨、自定义镜头、Media Foundation 解码与诊断日志；不含游戏素材、音频 bank 与音乐。

已安装上一版的用户可改下 `NientumStudio_Exp-v1_2026.11.22_rev2_patch.zip`（0.2 MB），只含两个 DLL 和安装脚本，放进安装目录双击 `应用增量补丁.bat`。

## 版权与下架声明

本项目为非官方玩家工具，与《Nientum - Op.ZERO》的开发商、发行商无隶属关系。游戏及其 CG、立绘、音乐、字体、剧本等素材版权归 QueseraGames 所有；发布包中的素材仅限个人学习交流，禁止商用、二次售卖或冒充官方。如权利人认为侵权，请通过本仓库 Issue 联系，核实后立即下架相关源码与发布物。

## 许可

代码以 MIT 许可发布，见 [LICENSE](LICENSE)。游戏素材不在许可范围内。