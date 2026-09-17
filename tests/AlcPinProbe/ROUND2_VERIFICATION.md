# Round2 复验报告：BaziProfile / TodoList 去 STJ 反射（源生成修复）

- 角色：software-qa-engineer-2（独立证伪，非盖章）
- 目标：独立复验 software-engineer-3 对 P0「STJ 反射钉住可回收 ALC」的**收口修复**
  （BaziProfile 改源生成 `BaziJsonContext`；TodoList 改插件侧源生成序列化 + 旧格式兼容）
- 基线 HEAD：`71f3994`（工作区含 15 个未提交改动文件）
- 复验时间：2026-09-18
- 结论：**两个 P0 修复均通过独立复验**（无残留 PINNED）。附「判据敏感性对照」以证明探针能真正翻面。

---

## 0. 复验前置：确认被测 DLL 就是修复后的源码

独立证据（不依赖工程师自述）：

| 取证 | 结果 |
|---|---|
| `plugins/com.lantern.bazi/Bazi.dll` mtime | 2026-09-18 01:44:59 > `BaziProfile.cs` 01:44:26 ✅ |
| `plugins/com.lantern.todo/TodoList.dll` mtime | 2026-09-18 01:45:01 > `TodoListPlugin.cs` 01:44:33 ✅ |
| 二进制内类型名（`grep -a`）| `Bazi.dll` 含 **`BaziJsonContext`**；`TodoList.dll` 含 **`TodoJsonContext`** ✅ |
| App bin 内 plugins 副本 | 与 repo `plugins/` 同尺寸同 mtime（同一次 Deploy）✅ |

> 说明：`PluginSmoke --only bazi` 跑的是 `src/DesktopPet.App/bin/.../win-x64/plugins`
> 下的副本，其 mtime/尺寸与 repo `plugins/` 一致 → 是修复后 DLL（非旧副本）。

---

## 1. ALC 回收判定（真实 `PluginLoader` + 可回收 ALC，逐 case 独立进程）

夹具：`tests/AlcPinProbe`（本轮**重写 todo 用例**，见 §3）。
命令：`./AlcPinProbe.exe "G:/lantetn-li/neko" <case>`，每 case 独立进程。

| case | 动作（走插件真实代码） | 结果 | 期望 | 判定 |
|---|---|---|---|---|
| `control` | 仅加载→卸载 | COLLECTED(Y) | Y | OK（基线） |
| `profileload` | `BaziProfile.Load`（源生成反序列化）| **COLLECTED(Y)** | Y | OK（**修复前 = N**） |
| `profilesave` | `BaziProfile.Save`（源生成序列化）| **COLLECTED(Y)** | Y | OK（**修复前 = N**） |
| `chatappend` | `ChatHistory.Append`（手写 writer）| COLLECTED(Y) | Y | OK |
| `buildbody` | `DeepSeekClient.BuildRequestBody` | COLLECTED(Y) | Y | OK |
| `sdkload` | `ChatHistory.Load`（JsonDocument）| COLLECTED(Y) | Y | OK |
| `clipstring` | `JsonSerializer.Serialize(List<string>)`（BCL 泛参）| COLLECTED(Y) | Y | OK（对照） |
| `todohostsave-mechanism` | **敏感度对照**：模拟旧写法把 `List<TodoItem>` 交给宿主泛型存储 | **PINNED(N)** | N | OK（**证明判据敏感**） |
| `todosave` | 真实 `TodoListPlugin.Save`（OnLoad→反射调私有 Save→源生成）| COLLECTED(Y) | Y | OK |
| `todoload` | 真实 `TodoListPlugin.Load`（预置**旧版裸数组**存档）→ 读回 2 条 | COLLECTED(Y) | Y | OK |
| `todoroundtrip` | 真实 Save→Load 往返（新格式）→ 读回 3 条且内容一致 | COLLECTED(Y) | Y | OK |

全部 `VERDICT-OK`，退出码 0。
`todosave/todoload/todoroundtrip` 还带**功能断言**（落盘是新格式 JSON 字符串 / 旧格式读回条数 / 往返内容），
故排除「Save 静默 catch 吞异常导致假 Y」。

---

## 2. 与旧版 STJ 反射的逐字节等价（`tests/BaziJsonDiff`，共 53 项，全 PASS）

新增 BaziProfile 段（Round2）：

| 项 | 断言 | 结果 |
|---|---|---|
| 9. 源生成落盘字节 vs 旧版反射（6 边界样例，含转义 / 超长 / 0 值非 null）| 逐字节一致 | PASS |
| 10. 随机差分模糊 **20000 例**（ApiKey 恒 null）| 逐字节一致 | PASS |
| 11. ApiKey 语义 | 落盘带 `enc:` 前缀、**不含明文**；读回明文逐字符还原；空串/null → 落盘 `null` | PASS |
| 12. 向后兼容 | 旧版反射存档逐字段读回一致；旧版**明文 ApiKey**（无 `enc:`）原样兼容 | PASS |
| 13. 畸形健壮 | null/空串/非 JSON/`{}`/`null`/number/array/未知字段/类型错/截断/过大（Mbti 5,000,000 字符）→ 均不抛，返回非 null | PASS |

