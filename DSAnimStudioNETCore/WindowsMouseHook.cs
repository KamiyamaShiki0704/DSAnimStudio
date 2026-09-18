using System;
using Linearstar.Windows.RawInput;
using System.Windows.Forms;
using ImGuiNET;
using Linearstar.Windows.RawInput.Native;

namespace DSAnimStudio
{
	class RawInputEventArgs : EventArgs
	{
		public RawInputEventArgs(RawInputData data)
		{
			Data = data;
		}

		public RawInputData Data { get; }
	}

	class RawInputReceiverWindow : NativeWindow
	{
		public event EventHandler<RawInputEventArgs> Input;

		public RawInputReceiverWindow()
		{
			CreateHandle(new CreateParams
			{
				X = 0,
				Y = 0,
				Width = 0,
				Height = 0,
				Style = 0x800000,
			});
		}

		protected override void WndProc(ref Message m)
		{
			const int WM_INPUT = 0x00FF;
			const int WM_CHAR = 0x0102;

			if (m.Msg == WM_INPUT)
			{
				var data = RawInputData.FromHandle(m.LParam);

				Input?.Invoke(this, new RawInputEventArgs(data));
			}
			else if (m.Msg == WM_CHAR)
			{
				ImGui.GetIO().AddInputCharacter((uint)m.WParam.ToInt64());
			}

			base.WndProc(ref m);
		}
	}

	class RawMouseMovedEventArgs : EventArgs
	{
		public readonly int X;
		public readonly int Y;
		public RawMouseMovedEventArgs(int x, int y)
		{
			X = x;
			Y = y;
		}
	}

	public static class WindowsMouseHook
    {
		static RawInputReceiverWindow receiver;

		public delegate void RawMouseMovedDelegate(int x, int y);

		public static RawMouseMovedDelegate RawMouseMoved;

		private static readonly RawMouseMotion motion = new();
		private static bool wasActive;
		public static bool IsAbsoluteMouseMotion => motion.IsAbsolute;
		public static void ResetAbsoluteMouseMotion() => motion.ResetAbsolutePositions();

		static void RawInputReceived(object sender, RawInputEventArgs e)
		{
			try
			{
				var data = e.Data;
				if (wasActive != Main.Active)
				{
					motion.ResetAbsolutePositions();
					wasActive = Main.Active;
				}
				switch (data)
				{
					case RawInputMouseData mouse:
						var desktop = System.Drawing.Rectangle.Empty;
						if ((mouse.Mouse.Flags & RawMouseFlags.MoveAbsolute) != 0)
							desktop = (mouse.Mouse.Flags & RawMouseFlags.VirtualDesktop) != 0
								? SystemInformation.VirtualScreen
								: new System.Drawing.Rectangle(System.Drawing.Point.Empty, SystemInformation.PrimaryMonitorSize);
						var delta = motion.Translate(RawInputDeviceHandle.GetRawValue(data.Header.DeviceHandle),
							mouse.Mouse.Flags, mouse.Mouse.LastX, mouse.Mouse.LastY, desktop);
						// A physical mouse may already have captured the cursor before RDP takes over.
						if (motion.IsAbsolute && Main.Input?.MouseCursorLocked == true)
							Main.Input.UnlockMouseCursor();
						if (delta.X != 0 || delta.Y != 0)
							RawMouseMoved?.Invoke(delta.X, delta.Y);
						break;
				}
			}
			catch
			{

			}
		}

		//public static void RefreshHook()
		//{
		//	receiver.Input -= RawInputReceived;
		//	receiver.DestroyHandle();
		//	receiver = new RawInputReceiverWindow();
		//	receiver.Input += RawInputReceived;
		//}

		public static void Hook(IntPtr hWnd)
        {
			motion.ResetAbsolutePositions();
			// To begin catching inputs, first make a window that listens WM_INPUT.
			receiver = new RawInputReceiverWindow();

			RawInputDevice.RegisterDevice(HidUsageAndPage.Mouse, RawInputDeviceFlags.InputSink, receiver.Handle);

			receiver.Input += RawInputReceived;
		}

		public static void Unhook()
		{
			motion.ResetAbsolutePositions();
			receiver?.ReleaseHandle();
			RawInputDevice.UnregisterDevice(HidUsageAndPage.Mouse);
		}
    }
}
