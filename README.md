# PairPair ConfigTool

个人本地配置表查看与编辑工具。工具独立于 Unity 项目，支持 `.xlsx`、生成后的 `.lua`，以及 `.json` 配置浏览和编辑。

JSON 支持 Protokit 的 `*Map`、`*Slice` 包装、顶层记录对象和数组；每个包装表单独显示，空表仍列在侧边栏，`MD5` 元数据不作为表。字段展开为列，嵌套对象和数组保留为 JSON 单元格，可使用当前表和全局搜索。只扫描所选目录第一层；没有支持的文件时会显示提示。

JSON 编辑沿用“编辑 → 修改已有单元格 → 保存”、未保存修改提示和快捷键。表头、生成的索引列和原本不存在的字段锁定。字符串直接输入；数字、布尔、数组、对象需保持原类型，数组和对象使用合法 JSON。修改与 Map 键一致的 `id` / `key` 会同步调整 Map 键；重复键会阻止保存，不自动改写其他表中的引用。

JSON 保存会读取当前 Unity 项目 `Assets/Scripts/Game/GameHotfix/GameProto/Runtime/gen/rawdata` 中的 Protobuf 描述，同步生成相邻 `.bytes` 并更新两份文件的 MD5。工具在写入前校验类型和 JSON / .bytes 外部变更，两份文件先暂存再替换，第二份替换失败时恢复第一份；恢复失败会保留原始备份并报告路径。缺少项目字段定义时拒绝保存。导表源 Excel / 在线表格不会回写，重新导表仍可能覆盖本地修改。Unity `.meta` 不改写。

BusyBits JSON 已添加广告策略、活动排期、每日任务项与行为、里程碑计分类型、商品、物品、多语言和奖励表达式关联。奖励支持多行及字符串数组，`journey_background +1i` 同时关联物品定义和背景配置。按住 Command / Ctrl 单击关联值正向跳转；按住 Option / Alt 单击 `id`、`key` 或配置了索引的 `type` / `target_type` 查询反向引用。奖励表达式按 Goods 的 `key` 查找；积分、价格、等级范围不会按相同数值关联物品。

## 使用

双击：

`PairPair ConfigTool.app`

右上角“目录预设”可以快速切换常用配置目录，也可以保存当前目录、修改名称和路径、调整顺序或指定启动默认目录。所有预设都只保存在本机，不会写入项目。未指定启动默认时，应用会继续读取上次使用的目录。

如果项目目录发生变化，可以在“目录预设”中管理路径，或点击“选择其他目录…”重新指定。

点击“编辑”后可修改当前配置页；存在修改时“保存”按钮才会启用。保存前会检查源文件是否被外部更新，冲突时要求先刷新，避免覆盖新内容。

注意：`match3/LuaProject/gen/conf` 属于生成结果，直接保存可以修改本地 Lua 文件，但下次重新生成配置时可能被覆盖。

## Git 代码同步

当选择的配置目录位于 Git 项目的子目录中，左下角会自动显示“Git 项目”卡片。它会从当前配置目录向上识别仓库根目录，读取当前分支、上游分支和实际远程地址；不会使用或修改 ConfigTool 自己的仓库地址，也不会创建额外克隆或切换分支。

“拉取代码”始终可点击（执行中除外）。它会直接运行 `git pull --ff-only`，不自动合并或变基；如果分支、网络、本地改动或远程状态导致拉取失败，工具会弹出 Git 的失败原因，不会再把按钮置灰。

“清理改动”会先预览，并将每个已跟踪改动和未跟踪文件/目录列为可勾选项。已跟踪改动默认全选，未跟踪项默认不选；确认时只对选中的已跟踪文件执行 `git restore --source=HEAD --staged --worktree -- <路径>`，只对选中的未跟踪项执行 `git clean -df -- <路径>`。被 Git 忽略的文件不会显示或删除。

存在未解决冲突时，工具不会执行清理；请先在常用 Git 工具中处理后再操作。

## 关联跳转

按住 `Command` 或 `Ctrl` 单击带有关联关系的单元格，可以跳到对应配置表并高亮目标行。工具支持普通 ID、父子表分组、`ID + SubID` 以及奖励 JSON 中的物品 ID。

明确关系维护在 `Resources/relationships.js`，其他以 `ID` / `Id` 结尾且存在同名配置表的字段会自动识别。发生多个候选时会显示选择菜单。

按住 `Option`（其他平台为 `Alt`）单击配置表的 `ID` 或 `SubID`，可以查询该主键被哪些配置引用。结果会按配置表分组，选择一项后跳转并高亮具体引用单元格。反向查询在原生配置缓存中完成，不会为了查询而把所有配置行加载到界面中。

## 快捷键

- `⌘K`：查找配置表
- `⌘F`：聚焦顶部搜索框；可用“当前 / 全局”切换搜索范围
- `当前`：只查当前配置表，并在表格内高亮匹配单元格
- `全局`：扫描当前配置目录内的全部配置表，结果按配置表分组；点击结果可定位到具体单元格
- `回车`：跳到下一条搜索结果
- `Shift + 回车`：跳到上一条搜索结果
- `⌘[`：返回关联跳转前的位置
- `⌘]`：前进到下一处关联位置
- `⌘R`：刷新
- `⌘S`：保存当前修改

## 重新构建

项目根目录只放可直接使用的应用：`PairPair ConfigTool.app`（macOS）和 `PairPair ConfigTool.exe`（Windows）。`Resources/` 是两个平台共用的界面资源；平台原生实现分别在 `macOS/` 与 `Windows/`。

### macOS

构建需要 .NET 10 SDK 和 Xcode Command Line Tools。JSON 保存组件使用 Native AOT 打包，运行 App 无需安装 .NET 或 Python。

```bash
./macOS/build.sh
```

JSON 编码集成验证（只操作临时副本）可安装 Python `protobuf` 后运行 `Tests/json_edit_integration.py <rawdata目录> <App路径>/Contents/MacOS/ConfigToolJsonHelper`；它使用独立官方解码器核对全部表、MD5、主键改名、嵌套值和冲突保护。

### Windows

支持 Windows 10/11 x64，应用使用系统的 Microsoft Edge WebView2 Runtime（通常随 Edge/Windows 安装；缺少时启动会给出提示）。在 PowerShell 中执行：

```powershell
.\Windows\build-windows.ps1
```

会在根目录生成 `PairPair ConfigTool.exe`。分发时请带上根目录同级的 `Resources` 文件夹。

如需 Windows on ARM，在 PowerShell 执行：

```powershell
.\Windows\build-windows.ps1 -Runtime win-arm64
```
