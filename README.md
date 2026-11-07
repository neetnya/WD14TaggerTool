# WD14 图片反推工具

由开源项目 [ComfyUI-WD14-Tagger](https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/main) 改造而来。

## License

包含的原项目代码遵循 MIT 许可证，版权归 Copyright (c) 2024 pythongosssss 所有。详见 `LICENSE-original` 文件。

## 一、目录结构

```
WD14Tagger.exe            主程序（自包含，.NET 运行时已打包进来）
models\                   模型：wd-vit-tagger-v3.onnx + wd-vit-tagger-v3.csv
data\config.json          设置（阈值、线程数、复制格式、上次目录……）
data\results.csv          反推结果汇总（一行一张图）
data\app.log              运行日志
```

- 模型路径是**写死的 `models\`**，没有导入/选择目录的功能。要换模型就把新的 `xxx.onnx` 和同名 `xxx.csv` 丢进 `models\`，重启软件后在右上角下拉里选。
- 想恢复默认设置：删掉 `data\config.json`。
- 想清空历史结果：点上方工具条的「清空数据」（会清空 `data\results.csv`，队列里已完成的图片恢复成待处理），或直接删掉 `data\results.csv`。

## 二、结果在哪

- **`data\results.csv`**：唯一的结果存放处。表头是 `文件名,目录,标签数,评级,评级分,标签`。
- **同一张图再反推不会追加重复行**：启动时会读回已有结果，写的时候按「目录 + 文件名」覆盖原来那一行，所以反复跑也不会越来越乱。
- **加入目录时会自动认领旧结果**：CSV 里已有记录的图片直接显示成「完成」并带上标签，勾着「跳过已完成」就不会重复跑；想重跑一两张就取消勾选再点「反推全部/反推选中」。
- 队列里删掉某项（Delete 键）会同时删掉 CSV 里那一行，下次打开不会又冒出来。
- 旧版本（带 时间/模型/阈值 等列的）CSV 会被自动认出来并转成新格式，原有行都保留。
- 标签串在 CSV 里是**原始形式**（保留下划线、按模型词表顺序、没有末尾逗号），方便当数据用。
- **复制到剪贴板**时才应用「复制格式」三项：下划线转空格、末尾加逗号、括号转义。所以同一批结果既能存档也能直接粘进提示词。
- **读回旧结果（CSV 里已有记录）时，这三项同样生效**：标签串会先还原成原始标签，再按当前复制格式显示/复制；切换勾选会立刻重算，不用重新反推。
- 分数不再记录到 CSV，所以界面上去掉了「按分数排序」（结果顺序固定为模型词表顺序：角色标签在前、通用标签在后）。
- 标签里的括号**默认转义**成 `\(` `\)`（复制成提示词可直接用）；取消勾选「复制格式 → 括号转义」就原样输出。

## 三、参数

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
| 复制格式：括号转义 | `hatsune miku (cosplay)` → `hatsune miku \(cosplay\)`，方便直接丢给 ComfyUI 的权重解析（默认开） |

标签区显示的是按「复制格式」转换后的文本，和「复制标签」拿到的内容一致；想夹到别处用，直接选中文本也能复制一部分。

快捷键：`F5` 开始反推，`Delete` 删除选中的队列项；拖放文件到窗口即加入队列。

## 四、自己编译

源码在 `WD14Tagger\`，需要 .NET 9 SDK。

```bat
dotnet publish WD14Tagger\WD14Tagger.csproj -c Release -r win-x64 --self-contained true -o dist
```

然后自己准备模型：把 `xxx.onnx` 和同名 `xxx.csv` 放进 `dist\models\`（仓库里不含模型文件，占 360 MB 且版权归原作者）。
模型地址:https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/tree/main