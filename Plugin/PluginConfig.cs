using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Serialization;

namespace SmoothFrames
{
	/// <summary>
	///     XML-serialized plugin settings persisted between sessions. Loaded
	///     once during <see cref="Plugin.Init"/>; mutated from the config
	///     dialog and saved on every change.
	/// </summary>
	public class PluginConfig
	{
		public const int MinFrameRateCap = 60;

		// Slider max + first-install default both track the user's primary
		// monitor refresh rate. Static-readonly (not const) because the
		// detection runs at type init via P/Invoke; the slider in
		// PluginConfigDialog binds to these and picks up whatever the monitor
		// reports.
		public static readonly int MaxFrameRateCap = DetectMonitorRefresh();
		public static readonly int DefaultFrameRateCap = MaxFrameRateCap;

		public int FrameRateCap { get; set; } = DefaultFrameRateCap;

		public bool InterpolationEnabled { get; set; } = true;

		// Private field — XmlSerializer skips non-public members, so this stays
		// out of the persisted file without an explicit attribute.
		private string _path;

		public static PluginConfig Load(string path)
		{
			PluginConfig config = null;
			if (File.Exists(path))
			{
				try
				{
					var serializer = new XmlSerializer(typeof(PluginConfig));
					using (var stream = File.OpenText(path))
					{
						config = (PluginConfig)serializer.Deserialize(stream);
					}
				}
				catch
				{
					config = null;
				}
			}

			if (config == null)
			{
				config = new PluginConfig();
			}

			config._path = path;
			config.Clamp();
			return config;
		}

		public void Save()
		{
			if (string.IsNullOrEmpty(_path))
			{
				return;
			}

			Clamp();
			try
			{
				var serializer = new XmlSerializer(typeof(PluginConfig));
				using (var stream = File.CreateText(_path))
				{
					serializer.Serialize(stream, this);
				}
			}
			catch
			{
				// Best-effort persistence; ignore disk errors.
			}
		}

		private void Clamp()
		{
			if (FrameRateCap < MinFrameRateCap)
			{
				FrameRateCap = MinFrameRateCap;
			}
			else if (FrameRateCap > MaxFrameRateCap)
			{
				FrameRateCap = MaxFrameRateCap;
			}
		}

		private static int DetectMonitorRefresh()
		{
			try
			{
				var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
				if (EnumDisplaySettings(null, EnumCurrentSettings, ref dm) && dm.dmDisplayFrequency > 0)
				{
					return dm.dmDisplayFrequency;
				}
			}
			catch
			{
				// Detection failed (e.g. headless / unusual display setup); fall
				// through to the fallback.
			}

			return 240;
		}

		private const int EnumCurrentSettings = -1;

		[DllImport("user32.dll", CharSet = CharSet.Auto)]
		private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

		// Standard layout for Win32 DEVMODE; only dmDisplayFrequency is read
		// but the full prefix is required so the offset is correct.
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
		private struct DEVMODE
		{
			private const int CchDeviceName = 32;
			private const int CchFormName = 32;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
			public string dmDeviceName;
			public short dmSpecVersion;
			public short dmDriverVersion;
			public short dmSize;
			public short dmDriverExtra;
			public int dmFields;
			public short dmOrientation;
			public short dmPaperSize;
			public short dmPaperLength;
			public short dmPaperWidth;
			public short dmScale;
			public short dmCopies;
			public short dmDefaultSource;
			public short dmPrintQuality;
			public short dmColor;
			public short dmDuplex;
			public short dmYResolution;
			public short dmTTOption;
			public short dmCollate;
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
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
	}
}
