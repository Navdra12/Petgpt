using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PetGPT.Models;

namespace PetGPT.Services;

public static class WindowPositionService
{
    private const double WindowMarginDip = 8;
    private const double BubbleGapDip = 10;
    private const uint MonitorInfoPrimary = 1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;

    public static double DipToPx(double valueDip, double dpi) => valueDip * ValidateDpi(dpi) / 96d;

    public static double PxToDip(double valuePx, double dpi) => valuePx * 96d / ValidateDpi(dpi);

    public static ScreenRectPx RestorePlacement(
        WindowPlacement placement,
        SizeDip sizeDip,
        IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var monitor = SelectRestoreMonitor(placement, monitors);
        var widthPx = DipToPx(sizeDip.Width, monitor.DpiX);
        var heightPx = DipToPx(sizeDip.Height, monitor.DpiY);

        double leftPx;
        double topPx;
        if (placement.MonitorId is null &&
            placement.XWithinWorkAreaDip.HasValue &&
            placement.YWithinWorkAreaDip.HasValue)
        {
            // T2 stored global WPF coordinates without monitor identity. Treat the
            // numeric values as the best available screen-space estimate, then
            // clamp. Historical mixed-DPI intent cannot be reconstructed exactly.
            leftPx = placement.XWithinWorkAreaDip.Value;
            topPx = placement.YWithinWorkAreaDip.Value;
        }
        else
        {
            leftPx = monitor.WorkAreaPx.Left + DipToPx(placement.XWithinWorkAreaDip ?? 0, monitor.DpiX);
            topPx = monitor.WorkAreaPx.Top + DipToPx(placement.YWithinWorkAreaDip ?? 0, monitor.DpiY);
        }

        return ClampToMonitor(
            new ScreenRectPx(leftPx, topPx, widthPx, heightPx),
            monitor);
    }

    public static WindowPlacement CreatePlacement(
        ScreenRectPx windowRectPx,
        IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = SelectMonitorForRect(windowRectPx, RequireMonitors(monitors));
        return new WindowPlacement(
            monitor.Id,
            PxToDip(windowRectPx.Left - monitor.WorkAreaPx.Left, monitor.DpiX),
            PxToDip(windowRectPx.Top - monitor.WorkAreaPx.Top, monitor.DpiY));
    }

    public static ScreenRectPx PositionFollowPet(
        ScreenRectPx petRectPx,
        SizeDip bubbleSizeDip,
        IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = SelectMonitorForRect(petRectPx, RequireMonitors(monitors));
        var marginX = DipToPx(WindowMarginDip, monitor.DpiX);
        var marginY = DipToPx(WindowMarginDip, monitor.DpiY);
        var gap = DipToPx(BubbleGapDip, monitor.DpiY);
        var width = Math.Min(
            DipToPx(bubbleSizeDip.Width, monitor.DpiX),
            Math.Max(1, monitor.WorkAreaPx.Width - (2 * marginX)));
        var height = Math.Min(
            DipToPx(bubbleSizeDip.Height, monitor.DpiY),
            Math.Max(1, monitor.WorkAreaPx.Height - (2 * marginY)));
        var left = petRectPx.Left + ((petRectPx.Width - width) / 2);
        var above = petRectPx.Top - height - gap;
        var top = above >= monitor.WorkAreaPx.Top + marginY
            ? above
            : petRectPx.Bottom + gap;

        return ClampToMonitor(new ScreenRectPx(left, top, width, height), monitor);
    }

    public static ScreenRectPx MoveByScreenDelta(
        ScreenRectPx initialWindowRectPx,
        ScreenPointPx pointerStartPx,
        ScreenPointPx pointerCurrentPx) =>
        new(
            initialWindowRectPx.Left + pointerCurrentPx.X - pointerStartPx.X,
            initialWindowRectPx.Top + pointerCurrentPx.Y - pointerStartPx.Y,
            initialWindowRectPx.Width,
            initialWindowRectPx.Height);

    public static ScreenRectPx ResizeAroundAnchor(
        ScreenRectPx currentRectPx,
        double oldAnchorX,
        double oldAnchorY,
        SizeDip newSizeDip,
        double newAnchorX,
        double newAnchorY,
        IReadOnlyList<MonitorInfo> monitors)
    {
        ValidateAnchor(oldAnchorX, nameof(oldAnchorX));
        ValidateAnchor(oldAnchorY, nameof(oldAnchorY));
        ValidateAnchor(newAnchorX, nameof(newAnchorX));
        ValidateAnchor(newAnchorY, nameof(newAnchorY));
        var available = RequireMonitors(monitors);
        var monitor = SelectMonitorForRect(currentRectPx, available);
        var widthPx = DipToPx(newSizeDip.Width, monitor.DpiX);
        var heightPx = DipToPx(newSizeDip.Height, monitor.DpiY);
        var screenAnchorX = currentRectPx.Left + (currentRectPx.Width * oldAnchorX);
        var screenAnchorY = currentRectPx.Top + (currentRectPx.Height * oldAnchorY);
        var desired = new ScreenRectPx(
            screenAnchorX - (widthPx * newAnchorX),
            screenAnchorY - (heightPx * newAnchorY),
            widthPx,
            heightPx);
        return ClampToMonitor(desired, monitor);
    }

