# WD14 图片反推工具（WPF · 纯 CPU · 绿色便携）

把 ComfyUI-WD14-Tagger 的反推能力抽出来做成独立小工具：**不需要 ComfyUI、不需要 Python、不用联网**，解压双击就能跑。

模型固定放在程序目录的 `models\` 里，结果只落在 `data\results.csv`，软件本身不往别处写东西。

## 一、开箱使用

1. 把整个文件夹放到任意位置（路径里有中文/空格也没问题），**不要只把 exe 拎出来**。
2. 双击 `WD14Tagger.exe`（启动即最大化）。
3. 点「选择目录」挑一个图片文件夹，也可以直接把文件夹 / 图片拖进窗口。
4. 点「开始反推」。16 核 CPU 上大约 0.5 秒一张（448 的 ViT 模型）。

反推完在左侧队列点选图片，中栏显示预览与标签文本框；「复制标签」一键进剪贴板。

## 二、目录结构（全部在软件目录里）

```
WD14Tagger.exe            主程序（自包含，.NET 运行时已打包进来）
models\                   模型：wd-vit-tagger-v3.onnx + wd-vit-tagger-v3.csv
data\config.json          设置（阈值、线程数、复制格式、上次目录……）
data\results.csv          反推结果汇总（一行一张图）
data\app.log              运行日志
```

- 模型路径是**写死的 `models\`**，没有导入/选择目录的功能。要换模型就把新的 `xxx.onnx` 和同名 `xxx.csv` 丢进 `models\`，重启软件后在右上角下拉里选。
- 想恢复默认设置：删掉 `data\config.json`。
- 想清空历史结果：删掉 `data\results.csv`（下次运行会重新写表头）。

## 三、结果在哪

- **`data\results.csv`**：唯一的结果存放处。表头是 `文件名,目录,标签数,评级,评级分,标签`。
- **同一张图再反推不会追加重复行**：启动时会读回已有结果，写的时候按「目录 + 文件名」覆盖原来那一行，所以反复跑也不会越来越乱。
- 旧版本（带 时间/模型/阈值 等列的）CSV 会被自动认出来并转成新格式，原有行都保留。
- 标签串在 CSV 里是**原始形式**（保留下划线、按模型词表顺序、没有末尾逗号），方便当数据用。
- **复制到剪贴板**时才应用「复制格式」四项：下划线转空格、末尾加逗号、按分数排序、括号转义。所以同一批结果既能存档也能直接粘进提示词。
- 标签里的括号默认**原样保留**；需要在提示词里当字面量时，勾选「复制格式 → 括号转义」就会复制成 `\(` `\)`。

## 四、参数

| 参数 | 说明 |
| --- | --- |
| 普通标签阈值 | 默认 0.35。越低标签越多，0.3~0.4 最常用 |
| 角色标签阈值 | 默认 0.85。角色名分数通常很高，放宽会冒出无关角色 |
| 附带分级标签 | 额外输出 `general / sensitive / questionable / explicit` |
| 推理线程 | 默认 = min(8, 核数/2)。实测 8 线程最快，开满核心反而变慢 |
| 跳过已完成 | 队列里已出结果的图片不再重跑（中断后继续很方便） |
| 含子目录 | 选目录时递归所有子文件夹 |
| 复制格式：下划线转空格 | `blue_background` → `blue background`（默认开） |
| 复制格式：末尾加逗号 | 复制成 `a, b, c, ` 形式，方便直接拼提示词（默认关） |
| 复制格式：按分数排序 | 按概率从高到低，而不是词表顺序（默认关） |
| 复制格式：括号转义 | `hatsune miku (cosplay)` → `hatsune miku \(cosplay\)`，方便直接丢给 ComfyUI 的权重解析（默认关） |

标签区只显示一段纯文本（存档用的原始标签），复制时再按「复制格式」转换；想夹到别处用，直接选中文本也能复制一部分。

快捷键：`F5` 开始反推，`Delete` 删除选中的队列项；拖放文件到窗口即加入队列。

## 五、命令行模式（批量 / 脚本）

```bat
WD14Tagger.exe --cli "D:\pics" --threshold 0.3 --sort
```

| 参数 | 说明 |
| --- | --- |
| `--cli <目录或图片...>` | 进入命令行模式，可给多个路径 |
| `--model <onnx>` | 指定模型，默认取 `models\` 里最大的那个 |
| `--csv <path>` | 汇总 CSV 路径（默认 `data\results.csv`） |
| `--threshold` / `--char-threshold` | 阈值，默认 0.35 / 0.85 |
| `--threads` | intra-op 线程数 |
| `--underscore` / `--no-underscore` | 复制时下划线转空格（默认开） |
| `--trailing` / `--no-trailing` | 复制时末尾加 `", "`（默认关） |
| `--sort` | 复制时按分数排序（默认按词表顺序） |
| `--escape-parens` / `--no-escape-parens` | 复制时括号转义成 `\(` `\)`（默认关） |
| `--dump <json>` | 把每张图所有标签的分数导出成 JSON（调试用） |
| `--quiet` | 不逐条打印，只出统计 |

输出格式：`图片相对路径<TAB>标签数<TAB>标签串`，最后一行是汇总统计（成功数、总用时、每张耗时）。

## 六、实现要点（想改代码的话）

- **预处理与原插件严格对齐**：WIC 解码（含 EXIF 旋转）→ 自写抗锯齿 Lanczos3 等比缩放（单程，不做盒式预缩小）→ 白底居中补边 → RGB→BGR float32 NHWC 0~255。拿 6 张图跟原插件（PIL LANCZOS + onnxruntime）逐标签比过，最大分数差 0.014，标签集合基本一致。
- **吞吐最优解**：单 ONNX session + intra-op=8 + 单张串行 + 「解码/预处理」与「推理」双缓冲流水线。实测比 batch 推理、多 session 并行都快（batch 越大单张越慢）。
- **纯手写 MVVM**，唯一第三方依赖是 `Microsoft.ML.OnnxRuntime`（CPU 版）。

## 七、自己编译

源码在 `WD14Tagger\`，需要 .NET 9 SDK。

```bat
:: 联网环境：还原走 nuget.org
dotnet publish WD14Tagger\WD14Tagger.csproj -c Release -r win-x64 --self-contained true -o dist
```

然后自己准备模型：把 `xxx.onnx` 和同名 `xxx.csv` 放进 `dist\models\`（仓库里不含模型文件，占 360 MB 且版权归原作者）。

**离线编译**（无外网、用本地 nupkg 源）：把 API 包放进 `nuget-feed\`，再用附带的离线配置：

```bat
dotnet publish WD14Tagger\WD14Tagger.csproj -c Release -r win-x64 --self-contained true -o dist ^
  --configfile nuget.offline.config -p:RestorePackagesPath=packages
```

`nuget.offline.config` 里 `globalPackagesFolder` 指向仓库内的 `packages\`，`fallbackPackageFolders` 借用系统缓存里的运行时包（自包含发布需要 `Microsoft.NETCore.App.Runtime.win-x64`，缺了就报 `NU1101`）。

## 八、常见问题

- **状态栏显示「models\ 里还没有模型」**：把 `xxx.onnx` 和同名 `xxx.csv` 放进程序目录的 `models\`（路径会显示在右侧「模型目录」里），重启软件。多半是把 exe 单独拷出来了，或者跑的是 `bin\Release\...` 里的副本。
- **第一次点开始有点慢**：首次会初始化 ONNX session 并热身，约 1 秒。
- **CPU 占满 / 风扇狂转**：线程滑块往左调，8 → 6 → 4；低于 4 会明显变慢，只是更安静。
- **窗口太大 / 太小**：默认最大化，可以直接用窗口右上角的还原按钮缩小。
- 反推只在 CPU 上跑，不需要显卡。
