using DesktopPet.App.Core;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopPet.App.Services;
using DesktopPet.App.Windows;
using DesktopPet.Core;

internal static partial class Program
{
    private static void RunCompanionChecks()
    {
        Check("host wheel relocates when keyboard resizes and remains separate from companions", () =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var pet = new PetWindow(new AppConfig { Scale = 3.2, AppMode = "work", PosX = 500, PosY = 300 });
            try
            {
                pet.Show(); pet.SetAppMode("work"); pet.LoadPlugins();
                pet.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                pet.OpenRadialMenu();
                var menu = (RadialMenu)typeof(PetWindow).GetField("_radialMenu", flags)!.GetValue(pet)!;
                var keyboard = (InputMirrorWindow)typeof(PetWindow).GetField("_mirrorWindow", flags)!.GetValue(pet)!;
                foreach (var scale in new[] { .5, 1.0, 2.0 })
                {
                    pet.SetKeyboardScale(scale);
                    typeof(PetWindow).GetMethod("SyncFollowers", flags)!.Invoke(pet, null);
                    var bounds = new Rect(menu.Left, menu.Top, menu.Width, menu.Height);
                    Require(!bounds.IntersectsWith(new Rect(pet.Left, pet.Top, pet.Width, pet.Height)), "host menu covers cat");
                    Require(!bounds.IntersectsWith(new Rect(keyboard.Left, keyboard.Top, keyboard.Width, keyboard.Height)), "host menu covers keyboard");
                }
                keyboard.Width = SystemParameters.WorkArea.Width;
                keyboard.Height = SystemParameters.WorkArea.Height;
                typeof(PetWindow).GetMethod("SyncFollowers", flags)!.Invoke(pet, null);
                Require(!keyboard.IsVisible, "menu covered an oversized keyboard instead of clearing space");
                menu.HideMenu();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
                Require(keyboard.IsVisible, "closing menu did not restore keyboard");
                pet.SetKeyboardScale(1); pet.OpenRadialMenu();
                var output = Environment.GetEnvironmentVariable("NEKO_CAPTURE_COMPANION");
                if (!string.IsNullOrEmpty(output))
                {
                    System.IO.Directory.CreateDirectory(output);
                    pet.SetKeyboardScale(1);
                    typeof(PetWindow).GetMethod("SyncFollowers", flags)!.Invoke(pet, null);
                    CaptureUiWindow(menu, System.IO.Path.Combine(output, "menu.png"));
                    CaptureUiWindow(keyboard, System.IO.Path.Combine(output, "keyboard.png"));
                    var windows = new Window[] { pet, menu, keyboard };
                    double left = windows.Min(w => w.Left) - 16, top = windows.Min(w => w.Top) - 16;
                    double width = windows.Max(w => w.Left + w.Width) - left + 16;
                    double height = windows.Max(w => w.Top + w.Height) - top + 16;
                    var drawing = new DrawingVisual();
                    using (var dc = drawing.RenderOpen())
                    {
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 35, 43)), null, new Rect(0, 0, width, height));
                        foreach (var window in windows)
                            dc.DrawRectangle(new VisualBrush(window), null, new Rect(window.Left-left, window.Top-top, window.ActualWidth, window.ActualHeight));
                    }
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(output, "companion.png")); encoder.Save(file);
                }
            }
            finally { pet.Dispose(); pet.Close(); }
        });
        Check("companion wheel avoids cat and keyboard at screen edges and all keyboard scales", () =>
        {
            var work = new Rect(-1600, 0, 1600, 900);
            foreach (var scale in new[] { .5, 1.0, 2.0 })
            foreach (var x in new[] { -1600.0, -900, -120 })
            foreach (var y in new[] { 0.0, 400, 780 })
            {
                var cat = new Rect(x, y, 120, 120);
                double w = 444 * scale, h = 190 * scale;
                var keyboard = new Rect(Math.Clamp(x + 60 - w / 2, work.Left, work.Right - w),
                    y + 132 + h <= work.Bottom ? y + 132 : Math.Max(0, y - h - 12), w, h);
                var menu = new Rect(CompanionLayout.Place(new Size(202, 228), work, new[] { cat, keyboard }), new Size(202, 228));
                Require(work.Contains(menu), "menu escaped work area");
                Require(!menu.IntersectsWith(cat) && !menu.IntersectsWith(keyboard), $"menu overlapped companion at {x},{y} scale {scale}");
            }
        });
        Check("live mouse follows global motion, stays bounded, and detaches on close", () =>
        {
            using var input = new GlobalInput();
            var window = new InputMirrorWindow(input, new AppConfig(), () => { });
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var signal = typeof(GlobalInput).GetField("RawMouseMoveEvent", flags)!;
            try
            {
                window.Show(); window.UpdateLayout();
                var callback = (Action<Point>)signal.GetValue(input)!;
                callback(new Point(0, 0)); callback(new Point(10000, -10000));
                var tick = typeof(InputMirrorWindow).GetMethod("OnMouseMotionTick", flags)!;
                for (int i = 0; i < 40; i++) tick.Invoke(window, new object?[] { null, EventArgs.Empty });
                var motion = (TranslateTransform)typeof(InputMirrorWindow).GetField("_mouseMotion", flags)!.GetValue(window)!;
                Require(motion.X > 5 && motion.X <= 6 && motion.Y < -21 && motion.Y >= -22, "mouse did not follow bounded movement");
                window.FlashMouse(0, true); Require(window.IsMousePawDown, "moving mouse lost click feedback");
                window.FlashMouse(0, false); window.FlashWheel();
                window.Hide(); callback(new Point(-10000, 10000));
                var timer = (DispatcherTimer)typeof(InputMirrorWindow).GetField("_motionTimer", flags)!.GetValue(window)!;
                Require(!timer.IsEnabled, "hidden mouse still animates");
                window.Close();
                Require(signal.GetValue(input) is null, "motion subscription leaked after close");
            }
            finally { if (window.IsVisible) window.Close(); }
        });
    }
}