    public static ScreenRectPx ClampToAvailableWorkArea(
        ScreenRectPx windowRectPx,
        IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = SelectMonitorForRect(windowRectPx, RequireMonitors(monitors));
        return ClampToMonitor(windowRectPx, monitor);
    }

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        MonitorEnumProc callback = (monitorHandle, _, _, _) =>
        {
            var native = new NativeMonitorInfo
            {
                Size = Marshal.SizeOf<NativeMonitorInfo>()
            };

            if (!GetMonitorInfo(monitorHandle, ref native))
                return true;

            var dpiX = 96u;
            var dpiY = 96u;
            try
            {
                if (GetDpiForMonitor(monitorHandle, 0, out var detectedX, out var detectedY) == 0)
                {
                    dpiX = detectedX;
                    dpiY = detectedY;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            monitors.Add(new MonitorInfo(
                native.DeviceName,
                ToScreenRect(native.Monitor),
                ToScreenRect(native.WorkArea),
                dpiX,
                dpiY,
                (native.Flags & MonitorInfoPrimary) != 0));
            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || monitors.Count == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate display monitors.");

        return monitors;
    }

    public static ScreenPointPx GetCursorPositionPx()
    {
        if (!GetCursorPos(out var point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the cursor position.");

        return new ScreenPointPx(point.X, point.Y);
    }

    public static ScreenRectPx GetWindowRectPx(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!GetWindowRect(handle, out var rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the window position.");

        return ToScreenRect(rectangle);
    }

    public static void PutPetAtDefault(Window pet)
    {
        PutPetAtDefault(pet, GetMonitors());
    }

    public static void PutPetAtDefault(Window pet, IReadOnlyList<MonitorInfo> monitors)
    {
        monitors = RequireMonitors(monitors);
        var monitor = monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? monitors[0];
        var size = GetWindowSizeDip(pet);
        var width = DipToPx(size.Width, monitor.DpiX);
        var height = DipToPx(size.Height, monitor.DpiY);
        var desired = new ScreenRectPx(
            monitor.WorkAreaPx.Right - width - DipToPx(24, monitor.DpiX),
            monitor.WorkAreaPx.Bottom - height - DipToPx(16, monitor.DpiY),
            width,
            height);
        ApplyScreenRect(pet, ClampToMonitor(desired, monitor), resize: true);
    }

    public static void RestorePet(Window pet, PetPlacementSettings placement)
    {
        RestorePet(pet, placement, GetMonitors());
    }

    public static void RestorePet(
        Window pet,
        PetPlacementSettings placement,
        IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var rectangle = RestorePlacement(
            new WindowPlacement(
                placement.MonitorId,
                placement.XWithinWorkAreaDip,
                placement.YWithinWorkAreaDip),
            GetWindowSizeDip(pet),
            monitors);
        ApplyScreenRect(pet, rectangle, resize: true);
    }

    public static void PositionBubble(Window bubble, Window pet, ChatWindowSettings settings)
    {
        PositionBubble(bubble, pet, settings, GetMonitors());
    }

    public static void PositionBubble(
        Window bubble,
        Window pet,
        ChatWindowSettings settings,
        IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(settings);
        monitors = RequireMonitors(monitors);
        var size = new SizeDip(settings.WidthDip, settings.HeightDip);
        var rectangle = settings.PlacementMode == "FollowPet"
            ? PositionFollowPet(GetWindowRectPx(pet), size, monitors)
            : RestorePlacement(
                new WindowPlacement(
                    settings.MonitorId,
                    settings.XWithinWorkAreaDip,
                    settings.YWithinWorkAreaDip),
                size,
                monitors);
        ApplyScreenRect(bubble, rectangle, resize: true);
    }

    public static void MoveWindowToScreenRect(Window window, ScreenRectPx rectanglePx) =>
        ApplyScreenRect(window, rectanglePx, resize: false);

    public static void ResizeWindowToScreenRect(Window window, ScreenRectPx rectanglePx) =>
        ApplyScreenRect(window, rectanglePx, resize: true);

    public static WindowPlacement CapturePlacement(Window window) =>
        CreatePlacement(GetWindowRectPx(window), GetMonitors());

    public static WindowPlacement CapturePlacement(
        Window window,
        IReadOnlyList<MonitorInfo> monitors) =>
        CreatePlacement(GetWindowRectPx(window), monitors);

    public static void ClampWindowToAvailableWorkArea(Window window)
    {
        ClampWindowToAvailableWorkArea(window, GetMonitors());
    }

    public static void ClampWindowToAvailableWorkArea(
        Window window,
        IReadOnlyList<MonitorInfo> monitors)
    {
        var clamped = ClampToAvailableWorkArea(GetWindowRectPx(window), monitors);
        ApplyScreenRect(window, clamped, resize: true);
    }

    private static MonitorInfo SelectRestoreMonitor(
        WindowPlacement placement,
        IReadOnlyList<MonitorInfo> monitors)
    {
        var available = RequireMonitors(monitors);
        if (!string.IsNullOrEmpty(placement.MonitorId))
        {
            var saved = available.FirstOrDefault(candidate =>
                candidate.Id.Equals(placement.MonitorId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
                return saved;

            return available.FirstOrDefault(candidate => candidate.IsPrimary) ?? available[0];
        }

        if (placement.XWithinWorkAreaDip.HasValue && placement.YWithinWorkAreaDip.HasValue)
        {
            return SelectMonitorForPoint(
                new ScreenPointPx(
                    placement.XWithinWorkAreaDip.Value,
                    placement.YWithinWorkAreaDip.Value),
                available);
        }

        return available.FirstOrDefault(candidate => candidate.IsPrimary) ?? available[0];
    }

    private static MonitorInfo SelectMonitorForRect(
        ScreenRectPx rectangle,
        IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        var bestIntersection = 0d;
        foreach (var monitor in monitors)
        {
            var intersection = IntersectionArea(rectangle, monitor.BoundsPx);
            if (intersection > bestIntersection)
            {
                bestIntersection = intersection;
                best = monitor;
            }
        }

        return best ?? SelectMonitorForPoint(rectangle.Center, monitors);
    }

    private static MonitorInfo SelectMonitorForPoint(
        ScreenPointPx point,
        IReadOnlyList<MonitorInfo> monitors) =>
        monitors
            .OrderBy(monitor => DistanceSquaredToRectangle(point, monitor.BoundsPx))
            .ThenByDescending(monitor => monitor.IsPrimary)
            .First();

    private static ScreenRectPx ClampToMonitor(ScreenRectPx rectangle, MonitorInfo monitor)
    {
        var marginX = DipToPx(WindowMarginDip, monitor.DpiX);
        var marginY = DipToPx(WindowMarginDip, monitor.DpiY);
        var width = Math.Min(
            Math.Max(1, rectangle.Width),
            Math.Max(1, monitor.WorkAreaPx.Width - (2 * marginX)));
        var height = Math.Min(
            Math.Max(1, rectangle.Height),
            Math.Max(1, monitor.WorkAreaPx.Height - (2 * marginY)));
        var minimumLeft = monitor.WorkAreaPx.Left + marginX;
        var minimumTop = monitor.WorkAreaPx.Top + marginY;
        var maximumLeft = Math.Max(minimumLeft, monitor.WorkAreaPx.Right - width - marginX);
        var maximumTop = Math.Max(minimumTop, monitor.WorkAreaPx.Bottom - height - marginY);

        return new ScreenRectPx(
            Math.Clamp(rectangle.Left, minimumLeft, maximumLeft),
            Math.Clamp(rectangle.Top, minimumTop, maximumTop),
            width,
            height);
    }

    private static IReadOnlyList<MonitorInfo> RequireMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));

        return monitors;
    }

    private static double ValidateDpi(double dpi)
    {
        if (!double.IsFinite(dpi) || dpi <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpi));

        return dpi;
    }

    private static void ValidateAnchor(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static double IntersectionArea(ScreenRectPx first, ScreenRectPx second)
    {
        var width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        var height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return width * height;
    }

    private static double DistanceSquaredToRectangle(ScreenPointPx point, ScreenRectPx rectangle)
    {
        var dx = point.X < rectangle.Left
            ? rectangle.Left - point.X
            : point.X > rectangle.Right
                ? point.X - rectangle.Right
                : 0;
        var dy = point.Y < rectangle.Top
            ? rectangle.Top - point.Y
            : point.Y > rectangle.Bottom
                ? point.Y - rectangle.Bottom
                : 0;
        return (dx * dx) + (dy * dy);
    }

    private static SizeDip GetWindowSizeDip(Window window)
    {
        var width = double.IsFinite(window.ActualWidth) && window.ActualWidth > 0
            ? window.ActualWidth
            : window.Width;
        var height = double.IsFinite(window.ActualHeight) && window.ActualHeight > 0
            ? window.ActualHeight
            : window.Height;
        return new SizeDip(width, height);
    }

    private static void ApplyScreenRect(Window window, ScreenRectPx rectanglePx, bool resize)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var flags = SwpNoActivate | SwpNoZOrder | (resize ? 0u : SwpNoSize);
        if (!SetWindowPos(
                handle,
                IntPtr.Zero,
                RoundToInt(rectanglePx.Left),
                RoundToInt(rectanglePx.Top),
                Math.Max(1, RoundToInt(rectanglePx.Width)),
                Math.Max(1, RoundToInt(rectanglePx.Height)),
                flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to position the window.");
        }
    }

    private static int RoundToInt(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static ScreenRectPx ToScreenRect(NativeRectangle rectangle) =>
        new(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right - rectangle.Left,
            rectangle.Bottom - rectangle.Top);

    private delegate bool MonitorEnumProc(
        IntPtr monitorHandle,
        IntPtr monitorDeviceContext,
        IntPtr monitorRectangle,
        IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumProc callback,
        IntPtr userData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref NativeMonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitorHandle,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
