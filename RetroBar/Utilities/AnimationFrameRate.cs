using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace RetroBar.Utilities
{
    /// <summary>
    /// WPF draws its animations at 60 frames a second unless asked for more: on a 165 Hz screen the popup's lyrics scroll and fades moved in
    /// steps of 16 ms, not 6, and unevenly. The rate animations ask for is set to the highest refresh rate of the screens (at the most 240).
    /// </summary>
    internal static class AnimationFrameRate
    {
        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        /// <summary>Called once, before any animation exists.</summary>
        public static void Apply()
        {
            int rate = Highest();
            if (rate > 60)
            {
                Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(rate));
            }
        }

        /// <summary>The highest refresh rate of the screens, from 60 to 240.</summary>
        public static int Highest()
        {
            int highest = 60;
            try
            {
                foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
                {
                    var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    if (EnumDisplaySettings(screen.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
                    {
                        highest = Math.Max(highest, mode.dmDisplayFrequency);
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // The rate stays the default.
            }

            return Math.Min(highest, 240);
        }
    }
}
