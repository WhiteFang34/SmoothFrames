using System;
using System.Globalization;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.Utils;
using VRageMath;

namespace SmoothFrames
{
	/// <summary>
	///     Configuration dialog opened from the Pulsar plugin loader's
	///     settings cog (auto-detected via <see cref="Plugin.OpenConfigDialog"/>).
	///     Exposes the interpolation toggle and frame-rate cap slider;
	///     changes apply live and persist to <c>SmoothFrames.cfg</c>.
	/// </summary>
	public class PluginConfigDialog : MyGuiScreenBase
	{
		private const string Caption = "Smooth Frames Configuration";

		// Dialog footprint — tuned to fit caption + two control rows + an OK
		// button without leaving empty space at the bottom edge.
		private static readonly Vector2 DialogSize = new Vector2(0.5f, 0.32f);

		// Local coordinate system used below: (0,0) is the dialog centre, top
		// edge sits at -DialogSize.Y / 2, bottom edge at +DialogSize.Y / 2.
		// `MyGuiScreenBase.AddCaption` puts the caption near the top with a
		// SCREEN_CAPTION_DELTA_Y (≈ 0.05) inset.
		private const float LabelX = -0.22f;
		private const float ControlX = 0.0f;
		private const float SliderWidth = 0.18f;
		private const float ValueX = 0.22f;

		private const float InterpolationRowY = -0.04f;
		private const float FrameRateRowY = 0.02f;
		private const float ButtonRowY = 0.10f;

		private MyGuiControlLabel _interpolationLabel;
		private MyGuiControlCheckbox _interpolationCheckbox;

		private MyGuiControlLabel _frameRateLabel;
		private MyGuiControlLabel _frameRateValueLabel;
		private MyGuiControlSlider _frameRateSlider;

		private MyGuiControlButton _closeButton;

		public PluginConfigDialog() : base(
			new Vector2(0.5f, 0.5f),
			MyGuiConstants.SCREEN_BACKGROUND_COLOR,
			DialogSize,
			false,
			null,
			MySandboxGame.Config.UIBkOpacity,
			MySandboxGame.Config.UIOpacity)
		{
			EnabledBackgroundFade = true;
			m_closeOnEsc = true;
			m_drawEvenWithoutFocus = true;
			CanHideOthers = true;
			CanBeHidden = true;
			CloseButtonEnabled = true;
		}

		public override string GetFriendlyName()
		{
			return nameof(PluginConfigDialog);
		}

		public override void LoadContent()
		{
			base.LoadContent();
			RecreateControls(true);
		}

		public override void RecreateControls(bool constructor)
		{
			base.RecreateControls(constructor);

			AddCaption(Caption);

			var config = Plugin.Config;

			_interpolationLabel = new MyGuiControlLabel
			{
				Position = new Vector2(LabelX, InterpolationRowY),
				OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
				Text = "Interpolation enabled"
			};
			Controls.Add(_interpolationLabel);

			_interpolationCheckbox = new MyGuiControlCheckbox(
				position: new Vector2(ControlX, InterpolationRowY),
				isChecked: Plugin.InterpolationEnabled,
				toolTip: "Smooth render frames between sim ticks. Same as Ctrl+F11.",
				originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
			_interpolationCheckbox.IsCheckedChanged += OnInterpolationCheckboxChanged;
			Controls.Add(_interpolationCheckbox);

			_frameRateLabel = new MyGuiControlLabel
			{
				Position = new Vector2(LabelX, FrameRateRowY),
				OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
				Text = "Frame rate cap"
			};
			Controls.Add(_frameRateLabel);

			_frameRateSlider = new MyGuiControlSlider(
				position: new Vector2(ControlX, FrameRateRowY),
				minValue: PluginConfig.MinFrameRateCap,
				maxValue: PluginConfig.MaxFrameRateCap,
				width: SliderWidth,
				defaultValue: PluginConfig.DefaultFrameRateCap,
				toolTip: "Maximum render frames per second.",
				originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
				intValue: true);
			_frameRateSlider.Value = config.FrameRateCap;
			_frameRateSlider.ValueChanged += OnFrameRateSliderChanged;
			Controls.Add(_frameRateSlider);

			_frameRateValueLabel = new MyGuiControlLabel
			{
				Position = new Vector2(ValueX, FrameRateRowY),
				OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER,
				Text = config.FrameRateCap.ToString(CultureInfo.InvariantCulture)
			};
			Controls.Add(_frameRateValueLabel);

			_closeButton = new MyGuiControlButton(
				position: new Vector2(0f, ButtonRowY),
				originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
				text: MyTexts.Get(MyCommonTexts.Ok),
				onButtonClick: OnOk);
			Controls.Add(_closeButton);
		}

		private void OnInterpolationCheckboxChanged(MyGuiControlCheckbox checkbox)
		{
			Plugin.SetInterpolationEnabled(checkbox.IsChecked);
		}

		private void OnFrameRateSliderChanged(MyGuiControlSliderBase slider)
		{
			// Apply live so the user sees the cap take effect while dragging,
			// but defer persistence — ValueChanged fires per integer increment
			// as the slider drags, which would otherwise rewrite the config
			// XML dozens of times per drag. The actual Save() runs once in
			// OnClosed.
			var value = (int)Math.Round(slider.Value);
			Plugin.Config.FrameRateCap = value;
			RenderFrameRateCap.TryApplyFrameRateCap(value);
			_frameRateValueLabel.Text = value.ToString(CultureInfo.InvariantCulture);
		}

		private void OnOk(MyGuiControlButton _)
		{
			CloseScreen();
		}

		protected override void OnClosed()
		{
			Plugin.Config.Save();
			base.OnClosed();
		}
	}
}