（既有的 ChatHistory / BuildRequestBody 段 1–8 亦全 PASS，无回归。）

> 注：ApiKey 非 null 时 DPAPI `ProtectedData.Protect` 每次密文随机 → **无法**做字节比对，
> 故该项只做**语义**断言（前缀 / 无明文 / 往返）。这是方法学上正确、也是诚实的选择。

---

## 3. 对 software-engineer-3「todo 用例」批评的采纳与修正

**工程师批评（成立）**：旧 `todolistsave` 直接 `JsonSerializer.Serialize(list, pluginListType)` 模拟宿主反射，
属「机制探针」——即便插件修好也**永远 N**，不能当回归判据。

**修正**：本轮把探针的存储替身 `HostLikeStorage` **逐行复刻**宿主 `JsonPluginStorage`
（`Set<T>` 内 `JsonSerializer.Serialize(value, WriteIndented)`），并新增两类用例：

1. `todohostsave-mechanism`（**敏感度对照**）：显式走「旧写法」路径 → 得 N。
   → 证明 `HostLikeStorage` 确实会反射插件类型，即判据是敏感的；一旦插件退回旧写法，
      `todosave` 会立刻翻成 N。
2. `todosave` / `todoload` / `todoroundtrip`：走**插件真实路径**（OnLoad 真 `TodoListPlugin`
   → 反射调其私有 `Save`/`Load`，其内部 `Capability.Storage` 即 `HostLikeStorage`）→ 得 Y。

二者一 N 一 Y，构成有效回归门。

---

## 4. 泛化扫描：是否还有插件类型被交给 STJ 反射

对 `builtins/**` 全量扫 `Storage.Get/Set<...>` 与 `JsonSerializer.*`：

| 站点 | 泛型实参 | 是否插件类型 |
|---|---|---|
| TodoList `Set/Save` | `string`（源生成）| 否 ✅ |
| BaziProfile `Load/Save` | `BaziProfile`（源生成 context）| 否 ✅ |
| ClipboardHistory `Get/Set<List<string>>` | `List<string>`（**BCL 闭泛型**）| 否 ✅ |
| WordCard/Notes/HourlyChime/Affection/Bazi/Countdown/Pomodoro/Sleep/Weather | `int/bool/string` | 否 ✅ |

结论：除已修的 BaziProfile / TodoList 外，**无其它**插件自定义类型被交给宿主 STJ 反射。

---

## 5. 已知限制 / 未验证项（诚实标注）

1. **TodoList 落盘格式变更（向下兼容非对称）**：新格式 = JSON **字符串**（含引号），旧格式 = **裸数组**。
   - 旧→新（升级读旧档）：已验证兼容（`todoload` 读回 2 条）。
   - 新→旧（**降级**回旧版本）：旧版 `Get<List<TodoItem>>` 对「JSON 字符串」反序列化会失败 →
     旧版可能显示为空（数据仍在盘上，未丢）。属**降级**场景的已知限制，非本轮缺陷。
2. **调度器持有插件委托的生命周期**未在本夹具覆盖：`SchedulerImpl` 本轮仅加可观测性日志
   （`DescribeCallback` 只读 `DeclaringType.FullName` 存字符串，不缓存 Type，无新增钉根），
   但「宿主 Scheduler 是否在 OnUnload 时彻底释放插件委托」属既有机制，未单独证伪。
   `PluginSmoke --only bazi`（会注册 1 个 timer）实测 `alc=Y`、`tmrL=0`，未见泄漏。
3. `profileload/profilesave` 的「修复前 = N」来自 Round1 实测 + 工程师复现自述；本轮只复验修复后 = Y。

---

## 6. 复现命令

```bash
# 1) 构建夹具
bash /c/Users/Lantern/neko-dotnet-env.sh build tests/AlcPinProbe/AlcPinProbe.csproj -c Debug
bash /c/Users/Lantern/neko-dotnet-env.sh build tests/BaziJsonDiff/BaziJsonDiff.csproj -c Debug

# 2) ALC 逐 case（务必独立进程）
cd tests/AlcPinProbe/bin/Debug/net8.0-windows
for c in control profileload profilesave chatappend buildbody sdkload clipstring \
         todohostsave-mechanism todosave todoload todoroundtrip; do
  ./AlcPinProbe.exe "G:/lantetn-li/neko" "$c"; echo "exit=$?"
done

# 3) 字节比对 + 兼容 + 健壮性
cd tests/BaziJsonDiff/bin/Debug/net8.0-windows && ./BaziJsonDiff.exe   # 期望 53 项全 PASS
```

退出码：ALC 结论符合期望 → 0；结论不符 → 1；功能断言失败 → 3。
