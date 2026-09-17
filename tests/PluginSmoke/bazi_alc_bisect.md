# Bazi 插件「ALC 收不回来」(P0-3) 根因定位报告

> 工具：`tests/PluginSmoke`（真实 `PluginLoader` + 可回收 ALC + 真实 `System.Threading.Timer` 语义的宿主替身）
> 环境：.NET 8 / win-x64 / Debug；每次运行单插件、空闲 3200ms、`OnUnload` 后强制 GC×3 再判 ALC 弱引用
> 纪律：只加载/卸载，绝不调用槽位回调；未改 `builtins/` 任何源文件（仅新增本工具与本文档）

---

## 0. 结论速览

| 项 | 结论 |
|---|---|
| **根因** | `builtins/DesktopPet.Builtin.Bazi/DeepSeekClient.cs:121` —— `BuildRequestBody()` 用 `JsonSerializer.Serialize(payload)` 序列化一个**定义在插件程序集里的匿名类型**，走的是 **System.Text.Json 反射式序列化**；其**进程级共享缓存**持有对该插件类型的引用，从默认 ALC 侧钉住整个插件 ALC。 |
| **触发点** | `BaziPlugin.cs:40` 的 `DeepSeekClient.SelfCheck()`（OnLoad 6 项自检之一）→ `SelfCheck()` 第 191 行调 `BuildRequestBody(...)`。 |
| **可修 / 不可修** | **可修**（完全在插件侧）——绕开 STJ 反射即可：改用 `Utf8JsonWriter` 手写 JSON 或源生成 `JsonSerializerContext`。`--probe writer` 实测 = **ALC 可回收 (Y)**。 |
| **次要发现** | 宿主的 `JsonPluginStorage.cs:32`（`JsonSerializer.Serialize(value, _opts)`）是同一条反射路径，任何插件把**自定义类型**存盘也会钉根（`--probe store` 实测 = N）。属宿主侧潜在同类隐患。 |
| **lunar.dll** | **排除**。`--selftest lunar` 实测 = ALC 可回收 (Y)；lunar 只有随 ALC 一起消亡的静态数组缓存，无进程级静态根。 |

---

## 1. 二分：OnLoad 的 6 项自检（`--selftest`）

`BaziPlugin.cs:35-40` 在 OnLoad 里跑 6 项自检。`--selftest <清单>` 会**跳过 OnLoad**，改为在已加载的插件 ALC 上按名字反射调用这些自检静态方法，从而隔离「到底是哪一项建立了进程级强根」。

| 变体（`--only bazi`） | dPrivMB | rPrivMB | **alc** |
|---|---|---|---|
| `--skip-onload`（基线，不调 OnLoad） | 2.39 | 2.46 | **Y** |
| （无开关，完整 OnLoad） | 9.94 | 9.03 | **N** |
| `--selftest none`（不跑任何自检） | 2.42 | 2.49 | **Y** |
| `--selftest lunar`（BaziEngine.VerifyKnownCases） | 8.95 | 8.91 | **Y** |
| `--selftest fortune`（DailyFortuneBuilder.SelfCheck） | 9.36 | 8.95 | **Y** |
| `--selftest api`（ApiKeyProtector.SelfCheck，DPAPI） | 2.48 | 3.11 | **Y** |
| `--selftest mbti`（MbtiQuiz.SelfCheck） | 2.53 | 3.33 | **Y** |
| `--selftest prompt`（FortunePrompt.SelfCheck） | 9.62 | 9.28 | **Y** |
| **`--selftest client`（DeepSeekClient.SelfCheck）** | 2.42 | 3.27 | **N** ← 唯一 |
| `--selftest all`（6 项全跑） | 9.98 | 9.15 | **N** |

> 结论：泄漏**不由整个 OnLoad**引起，也不由 lunar 引起，而由 **`DeepSeekClient.SelfCheck()` 单独**引起。
> `lunar/fortune/prompt` 的 dPriv 偏高（≈9MB）只是它们的**瞬时工作集**（RVA 静态数据 + lunar 计算），卸载后都能回收（alc=Y）——**不是**钉根。

原始输出（节选，表 A 行）：
```
--selftest lunar     : com.lantern.bazi  OK OK OK  8.95  0.11  8.91  0.09  0  Y
--selftest client    : com.lantern.bazi  OK OK OK  2.42  0.12  3.27  0.12  0  N
--selftest all       : com.lantern.bazi  OK OK OK  9.98  0.12  9.15  0.12  0  N
```

---

## 2. 机理对照实验（`--probe`）

`--probe <模式>` 不调 OnLoad，仅对**插件 ALC 里的自定义类型**（`DesktopPet.Builtin.Bazi.ChatMessage`）做一次序列化，逐级收紧全局状态来源：

| 模式 | 行为 | **alc** |
|---|---|---|
| `none` | 不序列化 | Y |
| `typeonly` | 只 `GetType` + `Activator.CreateInstance`（不序列化） | **Y** |
| `default` | `JsonSerializerOptions.Default`（进程全局 options） | **N** |
| `owned` | 新建 `JsonSerializerOptions`（resolver 仍为 null） | **N** |
| `ownresolver` | 新建 options + 新建 `DefaultJsonTypeInfoResolver` | **N** |
| **`writer`** | 用 `Utf8JsonWriter` 手写同 JSON（**绕开 STJ 反射**） | **Y** |
| `store` | 走宿主同款存储路径 `IPluginStorage.Set<T>` | **N** |

