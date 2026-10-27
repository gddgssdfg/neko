using System.Windows;
using System.Windows.Media.Imaging;

namespace DesktopPet.App.Services;

public static class BrandIcons
{
    private static BitmapFrame? _windowIcon;
    public static void Apply(Window window)
    {
        if (window.Icon is not null) return;
        if (_windowIcon is null)
        {
            _windowIcon = BitmapFrame.Create(new Uri("pack://application:,,,/Neko;component/Branding/App.ico"), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            _windowIcon.Freeze();
        }
        window.SetCurrentValue(Window.IconProperty, _windowIcon);
    }
}
