namespace DesktopPet.Core;

/// <summary>宠物情绪状态</summary>
public enum PetMood
{
    Idle,
    Happy,
    Sleepy,
    Angry,
    Sad,
}

/// <summary>宠物行为状态（对应雪碧图）</summary>
public enum PetState
{
    Idle,
    Walk,
    Sit,
    Sleep,
    Pet,
    Drag,
    Fall,
    Climb,
    /// <summary>v0.1.48：工作模式（BongoCat 化）——猫伏低前探 + 前爪伸向下方键盘</summary>
    Work,
}

/// <summary>2D 坐标（像素）</summary>
public readonly record struct Point2(double X, double Y)
{
    public static readonly Point2 Zero = new(0, 0);
}

/// <summary>矩形区域</summary>
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>插件权限（[Flags]）</summary>
[Flags]
public enum PluginPermissions : ulong
{
    None       = 0,
    /// <summary>播放宠物动画</summary>
    PetAnimate = 1L << 0,
    /// <summary>移动宠物</summary>
    PetMove    = 1L << 1,
    /// <summary>读取宠物位置与状态</summary>
    PetRead    = 1L << 2,
    /// <summary>注册径向菜单 / 托盘项 / 设置页</summary>
    UIRegister = 1L << 3,
    /// <summary>插件私有存储</summary>
    Storage    = 1L << 4,
    /// <summary>定时任务</summary>
    Scheduler  = 1L << 5,
    /// <summary>系统通知</summary>
    Notify     = 1L << 6,
    /// <summary>剪贴板读写（中风险）</summary>
    Clipboard  = 1L << 7,
    /// <summary>网络请求（中风险，域名白名单）</summary>
    Network    = 1L << 8,
    /// <summary>文件系统（高风险，需二次确认）</summary>
    FileAccess = 1L << 9,
    /// <summary>启动进程（高风险，需二次确认）</summary>
    Process    = 1L << 10,
    // ---- v0.1.80：权限细分（把"合法但被粗 token 误伤"的能力拆出来）----
    /// <summary>仅写入本插件自己的数据目录 data/plugins/&lt;id&gt;/（低危；不等于任意文件读写）</summary>
    FileOwnDir = 1L << 11,
    /// <summary>发送系统媒体键（上一曲 / 播放暂停 / 下一曲）（低危；不等于启动任意进程）</summary>
    MediaKey   = 1L << 12,
}