原始输出（节选）：
```
--probe typeonly   : com.lantern.bazi  OK OK OK  2.41  0.12  2.41  0.12  0  Y
--probe default    : com.lantern.bazi  OK OK OK  2.41  0.12  2.48  0.12  0  N
--probe owned      : com.lantern.bazi  OK OK OK  2.40  0.12  2.48  0.12  0  N
--probe ownresolver: com.lantern.bazi  OK OK OK  2.40  0.12  2.39  0.12  0  N
--probe writer     : com.lantern.bazi  OK OK OK  2.39  0.11  2.39  0.09  0  Y
--probe store      : com.lantern.bazi  OK OK OK  2.43  0.12  2.42  0.12  0  N
```

**读法**：
- 换 options、换 resolver **都无效**（仍 N）⇒ 钉根是 **STJ 反射式序列化路径上的进程级共享状态**，不是 options/resolver 实例本身，因此「把 HttpClient 改成可释放单例」这类 options 级 tweak 根本够不着它。
- `typeonly`=Y ⇒ 单纯创建插件类型实例**不会**钉根（排除「反射建对象」）。
- `writer`=Y ⇒ **绕开 STJ 反射**即可回收，这就是修复方向。
- `store`=N ⇒ 宿主 `JsonPluginStorage.cs:32` 同一条路径，是**宿主侧同类隐患**（任何插件把自定义类型存盘都会钉根）。

---

## 3. 为什么「HttpClient 改可释放单例」无效

`DeepSeekClient.SelfCheck()`（`DeepSeekClient.cs:179`）→ `ChatAsync(null, …)`：
`ChatAsync` 在第 **73-74 行**用 `ValidateApiKey` 判定空 Key 后**立即返回**，**根本没走到第 86 行的 `Http.SendAsync`**。
⇒ OnLoad 阶段 `_http` 从未被创建，`DisposeShared()` 自然是空操作，残留当然不变（≈9MB）。真正钉根的是同一次 SelfCheck 里第 **191 行**的 `BuildRequestBody(...)`。

---

## 4. 全量冒烟基线（18 个内置插件）

```
汇总：加载 18/18 成功；OnLoad 异常 0；OnUnload 异常 0
[定时器泄漏] 0 个   [事件未解绑] 0 个
[ALC 未能回收] 1 个  · com.lantern.bazi  rPriv=8.84MB (IsCollectible=True)
[闲置 CPU Top]  · clipboard cpu=47ms/3200ms  · bazi cpu=31ms/3200ms
```
仅 bazi 一个插件 ALC 不回收，其余 17 个均 Y —— 与「只有 bazi 在 OnLoad 阶段反射序列化插件自定义类型」完全吻合。

---

## 5. 建议修复（均可由插件自行完成，不需改框架）

1. **首选（最小、零行为变更）**：把 `DeepSeekClient.BuildRequestBody`（`DeepSeekClient.cs:113-122`）改为用 `Utf8JsonWriter` 写出同样结构的请求体 —— 请求体只是 `{model, messages:[{role,content}], stream:false}`，手写即等价；`SelfCheck` 的断言语义不变。**实测 `--probe writer` = ALC 可回收**。
2. **次选**：为请求体引入源生成 `JsonSerializerContext`（`[JsonSerializable]`），彻底不用反射式 STJ。
3. **不建议**：只在 OnLoad 关掉 6 项自检（如 `#if DEBUG`/环境变量）—— 只是把 OnLoad 时的钉根**推迟**到用户首次用 AI（`AskAsync → ChatAsync → BuildRequestBody`，`DeepSeekClient.cs:81`）时；根因仍在。
4. **宿主侧同类项**：`JsonPluginStorage.cs:32` 若确实要支持热重载，建议对插件自定义类型的读写改走源生成或 `Utf8JsonWriter`（否则任何插件存盘都会钉根）。

---

## 6. 复现命令

```bash
# 构建本工具（写入 gitignore 的 bin_alt/）
dotnet build tests/PluginSmoke/PluginSmoke.csproj -c Debug -p:BaseOutputPath=bin_alt/
DLL=tests/PluginSmoke/bin_alt/Debug/net8.0-windows/PluginSmoke.dll

# 二分自检
dotnet "$DLL" --only bazi --skip-onload
dotnet "$DLL" --only bazi --selftest none
dotnet "$DLL" --only bazi --selftest lunar
dotnet "$DLL" --only bazi --selftest client      # => alc=N（唯一）
dotnet "$DLL" --only bazi --selftest all

# 机理对照
dotnet "$DLL" --only bazi --probe default
dotnet "$DLL" --only bazi --probe owned
dotnet "$DLL" --only bazi --probe ownresolver
dotnet "$DLL" --only bazi --probe writer         # => alc=Y（修复方向）
dotnet "$DLL" --only bazi --probe store          # => alc=N（宿主存储亦中招）

# 全量
dotnet "$DLL"
```
