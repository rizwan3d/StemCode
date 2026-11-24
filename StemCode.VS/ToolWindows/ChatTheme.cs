using System;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace StemCode.VS.ToolWindows
{
    /// <summary>
    /// The chat palette, taken from the active Visual Studio theme so the window matches
    /// Dark, Light, Blue and custom themes. Every brush is a single shared, unfrozen instance:
    /// when the theme changes only its Color is updated, so XAML ({x:Static}) and code-built
    /// elements repaint in place without being rebuilt.
    /// </summary>
    internal static class ChatBrushes
    {
        // Surfaces
        public static readonly SolidColorBrush Background = New();
        public static readonly SolidColorBrush Panel = New();
        public static readonly SolidColorBrush Hover = New();
        public static readonly SolidColorBrush Pressed = New();
        public static readonly SolidColorBrush Border = New();
        public static readonly SolidColorBrush UserBubble = New();
        public static readonly SolidColorBrush Scrim = New();

        // Text
        public static readonly SolidColorBrush Text = New();
        public static readonly SolidColorBrush Dim = New();
        public static readonly SolidColorBrush Code = New();
        public static readonly SolidColorBrush CodeBg = New();
        public static readonly SolidColorBrush Link = New();

        // Accent (primary buttons, focus, the send button)
        public static readonly SolidColorBrush Accent = New();
        public static readonly SolidColorBrush AccentText = New();

        // Inputs
        public static readonly SolidColorBrush InputBg = New();
        public static readonly SolidColorBrush InputText = New();
        public static readonly SolidColorBrush InputBorder = New();
        public static readonly SolidColorBrush InputFocus = New();

        // Meaning
        public static readonly SolidColorBrush Ok = New();
        public static readonly SolidColorBrush Busy = New();
        public static readonly SolidColorBrush Error = New();
        public static readonly SolidColorBrush ErrorBg = New();
        public static readonly SolidColorBrush AddFg = New();
        public static readonly SolidColorBrush AddBg = New();
        public static readonly SolidColorBrush DelFg = New();
        public static readonly SolidColorBrush DelBg = New();
        public static readonly SolidColorBrush WarnFg = New();
        public static readonly SolidColorBrush MetaFg = New();

        public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

        /// <summary>Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
        public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static bool IsDark { get; private set; } = true;

        static ChatBrushes()
        {
            Apply();
            try
            {
                VSColorTheme.ThemeChanged += _ =>
                {
                    if (Background.Dispatcher.CheckAccess()) Apply();
                    else Background.Dispatcher.BeginInvoke(new Action(Apply));
                };
            }
            catch
            {
                // Outside Visual Studio (designer, tests): keep the fallback palette.
            }
        }

        private static SolidColorBrush New() => new(Colors.Transparent);

        private static void Apply()
        {
            Color bg = Themed(EnvironmentColors.ToolWindowBackgroundColorKey, Rgb(0x1E, 0x1E, 0x1E));
            Color fg = Themed(EnvironmentColors.ToolWindowTextColorKey, Rgb(0xCC, 0xCC, 0xCC));
            IsDark = Luminance(bg) < 0.5;

            Background.Color = bg;
            Panel.Color = Mix(bg, fg, 0.045);
            Hover.Color = Mix(bg, fg, 0.10);
            Pressed.Color = Mix(bg, fg, 0.16);
            Border.Color = Mix(bg, fg, IsDark ? 0.17 : 0.20);
            UserBubble.Color = Mix(bg, fg, IsDark ? 0.08 : 0.06);
            Scrim.Color = IsDark ? Color.FromArgb(0x99, 0, 0, 0) : Color.FromArgb(0x55, 0, 0, 0);

            Text.Color = fg;
            Dim.Color = Mix(fg, bg, IsDark ? 0.38 : 0.42);
            Code.Color = fg;
            CodeBg.Color = Mix(bg, fg, IsDark ? 0.065 : 0.045);
            Link.Color = Themed(EnvironmentColors.ControlLinkTextColorKey,
                IsDark ? Rgb(0x4D, 0xAA, 0xFC) : Rgb(0x00, 0x5F, 0xB8));

            Color accent = Themed(EnvironmentColors.SystemHighlightColorKey,
                IsDark ? Rgb(0x0E, 0x63, 0x9C) : Rgb(0x00, 0x5F, 0xB8));
            Accent.Color = accent;
            AccentText.Color = Luminance(accent) > 0.6 ? Colors.Black : Colors.White;

            InputBg.Color = Themed(CommonControlsColors.TextBoxBackgroundColorKey, Mix(bg, fg, 0.03));
            InputText.Color = Themed(CommonControlsColors.TextBoxTextColorKey, fg);
            InputBorder.Color = Themed(CommonControlsColors.TextBoxBorderColorKey, Border.Color);
            InputFocus.Color = Themed(CommonControlsColors.TextBoxBorderFocusedColorKey, accent);

            if (IsDark)
            {
                Ok.Color = Rgb(0x73, 0xC9, 0x91);
                Busy.Color = Rgb(0xCC, 0xA7, 0x00);
                Error.Color = Rgb(0xF4, 0x87, 0x71);
                AddFg.Color = Rgb(0x9C, 0xDC, 0xAA);
                DelFg.Color = Rgb(0xF4, 0x87, 0x71);
                WarnFg.Color = Rgb(0xE2, 0xC0, 0x8D);
                MetaFg.Color = Rgb(0x6A, 0x95, 0xD6);
            }
            else
            {
                Ok.Color = Rgb(0x1A, 0x7F, 0x37);
                Busy.Color = Rgb(0x9A, 0x67, 0x00);
                Error.Color = Rgb(0xCF, 0x22, 0x2E);
                AddFg.Color = Rgb(0x1A, 0x7F, 0x37);
                DelFg.Color = Rgb(0xCF, 0x22, 0x2E);
                WarnFg.Color = Rgb(0x9A, 0x67, 0x00);
                MetaFg.Color = Rgb(0x05, 0x50, 0xAE);
            }

            ErrorBg.Color = Mix(bg, Error.Color, IsDark ? 0.16 : 0.10);
            AddBg.Color = Color.FromArgb(0x33, AddFg.Color.R, AddFg.Color.G, AddFg.Color.B);
            DelBg.Color = Color.FromArgb(0x33, DelFg.Color.R, DelFg.Color.G, DelFg.Color.B);
        }

        private static Color Themed(ThemeResourceKey key, Color fallback)
        {
            try
            {
                System.Drawing.Color c = VSColorTheme.GetThemedColor(key);
                return c.A == 0 ? fallback : Color.FromArgb(c.A, c.R, c.G, c.B);
            }
            catch
            {
                return fallback;
            }
        }

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        /// <summary>Blends <paramref name="a"/> toward <paramref name="b"/> by <paramref name="t"/> (0..1).</summary>
        private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));

        private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    }

    /// <summary>Resource keys of the styles in ChatToolWindowControl.xaml, for code-built controls.</summary>
    internal static class ChatStyles
    {
        public const string Button = "ChatButton";
        public const string PrimaryButton = "ChatPrimaryButton";
        public const string DangerButton = "ChatDangerButton";
        public const string MenuButton = "ChatMenuButton";
        public const string Input = "ChatInput";
    }

    /// <summary>Status of the CLI, shown as the colored dot in the header.</summary>
    internal enum StatusKind { Idle, Busy, Ready, Error }
}
