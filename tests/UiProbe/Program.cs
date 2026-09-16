using System.IO;
using System.Reflection;
using Expr = System.Linq.Expressions.Expression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;

namespace DesktopPet.Tests.UiProbe;

/// <summary>
/// UI 层防回归探针（需真实窗口，跑在 STA 线程，运行时会短暂弹出宠物窗口）。
/// 守护两个曾经真实踩过、且无头测试（LoaderTest）覆盖不到的坑：
///   1. 宠物窗口尺寸 —— 曾用 SizeToContent + 无 MeasureOverride 的裸宿主，窗口被量成 12.8px，猫几乎不可见
///   2. 径向菜单反复开关 —— 曾用 Close() 销毁，关闭期间再 Show 抛 InvalidOperationException，直接崩掉整个应用
/// 运行：dotnet run --project tests/UiProbe
/// </summary>
internal static class Program
{
    private static int _fail;

    [STAThread]
    private static void Main()
    {
        // v0.1.82：stdout 重定向到文件时是**块缓冲**——一旦进程不退出，最后的输出会留在缓冲区里
        // 看不见（症状：日志停在某一项、没有结尾那行，容易误判成"卡在那一项"）。
        // 改成 AutoFlush，保证结果随时可读。
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Paths.EnsureCreated();

        // ---- 1. 径向菜单反复开关（复现旧崩溃：Close 期间 Show） ----
        var menu = new RadialMenu(new UIApiImpl(_ => null), new GlobalInput(), new AppConfig());
        Check("径向菜单 50 次 Show/Hide", () =>
        {
            for (int i = 0; i < 50; i++) { menu.ShowAt(300, 300); menu.HideMenu(); }
        });
        Check("径向菜单 连点 10 次（已显示时重复打开）", () =>
        {
            menu.ShowAt(300, 300);
            for (int i = 0; i < 10; i++) menu.ShowAt(300 + i * 5, 300);
            menu.HideMenu();
        });

        // ---- 2. 宠物窗口端到端（真实素材 + 真实渲染路径） ----
        var config = new AppConfig();
        var win = new PetWindow(config);
        win.Show();
        // 强制完成一次布局，确保 ActualWidth/Height 已计算
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        // v0.1.46：触发 LoadPlugins（同步 LoadCore + 异步 LoadDeferred）—— 直接调避免等 Loaded 异步
        // UiProbe bin/plugins/ 由 csproj None 复制保证存在
        win.LoadPlugins();

        var w = win.ActualWidth;
        var h = win.ActualHeight;
        var expectedW = config.Scale * SpriteAtlas.FrameW;
        var expectedH = config.Scale * SpriteAtlas.FrameH;
        Check($"宠物窗口尺寸 {w}x{h}（期望 {expectedW}x{expectedH}）", () =>
        {
            if (Math.Abs(w - expectedW) > 0.5 || Math.Abs(h - expectedH) > 0.5)
                throw new Exception($"实际 {w}x{h}，期望 {expectedW}x{expectedH}");
        });

        // ---- 2.5 v0.1.26 位置记忆：config 记录的位置 → 启动恢复到该位置 ----
        var config2 = new AppConfig { PosX = 200, PosY = 150 };
        var win2 = new PetWindow(config2);
        win2.Show();
        win2.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Check($"位置记忆恢复（期望 Left=200 Top=150，实际 {win2.Left:F0}x{win2.Top:F0}）", () =>
        {
            if (Math.Abs(win2.Left - 200) > 1 || Math.Abs(win2.Top - 150) > 1)
                throw new Exception($"实际 {win2.Left}x{win2.Top}（位置记忆可能回退）");
        });
        win2.Dispose();
        win2.Close();

        // ---- 2.6 v0.1.27 游戏模式全屏判定纯逻辑 ----
        Check("游戏模式全屏判定：盖满 ≥98% / 普通窗口 / 最小化窗口", () =>
        {
            if (!PetWindow.CoversMonitor(0, 0, 1920, 1080, 0, 0, 1920, 1080))
                throw new Exception("精确全屏未命中（判定过严）");
            if (!PetWindow.CoversMonitor(-8, -8, 1928, 1088, 0, 0, 1920, 1080))
                throw new Exception("略超出屏幕的全屏未命中（无边框游戏常见）");
            if (PetWindow.CoversMonitor(0, 0, 1500, 900, 0, 0, 1920, 1080))
                throw new Exception("普通窗口误判为全屏（判定过松）");
            if (PetWindow.CoversMonitor(-32000, -32000, -31840, -31860, 0, 0, 1920, 1080))
                throw new Exception("最小化窗口（-32000 偏移）误判为全屏");
        });

        // ---- 3. 经 PetWindow 的真实调用路径（右键 / 热键） ----
        Check("PetWindow 开/关菜单 20 次", () =>
        {
            for (int i = 0; i < 20; i++) { win.OpenRadialMenu(); win.CloseRadialMenu(); }
        });
        Check("PetWindow ToggleRadialMenu 10 轮（热键路径）", () =>
        {
            for (int i = 0; i < 10; i++) { win.ToggleRadialMenu(); win.ToggleRadialMenu(); }
        });

        // ---- 4. 交互压力（反射调私有方法压测真实逻辑路径） ----
        // 生产代码不动。反射调 StartFall/Pet/AttachEdge/DetachEdge 验证修复在压力下不崩。
        var winType = typeof(PetWindow);
        var bf = BindingFlags.NonPublic | BindingFlags.Instance;
        var startFall = winType.GetMethod("StartFall", bf) ?? throw new MissingMethodException("StartFall 不存在");
        var petMethod = winType.GetMethod("Pet", bf) ?? throw new MissingMethodException("Pet 不存在");
        var attachEdge = winType.GetMethod("AttachEdge", bf) ?? throw new MissingMethodException("AttachEdge 不存在");
        var detachEdge = winType.GetMethod("DetachEdge", bf) ?? throw new MissingMethodException("DetachEdge 不存在");
        var fallTimerField = winType.GetField("_fallTimer", bf) ?? throw new MissingFieldException("_fallTimer 字段不存在");

        Check("拖拽甩动压测：StartFall 连续 30 次（验证落体定时器不再叠加）", () =>
        {
            for (int i = 0; i < 30; i++)
                startFall.Invoke(win, new object[] { new Vector(20 + i % 10, 100 + i) });
        });
        Check("抚摸压测：Pet 连续 50 次", () =>
        {
            for (int i = 0; i < 50; i++) petMethod.Invoke(win, null);
        });
        Check("边缘吸附压测：AttachEdge/DetachEdge 各 20 次", () =>
        {
            for (int i = 0; i < 20; i++)
            {
                attachEdge.Invoke(win, new object[] { i % 4, 100.0 + i, 100.0 + i });
                detachEdge.Invoke(win, null);
            }
        });
        Check("落体定时器字段持有正确（非 null，证明 #2 修复没回退）", () =>
        {
            var t = fallTimerField.GetValue(win);
            if (t is null) throw new Exception("_fallTimer 为 null（修复可能回退：StartFall 后未持有定时器）");
        });

        // ---- 5. 槽位图标 PNG 缓存（v0.1.8：PNG 化不依赖系统 emoji 字体） ----
        Check("槽位图标 PNG 缓存加载成功（icons/ 目录 + default 回退）", () =>
        {
            var cacheField = typeof(RadialMenu).GetField("_iconCache", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("RadialMenu._iconCache 不存在（PNG 化可能回退）");
            var cache = cacheField.GetValue(menu) as System.Collections.Generic.Dictionary<string, System.Windows.Media.Imaging.BitmapImage>;
            if (cache is null) throw new Exception("_iconCache 为 null");
            if (cache.Count == 0) throw new Exception("图标缓存为空（neko-assets/icons/ 未加载）");
            if (!cache.ContainsKey("default")) throw new Exception("缺 default.png 回退图标");
        });

        // ---- 6. v0.1.25：22 槽位分页（每页 ≤8）+ 渲染像素验证（圆环挖洞、底盘能看清） ----
        var probeUi = new UIApiImpl(_ => null);
        using (probeUi.PushScope("probe"))
        {
            for (int i = 0; i < 22; i++)
            {
                var idx = i;
                probeUi.RegisterRadialSlot(new DesktopPet.Core.RadialSlot
                {
                    Id = $"probe{i}",
                    Label = $"项目{idx}",
                    Icon = "default",
                    Order = idx,
                    OnInvoke = () => { },
                });
            }
        }
        var menu2 = new RadialMenu(probeUi, new GlobalInput(), new AppConfig());
        menu2.ShowAt(500, 400, 55.2); // 模拟 3.2x 猫（半宽 51.2 + 4）

        Check("径向菜单 22 槽位分页：单页 ≤ 8，翻页 5 轮不崩", () =>
        {
            var canvasField = typeof(RadialMenu).GetField("_canvas", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("RadialMenu._canvas 不存在");
            var canvas = (System.Windows.Controls.Canvas)canvasField.GetValue(menu2)!;
            var next = typeof(RadialMenu).GetMethod("NextPage", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("RadialMenu.NextPage 不存在");
            for (int p = 0; p < 5; p++) next.Invoke(menu2, null);
            int slotCount = 0;
            for (int ci = 0; ci < canvas.Children.Count; ci++)
                if (canvas.Children[ci].GetType().Name == "SlotView") slotCount++;
            if (slotCount > 8) throw new Exception($"单页槽位 {slotCount} 个 > 8（分页失效，会再叠成一团）");
        });

        Check("径向菜单渲染像素：中心洞透明（透出猫）+ 底盘近乎不透明（能看清）", () =>
        {
            // v0.1.30：径向菜单加了 200ms 淡入动画——Sleep 会阻塞 STA 消息循环导致动画停摆，
            // 必须用 DispatcherFrame 让消息循环跑起来，Timer Tick 后再断言
            var frame = new DispatcherFrame();
            var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(260),
            };
            t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
            menu2.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            int w = (int)Math.Ceiling(menu2.ActualWidth);
            int h = (int)Math.Ceiling(menu2.ActualHeight);
            if (w <= 0 || h <= 0) throw new Exception($"菜单尺寸异常 {w}x{h}");
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(menu2);
            var pngPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neko-radial-v0.1.25.png");
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(pngPath)) enc.Save(fs);

            var pixels = new byte[w * h * 4];
            rtb.CopyPixels(pixels, w * 4, 0);
            int AlphaAt(int x, int y) => pixels[4 * (y * w + x) + 3];

            // 多页时窗口顶部多一条 HeaderH=26 的胶囊条，环心 y = header + ring/2
            var pageCountField = typeof(RadialMenu).GetField("_pageCount", BindingFlags.NonPublic | BindingFlags.Instance);
            int pageCount = pageCountField is null ? 1 : (int)(pageCountField.GetValue(menu2) ?? 1);
            int header = pageCount > 1 ? 26 : 0;
            int cy = header + (h - header) / 2;
            int holeA = AlphaAt(w / 2, cy);          // 圆心 = 挖洞区 → 应透明（透出猫）
            int plateRow = cy - 57;                   // 洞外 12px 的底盘留白带（55.2 洞半径 + 2）→ 必为纯底盘，不落槽位
            int plateA = AlphaAt(w / 2, plateRow);
            int plateB = pixels[4 * (plateRow * w + w / 2) + 2];
            if (holeA > 20) throw new Exception($"中心洞不透明 alpha={holeA}（挖洞失效，猫会被挡住）");
            if (plateA < 220) throw new Exception($"底盘太透 alpha={plateA}（看不清，用户要求能看清）");
            if (plateB < 200) throw new Exception($"底盘颜色异常 B={plateB}（应近白）");
            Console.WriteLine($"      [像素] 洞 alpha={holeA}（应≈0），底盘 alpha={plateA} B={plateB}（应≈255）；截图: {pngPath}");
        });

        menu2.HideMenu();
        menu2.Close();

        // ---- v0.1.28 第 16 项：键鼠映射窗（BongoCat 核心视觉）----
        Check("键鼠映射窗：66 键注册 + 鼠标 3 区 + 高亮/拍爪切换", () =>
        {
            var gi = new GlobalInput();          // 不 Install，纯 UI 测试
            var mcfg = new AppConfig();
            var mirror = new InputMirrorWindow(gi, mcfg, () => { });
            mirror.Show();
            mirror.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            if (mirror.RegisteredKeys != 66)
                throw new Exception($"注册键位 {mirror.RegisteredKeys} ≠ 66（vk 冲突被吞，键盘会缺键）");
            // v0.1.36：布局不重叠不溢出（v0.1.28 Grid 列溢出 bug 的回归守护）；v0.1.44：键盘画布缩小（KbW 反射读，防数字漂移）
            double kbw = (double)typeof(InputMirrorWindow).GetField("KbW", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            if (mirror.LayoutHasOverlaps())
                throw new Exception("键帽矩形存在重叠（Grid 列溢出：1.5u/2.25u 宽键盖住相邻键）");
            if (mirror.LayoutMaxRight() > kbw + 0.5)
                throw new Exception($"最右键帽右缘 {mirror.LayoutMaxRight()} > {kbw}（超出键盘画布，会被截断）");
            if (mirror.RegisteredMouseKeys != 3)
                throw new Exception($"鼠标区 {mirror.RegisteredMouseKeys} ≠ 3");
            // v0.1.29：爪子素材 + 敲键拍爪状态切换
            if (!mirror.PawAssetsLoaded)
                throw new Exception("爪子素材未加载（neko-assets/mirror/paw_up.png 等缺失）");
            mirror.FlashKey(0x41, true);
            if (!mirror.IsKeyboardPawDown)
                throw new Exception("敲键后键盘爪未压下（130ms 回弹定时器或帧切换异常）");
            mirror.FlashKey(0x41, false);
            // v0.1.35：鼠标键动画不能崩（Brushes.Transparent 冻结刷 + BeginAnimation 曾崩溃的守护）
            mirror.FlashMouse(0, true);
            mirror.FlashMouse(0, false);
            mirror.FlashMouse(2, true);
            mirror.FlashMouse(2, false);
            mirror.Close();
            gi.Dispose();
        });

        // ---- v0.1.38 第 17 项：设置页构建 + 5 section 切换（v0.1.31 深色重写后从未被 probe 覆盖）----
        Check("设置页：构建 + 5 个 section 切换不崩", () =>
        {
            var settings = new SettingsWindow(win);
            settings.Show();
            settings.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            // v0.1.39：ShowSection 改 public（托盘「关于」要直接定位），反射 flags 必须带 Public
            var showSection = typeof(SettingsWindow).GetMethod("ShowSection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("SettingsWindow.ShowSection 不存在");
            foreach (var sec in new[] { "general", "pet", "input", "plugin", "about" })
            {
                showSection.Invoke(settings, new object[] { sec });
                settings.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            }
            settings.Close();
        });

        // ---- v0.1.71 第 17b 项：设置页「径向菜单」构建 + 关键控件 ----
        Check("v0.1.71(径向菜单)：设置页构建 + 关键控件（数量上限 / 顺序显隐列表 / 恢复默认）", () =>
        {
            var settings = new SettingsWindow(win);
            settings.Show();
            settings.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var showSection = typeof(SettingsWindow).GetMethod("ShowSection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("SettingsWindow.ShowSection 不存在");
            showSection.Invoke(settings, new object[] { "radial" });
            settings.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var mainContent = (ContentControl)typeof(SettingsWindow).GetField("_mainContent", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(settings)!;
            if (mainContent.Content is not ScrollViewer)
                throw new Exception($"径向菜单页应可滚动（ScrollViewer），实际 {mainContent.Content?.GetType().Name}");

            var texts = new List<TextBlock>();
            Collect(mainContent.Content, texts);
            if (!texts.Any(t => t.Text.Contains("最多显示槽位数")))
                throw new Exception("缺「最多显示槽位数」数量上限 stepper");
            if (!texts.Any(t => t.Text.Contains("插件顺序与显隐")))
                throw new Exception("缺「插件顺序与显隐」小节标题");
            if (!texts.Any(t => t.Text.Contains("恢复默认")))
                throw new Exception("缺「恢复默认」按钮");
            settings.Close();
        });

        // ---- v0.1.39 第 18 项：双模态绑定猫位置（灯灯点名的"猫瞬移 / 轮盘不跟随猫"回归守护）----
        Check("v0.1.39：键盘镜像 + 轮盘跟随猫 + 映射窗以猫为锚", () =>
        {
            // ① 键盘朝向镜像：朝我 = ScaleX 1；朝猫 = ScaleX -1（水平镜像）
            var gi = new GlobalInput();
            var mcfg = new AppConfig();
            var mirror = new InputMirrorWindow(gi, mcfg, () => { });
            mirror.Show();
            mirror.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var rootField = typeof(InputMirrorWindow).GetField("_rootGrid", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("InputMirrorWindow._rootGrid 不存在");
            mirror.SetMirrored(false);
            if (((Grid)rootField.GetValue(mirror)!).RenderTransform is not ScaleTransform st0 || st0.ScaleX != 1.0)
                throw new Exception("键盘朝向=朝我时，内容根 ScaleX 应为 1（未镜像）");
            mirror.SetMirrored(true);
            if (((Grid)rootField.GetValue(mirror)!).RenderTransform is not ScaleTransform st1 || st1.ScaleX != -1.0)
                throw new Exception("键盘朝向=朝猫时，内容根 ScaleX 应为 -1（水平镜像）");
            mirror.Close();
            gi.Dispose();

            // ② 轮盘跟随猫：MoveTo 后圆心必须落在猫中心
            var probe = new RadialMenu(new UIApiImpl(_ => null), new GlobalInput(), new AppConfig());
            probe.ShowAt(500, 400, 55);
            probe.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            probe.MoveTo(800, 600);
            if (Math.Abs(probe.Left - (800 - probe.Width / 2)) > 0.5 || Math.Abs(probe.Top - (600 - probe.Height / 2)) > 0.5)
                throw new Exception($"轮盘未跟随猫：Left={probe.Left} Top={probe.Top}，期望圆心 (800,600)");
            probe.HideMenu();

            // ③ work 模式：映射窗来就猫（水平居中于猫 + 贴在猫下方），猫本身一步都不能动
            win.Left = 400; win.Top = 300;   // 挪到屏幕中部，排除"贴边被 clamp 拉回"的干扰
            var catLeft = win.Left;
            win.SetAppMode("work");
            win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var mirrorField = typeof(PetWindow).GetField("_mirrorWindow", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("PetWindow._mirrorWindow 不存在");
            if (mirrorField.GetValue(win) is not InputMirrorWindow mw || !mw.IsVisible)
                throw new Exception("work 模式未显示键鼠映射窗");
            if (Math.Abs(win.Left - catLeft) > 0.5)
                throw new Exception($"切 work 模式把猫瞬移了：{catLeft} → {win.Left}（应当是映射窗来就猫）");
            if (Math.Abs((mw.Left + mw.Width / 2) - (win.Left + win.Width / 2)) > 1.0)
                throw new Exception("映射窗未水平居中于猫（键盘应以猫视角为主）");
            if (Math.Abs(mw.Top - (win.Top + win.Height + 2)) > 1.5)   // v0.1.43：MirrorGap 6→2（猫几乎趴在键盘上）
                throw new Exception($"映射窗未贴在猫下方：Top={mw.Top}，期望 {win.Top + win.Height + 2}");

            // ④ slack 模式：映射窗必须收起
            win.SetAppMode("slack");
            win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            if (mw.IsVisible) throw new Exception("slack 模式未收起键鼠映射窗");
        });

        // ---- v0.1.40 第 19 项：气泡对话（像素云朵渲染 + 活跃度 + 好感度档位）----
        Check("v0.1.40：像素云朵气泡渲染 + 活跃度统计 + 好感度档位", () =>
        {
            // ① 像素云朵：ShowBubble 后可见、尺寸合理、云朵主体白且不透明、角落透明（穿透）
            var bubble = new BubbleWindow();
            bubble.ShowBubble("测试气泡", 3000);
            var frame = new DispatcherFrame();
            var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(220),   // 等 120ms 淡入完成
            };
            t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
            bubble.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            if (!bubble.IsVisible) throw new Exception("气泡未显示");
            if (bubble.Width < 40 || bubble.Height < 30)
                throw new Exception($"气泡尺寸异常 {bubble.Width}x{bubble.Height}（云朵应 ≥40x30）");
            // 断言前强制 Opacity=1 + 停动画：避免 120ms 淡入动画进行中导致半透明采样误判
            bubble.Opacity = 1;
            bubble.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            bubble.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            int bw = (int)Math.Ceiling(bubble.Width);
            int bh = (int)Math.Ceiling(bubble.Height);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(bw, bh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(bubble);
            var pixels = new byte[bw * bh * 4];
            rtb.CopyPixels(pixels, bw * 4, 0);
            byte A(int x, int y) => pixels[4 * (y * bw + x) + 3];
            // 扫云朵主体区域找一个非透明白块（避开文字 X=18..66 Y=14..30）——
            // WPF 窗口渲染到 RTB 有 sRGB/gamma 微差（实测 B≈225，非纯 #FFF9EC 的 236），不卡死阈值
            int foundX = -1, foundY = -1;
            for (int y = 8; y < 35 && foundX < 0; y += 2)
                for (int x = 1; x < bw - 1 && foundX < 0; x += 2)
                {
                    if (x >= 18 && x <= 66 && y >= 14 && y <= 30) continue;  // 文字区
                    if (A(x, y) >= 200) { foundX = x; foundY = y; }
                }
            if (foundX < 0)
                throw new Exception($"云朵主体找不到非透明白块（尺寸 {bw}x{bh}，扫了主体区）");
            if (A(1, 1) > 40)
                throw new Exception($"气泡角落不透明 alpha={A(1, 1)}（Background 应为 null 穿透）");
            bubble.Dismiss();

            // ② 活跃度：同分钟多次输入只计 1 活跃分钟（分钟级去重）
            var actFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neko-act-test.json");
            System.IO.File.Delete(actFile);
            var act = new ActivityTracker(actFile);
            act.NotifyInput();
            act.NotifyInput();
            act.NotifyInput();
            if (act.TodayActiveMinutes != 1)
                throw new Exception($"同分钟 3 次输入应只计 1 活跃分钟，实际 {act.TodayActiveMinutes}");
            System.IO.File.Delete(actFile);

            // ③ 好感度档位（ChatScheduler 读 score.json → 0..3，阈值对齐 AffectionPlugin）
            var affDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neko-aff-test");
            System.IO.Directory.CreateDirectory(affDir);
            var affFile = System.IO.Path.Combine(affDir, "score.json");
            var sched = new ChatScheduler((_, _) => { }, act, affFile, 45, "测试猫", "测试主人");   // v0.1.41：新增 catName/ownerTitle 参数
            var lv = typeof(ChatScheduler).GetMethod("ReadAffectionLevel", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("ChatScheduler.ReadAffectionLevel 不存在");
            System.IO.File.WriteAllText(affFile, "5");
            if ((int)(lv.Invoke(sched, null) ?? -1) != 0) throw new Exception("好感度 5 应=0 陌生");
            System.IO.File.WriteAllText(affFile, "60");
            if ((int)(lv.Invoke(sched, null) ?? -1) != 2) throw new Exception("好感度 60 应=2 亲密");
            System.IO.File.WriteAllText(affFile, "999");
            if ((int)(lv.Invoke(sched, null) ?? -1) != 3) throw new Exception("好感度 999 应=3 挚友");
            System.IO.Directory.Delete(affDir, true);
        });

        // ---- v0.1.41 第 20 项：首启引导（猫名/称呼 Config 字段 + ChatScheduler 模板替换 + FirstRunWindow 构建）----
        Check("v0.1.41：首启引导窗口构建 + 称呼/猫名注入台词", () =>
        {
            // ① Config 字段存在（首启引导存这里）
            if (typeof(AppConfig).GetProperty("CatName") is null)
                throw new Exception("Config 缺 CatName 字段");
            if (typeof(AppConfig).GetProperty("OwnerTitle") is null)
                throw new Exception("Config 缺 OwnerTitle 字段");

            // ② FirstRunWindow ctor 不崩（不 ShowDialog，只构造——ShowDialog 会要求人工输入）
            var onboardingCfg = new AppConfig();
            var onboarding = new FirstRunWindow(onboardingCfg);
            onboarding.Close();

            // ③ ChatScheduler 模板替换：反射 Pick + 反射 RestLines/ChatLines
            var act = new ActivityTracker(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neko-u20-act.json"));
            System.IO.File.Delete(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neko-u20-act.json"));
            var sched = new ChatScheduler((_, _) => { }, act, "", 45, "奶糖", "主人");
            var pick = typeof(ChatScheduler).GetMethod("Pick", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("ChatScheduler.Pick 不存在");
            // 拿 L2/L3 台词表（私有 static string[][]）
            string[][] GetLines(string name) =>
                (string[][])(typeof(ChatScheduler).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) ?? throw new MissingFieldException(name));
            // 分开断言：L2 Rest 含 {0}=称呼、L3 Chat 含 {1}=猫名（v0.1.41 设计：L0/L1 不融入；L2 亲昵喊称呼；L3 挚友聊猫名）
            bool hitOwner = false;
            for (int i = 0; i < 200 && !hitOwner; i++)
            {
                var s = (string)pick.Invoke(sched, new object[] { GetLines("RestLines")[2] })!;
                if (s.Contains("主人")) hitOwner = true;
            }
            if (!hitOwner) throw new Exception("L2 Rest 至少一条应含 {0}→主人（亲昵档）");
            bool hitCat = false;
            for (int i = 0; i < 200 && !hitCat; i++)
            {
                var s = (string)pick.Invoke(sched, new object[] { GetLines("ChatLines")[3] })!;
                if (s.Contains("奶糖")) hitCat = true;
            }
            if (!hitCat) throw new Exception("L3 Chat 至少一条应含 {1}→奶糖（挚友档）");
            // L0 陌生档故意不融入——L0 数组不应含 {0}/{1}
            var rest0 = GetLines("RestLines")[0];
            if (rest0.Any(s => s.Contains("{0}") || s.Contains("{1}"))) throw new Exception("L0 陌生档不应融入称呼/猫名（生硬）");
        });

        // ---- v0.1.43 第 21 项：猫爪跟随按键（爪滑到被按的键上方，不再钉死固定位）----
        Check("v0.1.43：猫爪跟随按键（键盘爪滑到键上 / 鼠标爪滑到按钮上）", () =>
        {
            var gi = new GlobalInput();
            var mirror = new InputMirrorWindow(gi, new AppConfig(), () => { });
            mirror.Show();
            mirror.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            // 反射拿内部常量与键表（避免硬编码数字漂移）
            double pawSize = (double)typeof(InputMirrorWindow).GetField("PawSize", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            double kbw = (double)typeof(InputMirrorWindow).GetField("KbW", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            double mg = (double)typeof(InputMirrorWindow).GetField("MouseGap", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            double mw = (double)typeof(InputMirrorWindow).GetField("MouseW", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var keys = (Dictionary<uint, System.Windows.Controls.Border>)typeof(InputMirrorWindow)
                .GetField("_keyByVk", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mirror)!;
            double KeyCenter(uint vk) => Canvas.GetLeft(keys[vk]) + keys[vk].Width / 2;
            double ClampX(double center) => Math.Clamp(center - pawSize / 2, 0, kbw - pawSize);

            // 按 A → 爪悬停到 A 键中心上方
            mirror.FlashKey(0x41, true);
            double expA = ClampX(KeyCenter(0x41));
            if (Math.Abs(mirror.KeyboardPawLeftPos - expA) > 2)
                throw new Exception($"按 A 爪应滑到 {expA:F0}，实际 {mirror.KeyboardPawLeftPos:F0}");
            // 按 Esc（左上角）→ 目标显著变化（爪跟键走，不再钉死 170）
            mirror.FlashKey(0x1B, true);
            double expEsc = ClampX(KeyCenter(0x1B));
            if (Math.Abs(mirror.KeyboardPawLeftPos - expEsc) > 2)
                throw new Exception($"按 Esc 爪应滑到 {expEsc:F0}，实际 {mirror.KeyboardPawLeftPos:F0}");
            // 鼠标右键 → 爪滑到右键中心上方
            mirror.FlashMouse(1, true);
            double expR = kbw + mg + mw / 4 * 3 - pawSize / 2;
            if (Math.Abs(mirror.MousePawLeftPos - expR) > 2)
                throw new Exception($"按鼠标右键爪应滑到 {expR:F0}，实际 {mirror.MousePawLeftPos:F0}");
            // 抬起后悬停位保持（不飞回固定位）
            mirror.FlashMouse(1, false);
            if (Math.Abs(mirror.MousePawLeftPos - expR) > 2)
                throw new Exception($"鼠标爪抬起后应停在右键上方 {expR:F0}，实际 {mirror.MousePawLeftPos:F0}");

            // v0.1.44：敲键后 130ms 爪必须举回"举手位"（键盘上方之外），不能悬停在键上挡键盘
            var pawField = typeof(InputMirrorWindow).GetField("_kbdPaw", BindingFlags.NonPublic | BindingFlags.Instance)!
                ?? throw new MissingFieldException("InputMirrorWindow._kbdPaw 不存在");
            var paw = (System.Windows.Controls.Image?)pawField.GetValue(mirror);
            double pawRaised = (double)typeof(InputMirrorWindow).GetField("PawRaisedTop", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            if (paw is null) throw new Exception("键盘爪未创建");
            mirror.FlashKey(0x41, true);   // 敲 A
            var frame2 = new DispatcherFrame();
            var t2 = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(220),   // 等 130ms 回弹定时器跑完
            };
            t2.Tick += (_, _) => { t2.Stop(); frame2.Continue = false; };
            t2.Start();
            Dispatcher.PushFrame(frame2);
            mirror.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            double pawTop = Canvas.GetTop(paw);
            if (pawTop < pawRaised - 1)
                throw new Exception($"爪应举回举手位（Top={pawRaised}，键盘上方之外），实际 Top={pawTop:F0}（挡住键盘了）");

            mirror.Close();
            gi.Dispose();
        });

        // ---- v0.1.46 第 22 项：插件启用/禁用开关（运行时 Disable/Enable 即时生效）----
        Check("v0.1.46：插件开关（Disable 卸载 + 撤销 UI + IsEnabled；Enable 重载）", () =>
        {
            if (win.PluginHost.Plugins.Count == 0)
                throw new Exception("没有可用插件可测");

            // 拿第一个插件做开关测试（彩蛋插件，方便数据稳定）
            string id = win.PluginHost.Plugins[0].Manifest.Id;
            int before = win.PluginHost.Plugins.Count;

            try
            {
                // ① 禁用 → 立刻从 Plugins 消失 + IsEnabled=false
                if (!win.PluginHost.DisablePlugin(id))
                    throw new Exception($"DisablePlugin({id}) 失败");
                if (win.PluginHost.IsEnabled(id))
                    throw new Exception("DisablePlugin 后 IsEnabled 应=false");
                if (win.PluginHost.Plugins.Any(p => p.Manifest.Id == id))
                    throw new Exception("DisablePlugin 后 Plugins 列表应不含该 id");
                if (win.PluginHost.Plugins.Count != before - 1)
                    throw new Exception($"Plugins 数 {before} → {win.PluginHost.Plugins.Count}（应减 1）");

                // ② 启用 → 重新加载 + IsEnabled=true + Plugins 含 id + 总数恢复
                if (!win.PluginHost.EnablePlugin(id))
                    throw new Exception($"EnablePlugin({id}) 失败（详见 PluginHost.Errors）");
                if (!win.PluginHost.IsEnabled(id))
                    throw new Exception("EnablePlugin 后 IsEnabled 应=true");
                if (!win.PluginHost.Plugins.Any(p => p.Manifest.Id == id))
                    throw new Exception("EnablePlugin 后 Plugins 列表应含该 id");
                if (win.PluginHost.Plugins.Count != before)
                    throw new Exception($"Plugins 数 {before} → {win.PluginHost.Plugins.Count}（应恢复）");
            }
            finally
            {
                // 收尾：恢复初始状态（避免污染后续项）
                if (!win.PluginHost.IsEnabled(id))
                    win.PluginHost.EnablePlugin(id);
            }
        });

        // ---- v0.1.67 第 25 项：命理插件 ChatWindow（纯 C# 独立窗口：构造 + 控件 + 失败路径可读提示）----
        // 教训（v0.1.32）：新 UI 功能漏加 UiProbe 断言，真机 11 分钟后才崩。此处守护 ChatWindow 的构造与错误分支。
        Check("v0.1.67：命理插件 ChatWindow（构造 + 关键控件 + 失败路径可读提示）", () =>
        {
            var asm = ResolveBaziAssembly(win);

            var chatWindowType = asm.GetType("DesktopPet.Builtin.Bazi.ChatWindow")
                ?? throw new Exception("ChatWindow 类型未找到");
            var dsType = asm.GetType("DesktopPet.Builtin.Bazi.DeepSeekClient")
                ?? throw new Exception("DeepSeekClient 类型未找到");
            var msgType = asm.GetType("DesktopPet.Builtin.Bazi.ChatMessage")
                ?? throw new Exception("ChatMessage 类型未找到");
            var resultType = asm.GetType("DesktopPet.Builtin.Bazi.ChatResult")
                ?? throw new Exception("ChatResult 类型未找到");

            // 空历史（List<ChatMessage> 实现 IReadOnlyList<ChatMessage>）
            var emptyHistory = Activator.CreateInstance(typeof(List<>).MakeGenericType(msgType))!;

            // ask 委托 = q => DeepSeekClient.ChatAsync(null, 空消息) —— 走「空 Key → 可读错误」分支，**不发网络请求**
            var chatAsync = dsType.GetMethod("ChatAsync", BindingFlags.Public | BindingFlags.Static)
                ?? throw new Exception("DeepSeekClient.ChatAsync 未找到");
            var messagesParam = chatAsync.GetParameters()[1].ParameterType;   // IReadOnlyList<ChatMessage>
            var taskResultType = typeof(Task<>).MakeGenericType(resultType);
            var funcType = typeof(Func<,>).MakeGenericType(typeof(string), taskResultType);

            var q = Expr.Parameter(typeof(string), "q");
            var call = Expr.Call(
                chatAsync,
                Expr.Constant(null, typeof(string)),
                Expr.Constant(emptyHistory, messagesParam));
            var ask = Expr.Lambda(funcType, call, q).Compile();

            // 反射构造 + 显示
            var ctor = chatWindowType.GetConstructors().Single();
            var window = (Window)ctor.Invoke(new object?[] { emptyHistory, ask });
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);   // 触发 Loaded + 布局

            try
            {
                // 关键控件存在
                var input = (TextBox)(chatWindowType.GetField("_input", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)
                    ?? throw new Exception("缺 _input 输入框"));
                var send = (Button)(chatWindowType.GetField("_sendBtn", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)
                    ?? throw new Exception("缺 _sendBtn 发送按钮"));
                var messages = (StackPanel)(chatWindowType.GetField("_messages", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)
                    ?? throw new Exception("缺 _messages 对话区"));
                if ((send.Content as string) != "发送")
                    throw new Exception($"发送按钮文案应为「发送」，实际「{send.Content}」");
                if (messages.Children.Count == 0)
                    throw new Exception("对话区应有初始引导气泡");

                // 失败路径：填问题 → 反射调 Send() → 断言出现可读失败提示（不抛异常）
                input.Text = "我今年适合换工作吗？";
                var sendMi = chatWindowType.GetMethod("Send", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new Exception("缺 Send 方法");
                sendMi.Invoke(window, null);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);   // 冲掉 BeginInvoke 的滚动

                var texts = CollectBubbleTexts(messages);
                if (!texts.Any(t => t.Contains("换工作")))
                    throw new Exception($"未见用户问题气泡，现有：{string.Join(" | ", texts)}");
                if (!texts.Any(t => t.Contains("API Key")))
                    throw new Exception($"失败路径未见可读提示（应含「API Key」），现有：{string.Join(" | ", texts)}");
                if (input.Text.Length != 0)
                    throw new Exception("发送后输入框应已清空");
                if (!send.IsEnabled)
                    throw new Exception("发送完成后按钮应恢复可用");
            }
            finally
            {
                window.Close();
            }
        });

        // ---- v0.1.73 第 26 项：命理插件人格定制（默认关闭 + 93 题 + 开关/开始测试/直接选择显隐）----
        // 只构建 + 校验结构，**不点提交**（提交会弹 MessageBox，会阻塞探针）。
        Check("v0.1.73：命理插件人格定制（默认关闭 + 93 题 + 入口显隐 + 问卷窗口控件）", () =>
        {
            var asm = ResolveBaziAssembly(win);
            var pageType = asm.GetType("DesktopPet.Builtin.Bazi.BaziSettingsPage")
                ?? throw new Exception("BaziSettingsPage 类型未找到");
            var quizType = asm.GetType("DesktopPet.Builtin.Bazi.MbtiQuiz")
                ?? throw new Exception("MbtiQuiz 类型未找到");

            var page = pageType.GetConstructor(new[] { typeof(DesktopPet.Core.IPluginStorage) })
                ?.Invoke(new object[] { new ProbeStorage() })
                ?? throw new Exception("BaziSettingsPage 构造失败");
            var content = pageType.GetMethod("BuildContent")?.Invoke(page, null)
                ?? throw new Exception("BuildContent 返回 null");

            if (content is not ScrollViewer)
                throw new Exception($"设置页应可滚动（返回 ScrollViewer），实际 {content.GetType().Name}");

            var buttons = new List<Button>();
            Collect(content, buttons);
            var texts = new List<TextBlock>();
            Collect(content, texts);
            var checkboxes = new List<CheckBox>();
            Collect(content, checkboxes);
            var combos = new List<ComboBox>();
            Collect(content, combos);

            // 题库应为 93 题
            var questions = quizType.GetProperty("Questions")!.GetValue(null)!;
            var questionCount = ((System.Collections.ICollection)questions).Count;
            if (questionCount != 93)
                throw new Exception($"题库应为 93 题，实际 {questionCount}");

            // 开关默认关
            var mbtiCheck = checkboxes.FirstOrDefault(c => (c.Content as string)?.Contains("启用人格定制") == true)
                ?? throw new Exception("缺「启用人格定制」开关");
            if (mbtiCheck.IsChecked == true)
                throw new Exception("人格定制开关应默认关闭");

            // 入口：开始测试按钮 + 直接选择下拉（16 型）；默认关闭时应隐藏
            var quizBtn = buttons.FirstOrDefault(b => (b.Content as string) == "开始测试")
                ?? throw new Exception("缺「开始测试」按钮");
            var typeCombo = combos.FirstOrDefault(c => c.Items.Count == 16)
                ?? throw new Exception("缺「直接选择」下拉框（16 型）");
            if (quizBtn.Visibility != Visibility.Collapsed || typeCombo.Visibility != Visibility.Collapsed)
                throw new Exception("默认关闭时「开始测试」「直接选择」应隐藏");

            // 状态行默认「未开启」
            if (!texts.Any(t => t.Text.Contains("未开启")))
                throw new Exception("默认关闭时状态行应含「未开启」");

            // 开启开关 → 入口显示 + 状态行变「已开启」
            mbtiCheck.IsChecked = true;
            if (quizBtn.Visibility != Visibility.Visible || typeCombo.Visibility != Visibility.Visible)
                throw new Exception("开启开关后「开始测试」「直接选择」应显示");
            if (!texts.Any(t => t.Text.Contains("已开启")))
                throw new Exception("开启开关后状态行应含「已开启」");

            // 再次关闭 → 入口隐藏
            mbtiCheck.IsChecked = false;
            if (quizBtn.Visibility != Visibility.Collapsed || typeCombo.Visibility != Visibility.Collapsed)
                throw new Exception("关闭开关后「开始测试」「直接选择」应再次隐藏");

            // ---- MbtiQuizWindow：独立弹窗 + 关键控件 ----
            var quizWinType = asm.GetType("DesktopPet.Builtin.Bazi.MbtiQuizWindow")
                ?? throw new Exception("MbtiQuizWindow 类型未找到");
            var quizCtor = quizWinType.GetConstructor(new[] { typeof(Action<string>) })
                ?? throw new Exception("MbtiQuizWindow 构造签名不符");
            var quizWin = (Window?)quizCtor.Invoke(new object[] { new Action<string>(_ => { }) })
                ?? throw new Exception("MbtiQuizWindow 构造失败");
            quizWin.Show();
            quizWin.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);   // 触发 Loaded → ShowQuestion(0)
            try
            {
                var progress = (ProgressBar)(quizWinType.GetField("_progress", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(quizWin)
                    ?? throw new Exception("缺 _progress 进度条"));
                if (Math.Abs(progress.Maximum - 93) > 0.5)
                    throw new Exception($"进度条最大值应为 93，实际 {progress.Maximum}");

                var questionText = (TextBlock)(quizWinType.GetField("_questionText", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(quizWin)
                    ?? throw new Exception("缺 _questionText 题目区"));
                if (string.IsNullOrWhiteSpace(questionText.Text))
                    throw new Exception("题目区不应为空");

                var optA = (RadioButton)(quizWinType.GetField("_optA", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(quizWin)
                    ?? throw new Exception("缺 _optA 选项"));
                var optB = (RadioButton)(quizWinType.GetField("_optB", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(quizWin)
                    ?? throw new Exception("缺 _optB 选项"));
                if (optA.GroupName != "mbti" || optB.GroupName != "mbti")
                    throw new Exception("两个选项应同组（GroupName=mbti）");

                var prevBtn = (Button)(quizWinType.GetField("_prevBtn", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(quizWin)
                    ?? throw new Exception("缺 _prevBtn 上一题"));
                var nextBtn = (Button)(quizWinType.GetField("_nextBtn", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(quizWin)
                    ?? throw new Exception("缺 _nextBtn 下一题"));
                if ((prevBtn.Content as string) != "上一题" || prevBtn.IsEnabled)
                    throw new Exception("第一题时「上一题」应存在且禁用");
                if ((nextBtn.Content as string) != "下一题" || !nextBtn.IsEnabled)
                    throw new Exception("第一题时「下一题」应存在且可用");
            }
            finally
            {
                quizWin.Close();
            }
        });

        // ---- v0.1.74 第 23 项：皮肤目录可扩展（SkinRegistry 扫目录 + skin.json 驱动）----
        Check("v0.1.74：皮肤（skin.json 扫目录发现；pixel/soft 元数据与旧硬编码一致）", () =>
        {
            // 1) 至少扫到 pixel + soft
            var skins = SkinRegistry.Scan(Paths.Assets);
            if (skins.Count < 2) throw new Exception($"应至少扫到 2 个皮肤，实际 {skins.Count}");
            var pixel = skins.FirstOrDefault(s => s.Id == "pixel") ?? throw new Exception("未扫到 pixel 皮肤");
            var soft = skins.FirstOrDefault(s => s.Id == "soft") ?? throw new Exception("未扫到 soft 皮肤");

            // 2) skin.json 与旧 C# 硬编码逐项一致（防提取时抄错）
            var expectedPixel = new (string name, double fps, string? eye)[]
            {
                ("idle", 4, "eyes_open"), ("walk", 8, "eyes_open"), ("sit", 4, "eyes_open"),
                ("sleep", 1, null), ("pet", 10, "eyes_pet"), ("drag", 6, "eyes_scared"),
                ("fall", 12, "eyes_scared"), ("climb", 4, "eyes_open"), ("work", 2, "eyes_open"),
            };
            if (pixel.States.Count != expectedPixel.Length)
                throw new Exception($"pixel states 应 {expectedPixel.Length} 个，实际 {pixel.States.Count}");
            foreach (var (name, fps, eye) in expectedPixel)
            {
                var st = pixel.States.FirstOrDefault(s => s.Name == name) ?? throw new Exception($"pixel 缺状态 {name}");
                if (Math.Abs(st.Fps - fps) > 1e-9) throw new Exception($"pixel.{name}.fps 应 {fps}，实际 {st.Fps}");
                if (st.EyeLayer != eye) throw new Exception($"pixel.{name}.eyeLayer 应 {eye ?? "null"}，实际 {st.EyeLayer ?? "null"}");
            }
            if (pixel.FrameWidth != 32 || pixel.FrameHeight != 32 || pixel.Frames != 4)
                throw new Exception($"pixel 应 32x32x4，实际 {pixel.FrameWidth}x{pixel.FrameHeight}x{pixel.Frames}");
            if (!pixel.HasShadow) throw new Exception("pixel 应有影子层");

            var expectedSoft = new (string name, double fps)[]
            {
                ("idle", 4), ("walk", 8), ("sit", 4), ("sleep", 1), ("pet", 10),
                ("drag", 6), ("fall", 12), ("climb", 4), ("work", 2),
            };
            if (soft.States.Count != expectedSoft.Length)
                throw new Exception($"soft states 应 {expectedSoft.Length} 个，实际 {soft.States.Count}");
            foreach (var (name, fps) in expectedSoft)
            {
                var st = soft.States.FirstOrDefault(s => s.Name == name) ?? throw new Exception($"soft 缺状态 {name}");
                if (Math.Abs(st.Fps - fps) > 1e-9) throw new Exception($"soft.{name}.fps 应 {fps}，实际 {st.Fps}");
                if (st.EyeLayer != null) throw new Exception($"soft.{name}.eyeLayer 应 null（眼睛画在身体里）");
            }
            if (soft.HasShadow) throw new Exception("soft 不应有影子层");

            // 3) 素材加载行为回归零差异
            var pixelAtlas = SpriteAtlas.LoadSkin(Paths.Assets, "pixel");
            if (pixelAtlas.Skin != "pixel") throw new Exception($"pixel Skin 应='pixel'，实际='{pixelAtlas.Skin}'");
            if (pixelAtlas.States.Count != 9) throw new Exception($"pixel 应有 9 状态，实际 {pixelAtlas.States.Count}");
            // 3b) v0.1.76：移除 anim.json 覆盖后，pixel 渲染图集 fps 仍逐项等于 skin.json（零行为变化）
            foreach (var (name, fps, _) in expectedPixel)
            {
                if (!pixelAtlas.States.TryGetValue(name, out var ast)) throw new Exception($"pixel 渲染图集缺状态 {name}");
                if (Math.Abs(ast.Fps - fps) > 1e-9)
                    throw new Exception($"pixel 渲染图集 {name}.fps 应 {fps}（skin.json 权威），实际 {ast.Fps}");
            }
            var softAtlas = SpriteAtlas.LoadSkin(Paths.Assets, "soft");
            if (softAtlas.Skin != "soft") throw new Exception($"soft Skin 应='soft'，实际='{softAtlas.Skin}'");
            if (!softAtlas.States.ContainsKey("work")) throw new Exception("soft 缺 work 状态");
            if (softAtlas.States["work"].EyeLayer != null) throw new Exception("soft work EyeLayer 应=null");

            // 4) 运行时 SetSkin：已注册皮肤可切换；未注册 id 回退默认 pixel
            var atlasField = typeof(PetRenderer).GetField("_atlas", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("PetRenderer._atlas 不存在");
            var rendererField = win.GetType().GetField("_renderer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            string prevSkin = win.Config.Skin ?? "pixel";
            try
            {
                win.SetSkin("soft");
                var cur = (SpriteAtlas)atlasField.GetValue(rendererField.GetValue(win))!;
                if (cur.Skin != "soft") throw new Exception($"SetSkin('soft') 后应='soft'，实际='{cur.Skin}'");
                if (win.Config.Skin != "soft") throw new Exception("SetSkin('soft') 后 config.Skin 应='soft'");

                win.SetSkin("__no_such_skin__");
                cur = (SpriteAtlas)atlasField.GetValue(rendererField.GetValue(win))!;
                if (cur.Skin != "pixel") throw new Exception($"未注册皮肤应回退 pixel，实际='{cur.Skin}'");
            }
            finally
            {
                win.SetSkin(prevSkin);
                if (win.Config.Skin != prevSkin) { win.Config.Skin = prevSkin; DesktopPet.App.Core.ConfigManager.Save(Paths.ConfigFile, win.Config); }
            }
        });

        // ---- v0.1.74 第 23b 项：损坏的 skin.json 被跳过且不抛异常 ----
        Check("v0.1.74：损坏 skin.json 被跳过（不抛异常）；无有效皮肤时回退内置默认", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "neko-skin-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(tmp, "broken"));
            File.WriteAllText(Path.Combine(tmp, "broken", "skin.json"), "{ this is not valid json ");
            try
            {
                // 只有损坏皮肤 → 扫不到有效皮肤 → 回退内置默认（不抛）
                var skins = SkinRegistry.Scan(tmp);
                if (skins.Count != 1) throw new Exception($"期望仅回退 1 个内置默认皮肤，实际 {skins.Count}");
                if (!skins[0].IsBuiltinFallback) throw new Exception("损坏清单时应回退内置默认皮肤");
                if (skins[0].Id != "pixel") throw new Exception($"兜底皮肤 id 应=pixel，实际={skins[0].Id}");

                // Resolve 对损坏/未知 id 安全回退，不抛
                if (SkinRegistry.Resolve(tmp, "broken").Id != "pixel") throw new Exception("损坏皮肤应回退 pixel");
                if (SkinRegistry.Exists(tmp, "broken")) throw new Exception("损坏的 skin.json 不应被视为已注册");

                // 用损坏皮肤 id 走真实素材根加载，也不应抛（回退 pixel 素材）
                var atlas = SpriteAtlas.LoadSkin(tmp, "broken");
                if (atlas.Skin != "pixel") throw new Exception($"损坏皮肤加载应回退 pixel，实际='{atlas.Skin}'");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        });

        // ---- v0.1.76 第 23c 项：skin.json 是 fps 唯一权威（anim.json 覆盖陷阱已移除）----
        Check("v0.1.76：skin.json 是 fps 唯一权威（只改 skin.json 的 fps 即生效；anim.json 不再覆盖）", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "neko-skin-fps-" + Guid.NewGuid().ToString("N"));
            var dir = Path.Combine(tmp, "fanmade");
            Directory.CreateDirectory(dir);
            try
            {
                // 造一个自制皮肤：idle fps=7。sheet 布局，复用 pixel 的 idle 雪碧图（128x32，4 帧）
                var sheetSrc = Path.Combine(Paths.Assets, "pixel", "neko_idle.png");
                if (!File.Exists(sheetSrc)) throw new Exception($"测试素材缺失：{sheetSrc}");
                File.Copy(sheetSrc, Path.Combine(dir, "neko_idle.png"));
                File.WriteAllText(Path.Combine(dir, "skin.json"),
                    "{ \"id\": \"fanmade\", \"layout\": \"sheet\", \"frameWidth\": 32, \"frameHeight\": 32, " +
                    "\"frames\": 4, \"hasShadow\": false, \"eyeLayers\": [], " +
                    "\"states\": [ { \"name\": \"idle\", \"fps\": 7, \"eyeLayer\": null } ] }");

                // 1) 只读 skin.json：元数据层 + 渲染图集层 fps 都应为 7
                if (Math.Abs(SkinRegistry.Resolve(tmp, "fanmade").States.Single(s => s.Name == "idle").Fps - 7) > 1e-9)
                    throw new Exception("skin.json 元数据 idle.fps 应=7");
                var atlas1 = SpriteAtlas.LoadSkin(tmp, "fanmade");
                if (atlas1.Skin != "fanmade") throw new Exception($"自制皮肤 Skin 应='fanmade'，实际='{atlas1.Skin}'");
                if (!atlas1.States.TryGetValue("idle", out var idle1)) throw new Exception("自制皮肤缺 idle 状态（雪碧图未加载？）");
                if (Math.Abs(idle1.Fps - 7) > 1e-9)
                    throw new Exception($"渲染图集 idle.fps 应=7（skin.json 权威），实际={idle1.Fps}");

                // 2) 关键：同目录再塞一个会"打架"的 anim.json（idle=4），渲染 fps 必须仍是 7（覆盖已彻底移除）
                File.WriteAllText(Path.Combine(dir, "anim.json"),
                    "{ \"fps\": { \"idle\": 4 }, \"meta\": { \"frameWidth\": 32, \"frameHeight\": 32, \"framesPerState\": 4 } }");
                var atlas2 = SpriteAtlas.LoadSkin(tmp, "fanmade");
                if (!atlas2.States.TryGetValue("idle", out var idle2)) throw new Exception("塞入 anim.json 后自制皮肤缺 idle 状态");
                if (Math.Abs(idle2.Fps - 7) > 1e-9)
                    throw new Exception($"anim.json 不应再覆盖 skin.json：期望 idle.fps=7，实际={idle2.Fps}（覆盖路径未移除？）");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        });

        // ---- v0.1.77 第 25 项：自动更新（阶段1：IsNewer + 真实 Gitee 响应解析）----
        Check("v0.1.77：自动更新（IsNewer 语义化比较 + 真实 Gitee 响应解析）", () =>
        {
            // 1) IsNewer：数字段比较 / v 前缀 / 预发布 / 非法输入（保守 false）
            if (!UpdateService.IsNewer("0.1.76", "0.1.77")) throw new Exception("0.1.76 < 0.1.77 应 true");
            if (UpdateService.IsNewer("0.1.76", "0.1.76")) throw new Exception("相同版本应 false");
            if (UpdateService.IsNewer("0.1.77", "0.1.76")) throw new Exception("更旧版本应 false");
            if (!UpdateService.IsNewer("v0.1.76", "v0.1.77")) throw new Exception("带 v 前缀应能比较");
            if (!UpdateService.IsNewer("1.2", "1.2.1")) throw new Exception("缺位应补 0（1.2 < 1.2.1）");
            if (UpdateService.IsNewer("0.1.77", "0.1.77-beta")) throw new Exception("预发布应比正式版旧");
            if (!UpdateService.IsNewer("0.1.77-beta", "0.1.77")) throw new Exception("正式版应比预发布新");
            if (UpdateService.IsNewer("0.1.77-beta", "0.1.77-beta")) throw new Exception("同号同预发布应 false");
            if (UpdateService.IsNewer("abc", "1.0.0")) throw new Exception("非法 current 应 false（保守）");
            if (UpdateService.IsNewer("1.0.0", "not-a-version")) throw new Exception("非法 latest 应 false（保守）");
            if (UpdateService.IsNewer("", "1.0.0")) throw new Exception("空 current 应 false");

            // 2) CurrentVersion：形如 x.y.z
            var cv = UpdateService.CurrentVersion;
            if (string.IsNullOrWhiteSpace(cv) || cv.Split('.').Length != 3)
                throw new Exception($"CurrentVersion 应形如 x.y.z，实际='{cv}'");

            // 3) 解析「真实 Gitee 响应」（实抓自 mindspore/mindspore 的 releases/latest；body 截断便于阅读）
            const string giteeJson = """
            {
              "id": 569215,
              "tag_name": "v2.7.2",
              "target_commitish": "970b12dc1a89d57d703e7a1bae412212aad95887",
              "prerelease": false,
              "name": "MindSpore 2.7.2 Release Notes",
              "body": "## MindSpore 2.7.2 Release Notes\r\n\r\n### Major Features and Improvements\r\n\r\n#### Ascend\r\n\r\n- [BETA] The GroupedMatMul + R",
              "created_at": "2026-01-16T15:50:42+08:00",
              "assets": [
                { "browser_download_url": "https://gitee.com/mindspore/mindspore/archive/refs/tags/v2.7.2.zip", "name": "v2.7.2.zip" },
                { "browser_download_url": "https://gitee.com/mindspore/mindspore/archive/refs/tags/v2.7.2.tar.gz", "name": "v2.7.2.tar.gz" }
              ]
            }
            """;
            var rel = UpdateService.ParseLatest(giteeJson) ?? throw new Exception("真实 Gitee 响应应能解析");
            if (rel.TagName != "v2.7.2") throw new Exception($"tag_name 应 v2.7.2，实际 {rel.TagName}");
            if (rel.Version != "2.7.2") throw new Exception($"Version 应 2.7.2（去 v 前缀），实际 {rel.Version}");
            if (string.IsNullOrEmpty(rel.Notes)) throw new Exception("body 应非空");
            if (rel.DownloadUrl != "https://gitee.com/mindspore/mindspore/archive/refs/tags/v2.7.2.zip")
                throw new Exception($"assets[0] 下载地址解析错误：{rel.DownloadUrl}");
            // Gitee 响应无 html_url → 应回退到按仓库坐标拼的发布页地址
            if (rel.ReleaseUrl != "https://gitee.com/gddgssdfg/neko/releases/tag/v2.7.2")
                throw new Exception($"无 html_url 时应拼发布页地址，实际 {rel.ReleaseUrl}");

            // 4) 显式 html_url 应被采用
            var rel2 = UpdateService.ParseLatest("{\"tag_name\":\"v9.9.9\",\"html_url\":\"https://example.com/r\",\"body\":\"x\"}");
            if (rel2 is null || rel2.ReleaseUrl != "https://example.com/r") throw new Exception("显式 html_url 应被采用");

            // 5) body 截断到 ~2000 字
            var bigBody = new string('A', 2500);
            var rel3 = UpdateService.ParseLatest($"{{\"tag_name\":\"v1.0.0\",\"body\":\"{bigBody}\"}}");
            if (rel3 is null || rel3.Notes.Length > 2001) throw new Exception($"body 应截断到 ~2000，实际 {rel3?.Notes.Length}");

            // 6) 缺 tag_name / 非法 JSON / 空 / 非对象 → null
            if (UpdateService.ParseLatest("{}") is not null) throw new Exception("缺 tag_name 应 null");
            if (UpdateService.ParseLatest("not json") is not null) throw new Exception("非法 JSON 应 null");
            if (UpdateService.ParseLatest("") is not null) throw new Exception("空串应 null");
            if (UpdateService.ParseLatest("[1,2,3]") is not null) throw new Exception("非对象应 null");
        });

        // ---- v0.1.77 第 25b 项：UpdateService 网络失败静默返回 null（绝不抛）----
        Check("v0.1.77：UpdateService 网络失败静默返回 null（不抛）", () =>
        {
            // .invalid 顶级域保证 DNS 解析失败 → HttpClient 抛 → 应被吞掉返回 null（铁律：没网不能崩）
            var r = UpdateService.FetchAsync("http://neko-update-probe.invalid/latest", default).GetAwaiter().GetResult();
            if (r is not null) throw new Exception("网络失败应返回 null");
        });

        // ---- v0.1.50 第 24 项：主题/语言字段 + SettingsWindow 主题色按 Theme 实例化（深/浅两套）----
        Check("v0.1.50：Config Theme/Language 字段 + SettingsWindow 主题色按主题实例化", () =>
        {
            // 1) Config 字段存在
            if (typeof(AppConfig).GetProperty("Theme") is null) throw new Exception("Config 缺 Theme 字段");
            if (typeof(AppConfig).GetProperty("Language") is null) throw new Exception("Config 缺 Language 字段");

            // 2) SettingsWindow 主题颜色字段已是实例（去掉 static，否则深浅切换不生效）
            var swType = typeof(SettingsWindow);
            var bgRootField = swType.GetField("BgRoot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("SettingsWindow.BgRoot 不是实例字段（可能还是 static）");

            // 3) 构造 SettingsWindow 验证主题色按 _config.Theme 选深/浅（深色默认 #1E26...，浅色 #F5F7...）
            string prevTheme = win.Config.Theme;
            try
            {
                win.Config.Theme = "dark";
                var swDark = new SettingsWindow(win);
                var bgDark = (Color)bgRootField.GetValue(swDark)!;
                if (bgDark.R != 0x1E || bgDark.G != 0x26 || bgDark.B != 0x2F)
                    throw new Exception($"深色主题 BgRoot 应=#1E262F 实际=#{bgDark.R:X2}{bgDark.G:X2}{bgDark.B:X2}");
                swDark.Close();

                win.Config.Theme = "light";
                var swLight = new SettingsWindow(win);
                var bgLight = (Color)bgRootField.GetValue(swLight)!;
                if (bgLight.R != 0xF5 || bgLight.G != 0xF7 || bgLight.B != 0xFA)
                    throw new Exception($"浅色主题 BgRoot 应=#F5F7FA 实际=#{bgLight.R:X2}{bgLight.G:X2}{bgLight.B:X2}");
                swLight.Close();
            }
            finally
            {
                win.Config.Theme = prevTheme;
            }
        });

        // ---- v0.1.57 第 25 项：设置页 ChoiceCard 点击后选中态实时高亮（存量 bug：onChange 后从不刷新按钮视觉）----
        Check("设置页 ChoiceCard：点击后选中项实时高亮（150ms ColorAnimation）", () =>
        {
            var settings = new SettingsWindow(win);
            settings.Show();
            settings.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var showSection = typeof(SettingsWindow).GetMethod("ShowSection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("SettingsWindow.ShowSection 不存在");
            showSection.Invoke(settings, new object[] { "general" });
            settings.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            static IEnumerable<DependencyObject> Descendants(DependencyObject root)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var c = VisualTreeHelper.GetChild(root, i);
                    yield return c;
                    foreach (var d in Descendants(c)) yield return d;
                }
            }

            // 定位游戏模式卡（3 个选项，中文文案不受语言切换影响）
            Border? target = null;
            foreach (var o in Descendants(settings))
                if (o is Border b && b.Child is TextBlock tb && tb.Text == "全屏时藏猫")
                { target = b; break; }
            if (target is null) throw new Exception("找不到游戏模式 ChoiceCard 的『全屏时藏猫』按钮");
            var panel = (StackPanel)target.Parent!;
            var buttons = new List<Border>();
            for (int i = 0; i < panel.Children.Count; i++)
                if (panel.Children[i] is Border b) buttons.Add(b);
            if (buttons.Count != 3) throw new Exception($"游戏模式按钮数应=3 实际={buttons.Count}");

            var accent = (Color)typeof(SettingsWindow).GetField("Accent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings)!;
            var grey = Color.FromRgb(0x3A, 0x44, 0x57);
            static int CD(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

            // 模拟点击第 2 项（索引 1 = 全屏时藏猫）：onChange + RefreshSelection
            buttons[1].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = buttons[1],
            });

            // 等 150ms ColorAnimation 完成（DispatcherFrame 驱动消息循环，动画时钟才会走）
            var frame = new DispatcherFrame();
            var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(240) };
            t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
            settings.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var sel = ((SolidColorBrush)buttons[1].Background).Color;
            var un0 = ((SolidColorBrush)buttons[0].Background).Color;
            var un2 = ((SolidColorBrush)buttons[2].Background).Color;
            if (CD(sel, accent) > 24)
                throw new Exception($"点击第2项后应高亮 Accent=#{accent.R:X2}{accent.G:X2}{accent.B:X2}，实际=#{sel.R:X2}{sel.G:X2}{sel.B:X2}");
            if (CD(un0, grey) > 24)
                throw new Exception($"第1项应回灰底 #3A4457，实际=#{un0.R:X2}{un0.G:X2}{un0.B:X2}");
            if (CD(un2, grey) > 24)
                throw new Exception($"第3项应保持灰底 #3A4457，实际=#{un2.R:X2}{un2.G:X2}{un2.B:X2}");

            // 还原：点回第 1 项（关闭），避免污染游戏模式状态
            buttons[0].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = buttons[0],
            });
            settings.Close();
        });

        // ---- v0.1.59 第 26 项：P4 渲染时钟统一 + ⑩鼠标张望 + ⑨心情驱动（行为等价内部重构 + 新交互）----
        Check("v0.1.59(P4/⑩/⑨)：渲染时钟统一 _clockMs + 鼠标张望增益 + 好感度驱动心情", () =>
        {
            // --- P4：闪眼/打字截止时间改为 _clockMs 基准（字段类型应是 double，非 DateTime）---
            var flashF = typeof(PetRenderer).GetField("_flashEyesUntilMs", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("PetRenderer._flashEyesUntilMs 不存在（P4 未改）");
            if (flashF.FieldType != typeof(double))
                throw new Exception($"P4：_flashEyesUntilMs 应为 double（_clockMs 基准），实际 {flashF.FieldType}");
            var typingF = typeof(PetRenderer).GetField("_typingUntilMs", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("PetRenderer._typingUntilMs 不存在（P4 未改）");
            if (typingF.FieldType != typeof(double))
                throw new Exception($"P4：_typingUntilMs 应为 double（_clockMs 基准），实际 {typingF.FieldType}");

            // FlashEyes 后 HasTransientVisual 应立即为真（闪眼生效）
            var renderer = (PetRenderer)typeof(PetWindow).GetField("_renderer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(win)!;
            renderer.FlashEyes(500);
            if (!renderer.HasTransientVisual)
                throw new Exception("P4：FlashEyes(500) 后 HasTransientVisual 应为 true（闪眼未生效）");

            // --- ⑩：SetEyeOffset 增益参数——张望时 |eyeOffset| 应大于基础值 ---
            var eyeF = typeof(PetRenderer).GetField("_eyeOffset", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var dir = new Vector(1, 0);   // 归一化方向
            renderer.SetEyeOffset(dir, 1.0);
            var baseOffset = ((Vector)eyeF.GetValue(renderer)!).Length;
            renderer.SetEyeOffset(dir, 1.6);
            var peekOffset = ((Vector)eyeF.GetValue(renderer)!).Length;
            if (Math.Abs(baseOffset - 2.0) > 0.01)
                throw new Exception($"⑩：基础眼球偏移应=2px，实际 {baseOffset:F2}");
            if (Math.Abs(peekOffset - 3.2) > 0.01)
                throw new Exception($"⑩：张望增益 1.6× 应=3.2px，实际 {peekOffset:F2}");

            // 边界响应：_peekActive 字段存在，进入半径内应为 true
            var peekF = typeof(PetWindow).GetField("_peekActive", BindingFlags.NonPublic | BindingFlags.Instance);
            if (peekF is null) throw new MissingFieldException("PetWindow._peekActive 不存在（⑩ 未实现）");

            // --- ⑨：AffectionReader 等级复用 + 心情映射（L3 挚友 → Happy 帧率 1.3×）---
            var affDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neko-v159-aff");
            System.IO.Directory.CreateDirectory(affDir);
            var affFile = System.IO.Path.Combine(affDir, "score.json");
            System.IO.File.WriteAllText(affFile, "5");
            if (AffectionReader.ReadLevel(affFile) != 0) throw new Exception("⑨：好感度 5 应=0 陌生");
            System.IO.File.WriteAllText(affFile, "999");
            if (AffectionReader.ReadLevel(affFile) != 3) throw new Exception("⑨：好感度 999 应=3 挚友");
            System.IO.Directory.Delete(affDir, true);

            // SetMood(Happy) → _moodIntensity 应=1.3
            var moodF = typeof(PetRenderer).GetField("_moodIntensity", BindingFlags.NonPublic | BindingFlags.Instance)!;
            renderer.SetMood(DesktopPet.Core.PetMood.Happy);
            if (Math.Abs((double)moodF.GetValue(renderer)! - 1.3) > 0.001)
                throw new Exception($"⑨：SetMood(Happy) 帧率倍率应=1.3，实际 {moodF.GetValue(renderer)}");
            renderer.SetMood(DesktopPet.Core.PetMood.Idle);   // 还原
        });

        // ---- v0.1.59 第 27 项：⑧ 抚摸爱心粒子浮层（独立窗口 + 粒子生成/飘散/自动隐藏）----
        Check("v0.1.59(⑧)：爱心粒子浮层——Burst 生成 3~5 颗 + 淡出后自动隐藏 + 背景穿透", () =>
        {
            var hearts = new HeartParticleWindow();
            hearts.Show();
            hearts.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            if (hearts.Background is not null)
                throw new Exception("⑧：爱心浮层 Background 应为 null（真穿透）");
            if (hearts.IsHitTestVisible)
                throw new Exception("⑧：爱心浮层不应拦鼠标（IsHitTestVisible 应=false）");

            // Burst 后匿名粒子入 _particles（反射取私有 List）
            hearts.Burst(new Point(300, 300));
            var partF = typeof(HeartParticleWindow).GetField("_particles", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("HeartParticleWindow._particles 不存在");
            var particles = (System.Collections.ICollection)partF.GetValue(hearts)!;
            if (particles.Count < 3 || particles.Count > 5)
                throw new Exception($"⑧：Burst 应生成 3~5 颗爱心，实际 {particles.Count}");

            // 等待 >800ms 寿命 → 粒子全部消散 + 窗口自动隐藏（不常驻）
            var tickF = typeof(HeartParticleWindow).GetField("_tickCount", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("HeartParticleWindow._tickCount 不存在");
            int ticksBefore = (int)tickF.GetValue(hearts)!;

            // 等待 >800ms 寿命 → 粒子全部消散 + 窗口自动隐藏（不常驻）。
            // 爱心仿真已用真实 elapsed（OnTick 内 TickCount64 差），tick 疏密不影响寿命；
            // 这里再用「反复 PushFrame + 短等」确定性驱动，双重稳妥（单 timer 等待曾在嵌套泵下提前退出）。
            var deadline = DateTime.Now.AddMilliseconds(1300);
            while (DateTime.Now < deadline)
            {
                var step = new DispatcherFrame();
                var stepTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(20),
                };
                stepTimer.Tick += (_, _) => { stepTimer.Stop(); step.Continue = false; };
                stepTimer.Start();
                Dispatcher.PushFrame(step);
            }
            hearts.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            // 决定性诊断：OnTick 必须真的被调度过（否则说明 timer 没跑，而非消散逻辑错）
            int ticksAfter = (int)tickF.GetValue(hearts)!;
            if (ticksAfter - ticksBefore <= 0)
                throw new Exception($"⑧：爱心 timer 未推进（tick {ticksBefore}→{ticksAfter}）——OnTick 从未被调度");
            if (particles.Count != 0)
                throw new Exception($"⑧：爱心寿命 800ms 后应全部消散，实际仍剩 {particles.Count} 颗（tick {ticksBefore}→{ticksAfter}）");
            if (hearts.IsVisible)
                throw new Exception("⑧：爱心播完应自动隐藏（不常驻）");
            hearts.Close();
        });

        // ---- v0.1.59 第 28 项：⑦ 扔猫抛物线物理（真实时序：水平飞行 + 越界防护）----
        Check("v0.1.59(⑦)：抛物线物理——真实时序水平飞行 + 越界防护 + 触地摩擦常量", () =>
        {
            // 常量存在
            var gf = typeof(PetWindow).GetField("GroundFriction", BindingFlags.NonPublic | BindingFlags.Static);
            if (gf is null) throw new MissingFieldException("PetWindow.GroundFriction 不存在（⑦ 未实现）");
            var work = (DesktopPet.Core.Rect2)typeof(PetWindow)
                .GetMethod("GetWorkArea", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(win, null)!;

            // 记录初始位置，向右上方甩出（vx>0, vy<0）→ 观察是否水平位移过
            double startLeft = win.Left;
            double startTop = win.Top;
            var startFall = typeof(PetWindow).GetMethod("StartFall", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("PetWindow.StartFall 不存在");
            win.Left = work.X + work.Width * 0.5;
            win.Top = work.Y + work.Height * 0.3;   // 抬高，保证有下落空间
            startFall.Invoke(win, new object[] { new Vector(8.0, -4.0) });   // ×60 → vx=480, vy=-240 px/s

            // 真实时序驱动 400ms（反复 PushFrame + 短等，确定性推进 dispatcher timer）
            var deadline = DateTime.Now.AddMilliseconds(400);
            while (DateTime.Now < deadline)
            {
                var step = new DispatcherFrame();
                var stepTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(20),
                };
                stepTimer.Tick += (_, _) => { stepTimer.Stop(); step.Continue = false; };
                stepTimer.Start();
                Dispatcher.PushFrame(step);
            }

            if (Math.Abs(win.Left - (work.X + work.Width * 0.5)) < 1.0)
                throw new Exception($"⑦：向右甩出后水平位置未变（Left={win.Left:F0}），抛物线未生效（落体 timer 未跑？）");
            if (Math.Abs(win.Top - (work.Y + work.Height * 0.3)) < 1.0)
                throw new Exception($"⑦：抛出后竖直位置未变（Top={win.Top:F0}），落体物理未生效");

            // 水平越界防护：飞过的 Left 必须始终落在工作区内（含左右边界）
            if (win.Left < work.X - 1 || win.Left + win.Width > work.X + work.Width + 1)
                throw new Exception($"⑦：飞行中 Left={win.Left:F0} 越出工作区（应被反射边界拦住）");

            // 收尾：停落体、复位
            var fallF = typeof(PetWindow).GetField("_fallTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            if (fallF.GetValue(win) is DispatcherTimer ft) ft.Stop();
            fallF.SetValue(win, null);
            win.Left = startLeft;
            win.Top = startTop;
        });

        win.Dispose();
        win.Close();

        // ---- v0.1.61：日志系统（任务1）----
        var logDir = Path.Combine(Path.GetTempPath(), "neko-logtest-" + Guid.NewGuid().ToString("N"));

        Check("v0.1.61(日志)：Log.Error 写文件且内容含消息", () =>
        {
            Log.Initialize("debug", logDir);
            Log.Error("日志探针测试消息-ABC123", new InvalidOperationException("探针内部异常-XYZ"));
            var files = Directory.GetFiles(logDir, "neko-*.log");
            if (files.Length == 0) throw new Exception("未生成日志文件");
            var text = File.ReadAllText(files[0]);
            if (!text.Contains("日志探针测试消息-ABC123")) throw new Exception("日志文件不含消息文本");
            if (!text.Contains("[ERROR]")) throw new Exception("日志缺少级别标记");
            if (!text.Contains("探针内部异常-XYZ")) throw new Exception("Log.Error 未记录异常内容");
        });

        Check("v0.1.61(日志)：多线程并发调用不抛异常", () =>
        {
            Log.Initialize("debug", logDir);
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            Parallel.For(0, 200, i =>
            {
                try { Log.Warn($"并发日志 {i}"); }
                catch (Exception ex) { errors.Add(ex); }
            });
            if (!errors.IsEmpty) throw new Exception($"并发写入抛异常 {errors.Count} 次");
            // 至少应写入若干行
            var total = Directory.GetFiles(logDir, "neko-*.log").Sum(f => File.ReadAllText(f).Length);
            if (total == 0) throw new Exception("并发写入后日志为空");
        });

        Check("v0.1.61(日志)：目录不存在时自动创建", () =>
        {
            var freshDir = Path.Combine(Path.GetTempPath(), "neko-logtest-fresh-" + Guid.NewGuid().ToString("N"), "nested");
            if (Directory.Exists(freshDir)) throw new Exception("前提错误：目录不应存在");
            Log.Initialize("info", freshDir);
            Log.Info("自动创建目录探针");
            if (!Directory.Exists(freshDir)) throw new Exception("日志目录未被自动创建");
            if (Directory.GetFiles(freshDir, "*.log").Length == 0) throw new Exception("未写入日志文件");
        });

        Check("v0.1.61(日志)：级别过滤（off 不写 / warn 默认）", () =>
        {
            var offDir = Path.Combine(Path.GetTempPath(), "neko-logtest-off-" + Guid.NewGuid().ToString("N"));
            Log.Initialize("off", offDir);
            Log.Error("off 级别不应落盘");
            if (Directory.Exists(offDir) && Directory.GetFiles(offDir, "*.log").Length > 0)
                throw new Exception("off 级别仍写入了日志");
            Console.WriteLine("  off 级别：未写盘 OK");
        });

        // ---- v0.1.61：调度器异常隔离（任务2 — 宿主不被拖垮）----
        Check("v0.1.61(调度器)：回调抛异常不拖垮宿主 + 记日志 + 熔断", () =>
        {
            var scDir = Path.Combine(Path.GetTempPath(), "neko-logtest-sched-" + Guid.NewGuid().ToString("N"));
            Log.Initialize("error", scDir);

            var sched = new SchedulerImpl();
            int calls = 0;
            var handle = sched.Every(20, () =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("插件定时任务故意抛异常");
            });

            // 等待足够多次 tick 以触发熔断（阈值 5 次）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (handle.IsActive && sw.ElapsedMilliseconds < 3000)
                Thread.Sleep(20);

            if (handle.IsActive) throw new Exception("连续失败后定时器未熔断停用（宿主会一直刷异常）");
            if (Volatile.Read(ref calls) < 5) throw new Exception($"实际仅触发 {calls} 次，未达熔断阈值");

            // 异常必须被记录（证明走的是 Log 而非裸崩）
            var logText = string.Join("\n", Directory.GetFiles(scDir, "*.log").Select(File.ReadAllText));
            if (!logText.Contains("插件定时任务故意抛异常"))
                throw new Exception("调度器异常未被记录到日志");
            Console.WriteLine($"  熔断生效：失败 {calls} 次后定时器停用，异常已落盘");

            handle.Dispose();
            Console.WriteLine($"  宿主进程存活（本探针继续运行即证明）");
        });

        Check("v0.1.61(调度器)：正常回调不被熔断 + 连续失败后恢复归零", () =>
        {
            var sched = new SchedulerImpl();
            int ok = 0;
            var h = sched.Every(10, () => Interlocked.Increment(ref ok));
            Thread.Sleep(200);
            if (ok < 3) throw new Exception($"正常定时器仅触发 {ok} 次");
            if (!h.IsActive) throw new Exception("正常定时器被误熔断");
            h.Dispose();
            if (h.IsActive) throw new Exception("Dispose 后 IsActive 应为 false");
        });

        // ---- v0.1.77：调度器「首帧回调竞态杀宿主」高频并发回归（P0-5）----
        // 复现原 bug 的触发条件：At(DateTime.Now) → delay≈0 → System.Threading.Timer 立即在线程池回调；
        // 旧实现把 timer 直接用 dueMs=0 启动后才赋值 handle → 回调闭包捕获的 handle 此刻仍是 null →
        // 首次 SafeInvoke(handle!) 内 NRE，catch 里再访问同一个 null handle 二次 NRE → 逃出回调 →
        // 线程池未处理异常 → CLR 直接终止进程（App.xaml.cs 三层兜底只覆盖 UI 线程，拦不住）。
        // 修复后 handle 先以 Timeout.Infinite 装配完成，再 Change(dueMs, periodMs) 启动，
        // 回调无论多早触发都拿得到非 null 的完整 handle。本探针 4 线程 × 125 并发打满这条路径。
        Check("v0.1.77(调度器)：4 线程 × 125 并发 At(DateTime.Now) 不杀宿主（P0-5 竞态回归）", () =>
        {
            // 记录任何逃逸到 AppDomain / TaskScheduler 的未处理异常（进程若存活，这必须是 0）
            int unhandled = 0;
            UnhandledExceptionEventHandler onUnhandled =
                (_, _) => Interlocked.Increment(ref unhandled);
            EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved =
                (_, e) => { Interlocked.Increment(ref unhandled); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += onUnhandled;
            TaskScheduler.UnobservedTaskException += onUnobserved;
            try
            {
                var sched = new SchedulerImpl();
                int fired = 0;
                const int threads = 4;
                const int perThread = 125;   // 4 × 125 = 500 个 due≈0 定时器
                var handles = new List<DesktopPet.Core.IScheduleHandle>(threads * perThread);
                var handlesLock = new object();

                var workers = new Thread[threads];
                for (int t = 0; t < threads; t++)
                {
                    workers[t] = new Thread(() =>
                    {
                        var local = new List<DesktopPet.Core.IScheduleHandle>(perThread);
                        for (int i = 0; i < perThread; i++)
                            // delay≈0：这是原竞态最凶的触发路径（回调抢在 handle 赋值前触发）
                            local.Add(sched.At(DateTime.Now, () => Interlocked.Increment(ref fired)));
                        lock (handlesLock) handles.AddRange(local);
                    })
                    { IsBackground = true };
                    workers[t].Start();
                }
                foreach (var w in workers) w.Join();

                // 等待全部 due=0 定时器在线程池上跑完（旧实现在此之前就已崩进程退出）
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref fired) < threads * perThread && sw.ElapsedMilliseconds < 5000)
                    Thread.Sleep(10);

                if (Volatile.Read(ref fired) < threads * perThread)
                    throw new Exception($"仅 {fired}/{threads * perThread} 个 At(DateTime.Now) 回调触发（回调被静默吞掉）");

                // 每个句柄都必须是有效对象（非 null、未被误熔断/误释放）
                foreach (var h in handles)
                {
                    if (h is null) throw new Exception("存在 null 句柄（handle 装配竞态未消除）");
                    if (!h.IsActive) throw new Exception("存在已失效句柄（被误熔断/误释放）");
                }
                foreach (var h in handles) h.Dispose();

                if (Volatile.Read(ref unhandled) != 0)
                    throw new Exception($"捕获到 {unhandled} 次未处理异常（应被 SafeInvoke 全部隔离）");

                Console.WriteLine($"  {threads * perThread} 个 At(DateTime.Now)(4 线程并发) 回调全部触发；" +
                                  "句柄均有效；进程存活；0 未处理异常");
            }
            finally
            {
                AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
                TaskScheduler.UnobservedTaskException -= onUnobserved;
            }
        });

        // ---- v0.1.71：径向菜单顺序/显隐/数量上限（RadialMenuResolver 纯函数单测）----
        Check("v0.1.71(径向菜单)：RadialMenuResolver 隐藏过滤 + Order 覆盖 + 数量上限截断 + 缺省回退", () =>
        {
            static DesktopPet.Core.RadialSlot S(string id, int order) => new() { Id = id, Label = id, Order = order, OnInvoke = () => { } };

            var slots = new[] { S("a", 1), S("b", 2), S("c", 3), S("d", 4) };

            // 1) 缺省：全显示，按插件 Order 升序
            var cfg0 = new RadialMenuConfig();
            var r0 = RadialMenuResolver.Resolve(slots, cfg0);
            if (r0.Select(s => s.Id).SequenceEqual(new[] { "a", "b", "c", "d" }) == false)
                throw new Exception($"缺省顺序错误: {string.Join(",", r0.Select(s => s.Id))}");

            // 2) 显隐：隐藏 b
            var cfg1 = new RadialMenuConfig { Visible = { ["b"] = false } };
            var r1 = RadialMenuResolver.Resolve(slots, cfg1);
            if (r1.Select(s => s.Id).SequenceEqual(new[] { "a", "c", "d" }) == false)
                throw new Exception($"隐藏过滤错误: {string.Join(",", r1.Select(s => s.Id))}");

            // 3) Order 覆盖：把 c 提到最前（覆盖值 0 < 插件 Order 3）
            var cfg2 = new RadialMenuConfig { Order = { ["c"] = 0 } };
            var r2 = RadialMenuResolver.Resolve(slots, cfg2);
            if (r2[0].Id != "c") throw new Exception($"Order 覆盖未生效，首项={r2[0].Id}");

            // 4) 数量上限：MaxVisible=2 只保留前 2 个
            var cfg3 = new RadialMenuConfig { MaxVisible = 2 };
            var r3 = RadialMenuResolver.Resolve(slots, cfg3);
            if (r3.Count != 2 || r3[0].Id != "a" || r3[1].Id != "b")
                throw new Exception($"上限截断错误: {string.Join(",", r3.Select(s => s.Id))}");

            // 5) FreezeOrder：固化顺序为 index
            var cfg4 = new RadialMenuConfig();
            RadialMenuResolver.FreezeOrder(new[] { slots[2], slots[0], slots[1], slots[3] }, cfg4);
            if (cfg4.Order["c"] != 0 || cfg4.Order["a"] != 1 || cfg4.Order["b"] != 2 || cfg4.Order["d"] != 3)
                throw new Exception("FreezeOrder 固化顺序错误");

            // 6) Prune：清掉不存在的 slotId 残留
            var cfg5 = new RadialMenuConfig { Order = { ["ghost"] = 0, ["a"] = 1 }, Visible = { ["ghost"] = false, ["b"] = false } };
            RadialMenuResolver.Prune(slots, cfg5);
            if (cfg5.Order.ContainsKey("ghost") || cfg5.Visible.ContainsKey("ghost"))
                throw new Exception("Prune 未清理残留 slotId");
            if (!cfg5.Order.ContainsKey("a") || !cfg5.Visible.ContainsKey("b"))
                throw new Exception("Prune 误删了合法 key");

            Console.WriteLine("  隐藏/覆盖/截断/缺省/冻结/清理 六项全通过");
        });

        // ---- v0.1.74：配置健壮性（原子写 + 解析失败隔离 + 未知字段透传，借鉴 BongoCat）----
        Check("v0.1.74(配置)：损坏文件隔离保留 + 原子写无 tmp 残留 + 未知字段透传", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "neko-cfgtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var cfgPath = Path.Combine(dir, "config.json");
            try
            {
                // 1) 损坏文件 → Load 返回默认 + 原文件被改名保留（绝不静默覆盖用户数据）
                File.WriteAllText(cfgPath, "{ \"Scale\": 3.2, \"Broken\": , }");   // 语法错误
                var loaded = ConfigManager.Load(cfgPath);
                if (loaded is null) throw new Exception("Load 应返回默认配置而非 null");
                if (Directory.GetFiles(dir, "config.json.rejected-*").Length == 0)
                    throw new Exception("解析失败时原文件应改名保留（.rejected-*），实际未找到");

                // 2) 原子写：Save 后文件存在且无 .tmp 残留
                foreach (var f in Directory.GetFiles(dir, "config.json.rejected-*")) File.Delete(f);
                ConfigManager.Save(cfgPath, new AppConfig { Scale = 2.5 });
                if (!File.Exists(cfgPath)) throw new Exception("Save 后 config.json 应存在");
                if (Directory.GetFiles(dir, "*.tmp").Length > 0) throw new Exception("Save 后不应残留 .tmp 文件");
                if (Math.Abs(ConfigManager.Load(cfgPath).Scale - 2.5) > 0.001)
                    throw new Exception("Save→Load 往返应保持 Scale=2.5");

                // 3) 未知字段透传：Load→Save 往返后未知字段仍在（保护「Core API 只增不改」）
                File.WriteAllText(cfgPath, "{ \"Scale\": 1.8, \"FutureField\": { \"x\": 42 }, \"PluginNs\": \"hello\" }");
                ConfigManager.Save(cfgPath, ConfigManager.Load(cfgPath));
                var text = File.ReadAllText(cfgPath);
                if (!text.Contains("FutureField") || !text.Contains("42") || !text.Contains("PluginNs"))
                    throw new Exception("未知字段应在 Load→Save 往返后保留（JsonExtensionData 未生效）");

                Console.WriteLine("  隔离/原子写/透传 三项全通过");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });

        // ---- v0.1.81 第 29 项：hd 皮肤尺寸自适应（recommendedScale 声明 + 切换自动套用）----
        Check("v0.1.81：hd 皮肤 recommendedScale 声明 + 切换自动套用（pixel/soft 未声明不改用户缩放）", () =>
        {
            // 0) hd 素材必须已部署（未部署 → throw，绝不静默 pass）——本断言的意义就是防「素材没进输出目录」
            var hdDir = Path.Combine(Paths.Assets, "hd");
            if (!Directory.Exists(hdDir))
                throw new Exception($"hd 皮肤目录不存在（素材未进输出目录？）：{hdDir}");

            // 1) 元数据层：Scan 扫到 hd，其 RecommendedScale=4；pixel/soft 未声明=0
            var skins = SkinRegistry.Scan(Paths.Assets);
            var hd = skins.FirstOrDefault(s => s.Id == "hd") ?? throw new Exception("Scan 未扫到 hd 皮肤");
            if (Math.Abs(hd.RecommendedScale - 4) > 1e-9)
                throw new Exception($"hd.RecommendedScale 应=4，实际 {hd.RecommendedScale}");
            var pixelInfo = skins.FirstOrDefault(s => s.Id == "pixel") ?? throw new Exception("未扫到 pixel 皮肤");
            var softInfo = skins.FirstOrDefault(s => s.Id == "soft") ?? throw new Exception("未扫到 soft 皮肤");
            if (pixelInfo.RecommendedScale != 0)
                throw new Exception($"pixel 未声明 recommendedScale 应=0，实际 {pixelInfo.RecommendedScale}");
            if (softInfo.RecommendedScale != 0)
                throw new Exception($"soft 未声明 recommendedScale 应=0，实际 {softInfo.RecommendedScale}");

            // 2) 渲染层：LoadSkin(hd) → 9 状态 × 4 帧，每帧位图宽=256（下采样后的单帧尺寸）
            var atlas = SpriteAtlas.LoadSkin(Paths.Assets, "hd");
            if (atlas.Skin != "hd") throw new Exception($"hd Skin 应='hd'，实际='{atlas.Skin}'");
            if (atlas.States.Count != 9) throw new Exception($"hd 应有 9 状态，实际 {atlas.States.Count}");
            if (Math.Abs(atlas.RecommendedScale - 4) > 1e-9)
                throw new Exception($"atlas.RecommendedScale 应=4（skin.json 透传失败？），实际 {atlas.RecommendedScale}");
            foreach (var st in atlas.States.Values)
            {
                if (st.Frames.Length != 4) throw new Exception($"hd.{st.Name} 应 4 帧，实际 {st.Frames.Length}");
                foreach (var fr in st.Frames)
                    if (fr.Bitmap.PixelWidth != 256)
                        throw new Exception($"hd.{st.Name} 帧位图宽应=256（下采样后），实际 {fr.Bitmap.PixelWidth}");
            }

            // 3) 端到端：真实 PetWindow 切换皮肤 → 自动套用推荐缩放 / 未声明则不改
            var probeCfg = new AppConfig { Skin = "pixel", Scale = 1.0 };
            var probeWin = new PetWindow(probeCfg);
            probeWin.Show();
            probeWin.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            string enterSkin = probeWin.Config.Skin;
            double enterScale = probeWin.CurrentScale;
            try
            {
                // 切 hd（声明 recommendedScale=4）→ 自动套用：Scale=4，窗口宽 = 32 × 4
                probeWin.SetSkin("hd");
                if (probeWin.Config.Skin != "hd")
                    throw new Exception($"SetSkin('hd') 后 Config.Skin 应='hd'，实际='{probeWin.Config.Skin}'");
                if (Math.Abs(probeWin.CurrentScale - 4) > 1e-9)
                    throw new Exception($"SetSkin('hd') 应自动套用推荐缩放 4，实际 CurrentScale={probeWin.CurrentScale}");
                if (Math.Abs(probeWin.Width - 32 * 4) > 0.5)
                    throw new Exception($"SetSkin('hd') 后窗口宽应=32×4=128，实际 {probeWin.Width}");

                // 再切 pixel（未声明推荐缩放）→ Scale 必须保持上一步的 4 不变
                probeWin.SetSkin("pixel");
                if (probeWin.Config.Skin != "pixel")
                    throw new Exception($"SetSkin('pixel') 后 Config.Skin 应='pixel'，实际='{probeWin.Config.Skin}'");
                if (Math.Abs(probeWin.CurrentScale - 4) > 1e-9)
                    throw new Exception($"pixel 未声明推荐缩放，Scale 应保持 4 不变，实际 {probeWin.CurrentScale}");
            }
            finally
            {
                // 还原进入时的皮肤 / 缩放
                probeWin.SetSkin(enterSkin);
                probeWin.SetScale(enterScale);
                probeWin.Dispose();
                probeWin.Close();
            }
        });

        // ---- v0.1.82 第 30 项：重复选皮肤重套推荐缩放 + 步进器 ± 按「活的当前值」起算 ----
        Check("v0.1.82：重复选皮肤重套推荐缩放 + 未声明皮肤零副作用 + 步进器 ± 从活值起算", () =>
        {
            // ---------- A. 重复 SetSkin("hd") 必须重新套用推荐缩放 ----------
            var p30Cfg = new AppConfig { Skin = "pixel", Scale = 1.0 };
            var p30Win = new PetWindow(p30Cfg);
            p30Win.Show();
            p30Win.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            string enterSkin30 = p30Win.Config.Skin;
            double enterScale30 = p30Win.CurrentScale;
            try
            {
                // 切到 hd → 自动套用 4
                p30Win.SetSkin("hd");
                if (Math.Abs(p30Win.CurrentScale - 4) > 1e-9)
                    throw new Exception($"切到 hd 应套用推荐缩放 4，实际 {p30Win.CurrentScale}");

                // 用户手动把缩放调回 2
                p30Win.SetScale(2.0);
                if (Math.Abs(p30Win.CurrentScale - 2) > 1e-9)
                    throw new Exception($"SetScale(2) 后应=2，实际 {p30Win.CurrentScale}");

                // 再点一次「同一皮肤 hd」→ 必须重新套用 4（这是设置页对已选中项也会回调的路径）
                p30Win.SetSkin("hd");
                if (Math.Abs(p30Win.CurrentScale - 4) > 1e-9)
                    throw new Exception($"重复选 hd 应重套 scale=4，实际 {p30Win.CurrentScale}（早返回吞掉了推荐缩放？）");
                if (Math.Abs(p30Win.Width - 128) > 0.5)
                    throw new Exception($"重复选 hd 后窗口宽应=128，实际 {p30Win.Width}");

                // ---------- 重复 SetSkin("pixel")（未声明推荐缩放）必须零副作用 ----------
                p30Win.SetSkin("pixel");   // atlas 变了，但 pixel 无推荐缩放 → 不改用户缩放
                if (p30Win.Config.Skin != "pixel")
                    throw new Exception($"切到 pixel 后 config.Skin 应=pixel，实际='{p30Win.Config.Skin}'");
                if (Math.Abs(p30Win.CurrentScale - 4) > 1e-9)
                    throw new Exception($"pixel 未声明推荐缩放，Scale 应保持 4 不变，实际 {p30Win.CurrentScale}");

                p30Win.SetScale(2.0);      // 用户再手动改成 2
                p30Win.SetSkin("pixel");   // 再点一次「同一皮肤 pixel」→ 未声明 → 不许拉回
                if (Math.Abs(p30Win.CurrentScale - 2) > 1e-9)
                    throw new Exception($"重复选 pixel（未声明推荐缩放）不该改缩放，应保持 2，实际 {p30Win.CurrentScale}");

                // ---------- B. 步进器 ± 必须从「活的当前值」起算 ----------
                // 直接反射驱动私有 MakeStepperButtons（不经 BuildStepperCard，降低脆弱性）。
                var p30Settings = new SettingsWindow(p30Win);
                p30Settings.Show();
                p30Settings.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                try
                {
                    var msw = typeof(SettingsWindow).GetMethod("MakeStepperButtons", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? throw new MissingMethodException("SettingsWindow.MakeStepperButtons 不存在");
                    double live = 1.0;               // 模拟「活的当前值」（外部写入者）
                    double? received = null;

                    // readCurrent 非空：点 + 必须基于 live 而非构建时的 initial
                    var btns = ((Border, Border))msw.Invoke(p30Settings, new object?[]
                    {
                        1.0, 0.2, (Action<double>)(v => received = v), (Func<double>)(() => live),
                    })!;
                    var incBtn = btns.Item2;
                    var decBtn = btns.Item1;

                    live = 4.0;   // 模拟切皮肤后外部把实时值改到 4.0
                    incBtn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = incBtn });
                    if (received is null || Math.Abs(received.Value - 4.2) > 1e-6)
                        throw new Exception($"readCurrent=4.0 时点 + 应回调 4.2（基于活值），实际 {received?.ToString() ?? "null"}");

                    received = null;
                    live = 4.2;
                    decBtn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = decBtn });
                    if (received is null || Math.Abs(received.Value - 4.0) > 1e-6)
                        throw new Exception($"readCurrent=4.2 时点 − 应回调 4.0（基于活值），实际 {received?.ToString() ?? "null"}");

                    // 回归：readCurrent=null 时退化为内部自持值（无外部写入者的卡片，行为必须不变）
                    received = null;
                    var btns2 = ((Border, Border))msw.Invoke(p30Settings, new object?[]
                    {
                        1.0, 0.5, (Action<double>)(v => received = v), (Func<double>?)null,
                    })!;
                    btns2.Item2.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = btns2.Item2 });
                    if (received is null || Math.Abs(received.Value - 1.5) > 1e-6)
                        throw new Exception($"readCurrent=null 时点 + 应回调 1.5（内部值 1.0+0.5），实际 {received?.ToString() ?? "null"}");
                }
                finally
                {
                    p30Settings.Close();
                }
            }
            finally
            {
                p30Win.SetSkin(enterSkin30);
                p30Win.SetScale(enterScale30);
                p30Win.Dispose();
                p30Win.Close();
            }
        });

        // ---- v0.1.82 第 31 项：新动画皮肤契约 + 设置页缩略图 ----
        Check("v0.1.82：动画皮肤契约（8 帧 / 静态态 fps=0）+ 设置页缩略图齐备", () =>
        {
            var animated = new[] { "cat_pixel", "cat_hd", "phoebe_pixel", "phoebe_hd" };
            var skins = SkinRegistry.Scan(Paths.Assets);
            var staticStates = new[] { "idle", "sit", "sleep" };

            foreach (var id in animated)
            {
                var info = skins.FirstOrDefault(s => s.Id == id)
                    ?? throw new Exception($"未扫到皮肤 {id}");
                if (info.Frames != 8)
                    throw new Exception($"{id} 的 frames 应为 8，实际 {info.Frames}");

                // 待机/坐/睡必须是静止（fps=0，帧不推进）；其余必须是动画（fps>0）
                foreach (var st in info.States)
                {
                    bool isStatic = staticStates.Contains(st.Name);
                    if (isStatic && st.Fps > 0)
                        throw new Exception($"{id}.{st.Name} 应静止(fps=0)，实际 {st.Fps}");
                    if (!isStatic && st.Fps <= 0)
                        throw new Exception($"{id}.{st.Name} 应有动画(fps>0)，实际 {st.Fps}");
                }

                // 帧契约：每张状态图总宽 = frameWidth × frames（否则引擎固定切帧会切到画布外 = 空白帧闪烁）
                var atlas = SpriteAtlas.LoadSkin(Paths.Assets, id);
                if (atlas.States.Count != 9)
                    throw new Exception($"{id} 应有 9 状态，实际 {atlas.States.Count}");
                foreach (var st in atlas.States.Values)
                {
                    if (st.Frames.Length != 8)
                        throw new Exception($"{id}.{st.Name} 应有 8 帧，实际 {st.Frames.Length}");
                    foreach (var fr in st.Frames)
                    {
                        if (fr.Bitmap.PixelWidth != info.FrameWidth || fr.Bitmap.PixelHeight != info.FrameHeight)
                            throw new Exception($"{id}.{st.Name} 帧尺寸应为 {info.FrameWidth}x{info.FrameHeight}，" +
                                                $"实际 {fr.Bitmap.PixelWidth}x{fr.Bitmap.PixelHeight}");
                    }
                }

                // 设置页缩略图：thumb.png 必须存在且为 96×96 RGBA（缺失时列表显示"无预览"）
                // ⚠️ 不能叫 preview.png —— csproj 的 `<None Remove="**\*preview*" />` 会把它排除掉，打不进输出目录
                var thumb = System.IO.Path.Combine(Paths.Assets, id, "thumb.png");
                if (!System.IO.File.Exists(thumb))
                    throw new Exception($"{id} 缺少 thumb.png（跑 tools/gen_skin_previews.py 生成）");
                using var fs = System.IO.File.OpenRead(thumb);
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
                if (bmp.PixelWidth != 96 || bmp.PixelHeight != 96)
                    throw new Exception($"{id}/thumb.png 应为 96x96，实际 {bmp.PixelWidth}x{bmp.PixelHeight}");
            }

            // 端到端：切到动画皮肤会套用推荐缩放 4
            var c31 = new AppConfig { Skin = "pixel", Scale = 1.0 };
            var w31 = new PetWindow(c31);
            w31.Show();
            w31.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            try
            {
                w31.SetSkin("cat_pixel");
                if (Math.Abs(w31.CurrentScale - 4) > 1e-9)
                    throw new Exception($"切到 cat_pixel 应套用推荐缩放 4，实际 {w31.CurrentScale}");
            }
            finally
            {
                w31.Dispose();
                w31.Close();
            }
        });

        Console.WriteLine(_fail == 0 ? "\n=== 全部通过 ===" : $"\n=== {_fail} 项失败 ===");
        Console.Out.Flush();
        // v0.1.82：用 Environment.Exit 强制退出。测试里创建过真实 WPF 窗口（PetWindow/RadialMenu），
        // 它们的 Dispatcher 与全局钩子会留下前台线程，正常 return 不保证进程结束
        // （表现为"打印完结果却不退出"，QA 复验时观察到过 19 分钟不退出）。
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static void Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"PASS: {name}");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"FAIL: {name} -> {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>取出对话区里所有气泡文本（气泡是 <c>Border { Child = TextBlock }</c>）。</summary>
    private static List<string> CollectBubbleTexts(StackPanel panel)
    {
        var result = new List<string>();
        foreach (var child in panel.Children)
        {
            if (child is Border { Child: TextBlock tb }) result.Add(tb.Text);
            else if (child is TextBlock direct) result.Add(direct.Text);
        }
        return result;
    }

    /// <summary>
    /// 取命理插件程序集：先等异步 LoadDeferred，再从已加载插件（其 ALC）取；拿不到则回退直接 LoadFrom 插件目录。
    /// </summary>
    private static Assembly ResolveBaziAssembly(PetWindow win)
    {
        var pump = new DispatcherFrame();
        var pt = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        pt.Tick += (_, _) => { pt.Stop(); pump.Continue = false; };
        pt.Start();
        Dispatcher.PushFrame(pump);

        var baziPlugin = win.PluginHost.Plugins.FirstOrDefault(p => p.Manifest.Id == "com.lantern.bazi");
        if (baziPlugin is not null) return baziPlugin.Instance.GetType().Assembly;

        var dll = Path.Combine(AppContext.BaseDirectory, "plugins", "com.lantern.bazi", "Bazi.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"命理插件未加载，且找不到 {dll}");
        return Assembly.LoadFrom(dll);
    }

    /// <summary>深度遍历控件树，收集所有类型为 <typeparamref name="T"/> 的节点（Panel / Decorator / ContentControl 三类容器）。</summary>
    private static void Collect<T>(object? node, List<T> outList) where T : class
    {
        if (node is null) return;
        if (node is T t) outList.Add(t);
        switch (node)
        {
            case Panel p:
                foreach (var c in p.Children) Collect(c, outList);
                break;
            case Decorator d when d.Child is not null:
                Collect(d.Child, outList);
                break;
            case ContentControl cc when cc.Content is not null:
                Collect(cc.Content, outList);
                break;
        }
    }
}

/// <summary>探针用的内存版 <see cref="DesktopPet.Core.IPluginStorage"/>（不落盘）。</summary>
public sealed class ProbeStorage : DesktopPet.Core.IPluginStorage
{
    private readonly Dictionary<string, object?> _map = new();
    public T? Get<T>(string key) => _map.TryGetValue(key, out var v) && v is T t ? t : default;
    public void Set<T>(string key, T value) => _map[key] = value;
    public void Remove(string key) => _map.Remove(key);
    public bool Contains(string key) => _map.ContainsKey(key);
}
