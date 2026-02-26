//////////////////////////////////////////////////////////////////////////////////
//	Wiimote.cs
//	Managed Wiimote Library
//	Written by Brian Peek (http://www.brianpeek.com/)
//	for MSDN's Coding4Fun (http://msdn.microsoft.com/coding4fun/)
//	Visit http://blogs.msdn.com/coding4fun/archive/2007/03/14/1879033.aspx
//  and http://www.codeplex.com/WiimoteLib
//	for more information
//////////////////////////////////////////////////////////////////////////////////

using System.Runtime.InteropServices;
using System.Diagnostics;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace WiimoteLib
{
	/// <summary>
	/// Implementation of Wiimote
	/// </summary>
	public class Wiimote : IDisposable, IAsyncDisposable
	{
		private readonly HidDevice _hidDevice;

		private HidStream? _hidStream;
		public HidStream HidStream => _hidStream ?? throw new InvalidOperationException("HidStream is null");

		public Wiimote(HidDevice hidDevice)
		{
			_hidDevice = hidDevice;
		}

		/// <summary>
		/// Event raised when Wiimote state is changed
		/// </summary>
		public event EventHandler<WiimoteChangedEventArgs>? WiimoteChanged;

		/// <summary>
		/// Event raised when an extension is inserted or removed
		/// </summary>
		public event EventHandler<WiimoteExtensionChangedEventArgs>? WiimoteExtensionChanged;

		// VID = Nintendo, PID = Wiimote
		private const int VID = 0x057e;
		private const int PID = 0x0306;
		private const int PID_WITHMOTIONPLUS = 0x0330;

		// sure, we could find this out the hard way using HID, but trust me, it's 22
		private const int REPORT_LENGTH = 22;

		// Wiimote registers
		private const int REGISTER_IR				= 0x04b00030;
		private const int REGISTER_IR_SENSITIVITY_1	= 0x04b00000;
		private const int REGISTER_IR_SENSITIVITY_2	= 0x04b0001a;
		private const int REGISTER_IR_MODE			= 0x04b00033;

		private const int REGISTER_EXTENSION_INIT_1			= 0x04a400f0;
		private const int REGISTER_EXTENSION_INIT_2			= 0x04a400fb;
		private const int REGISTER_EXTENSION_TYPE			= 0x04a400fa;
		private const int REGISTER_EXTENSION_CALIBRATION	= 0x04a40020;

		// length between board sensors
		private const int BSL = 43;

		// width between board sensors
		private const int BSW = 24;

		// report buffer
		private readonly byte[] mBuff = new byte[REPORT_LENGTH];

		// current state of controller
		private readonly WiimoteState mWiimoteState = new WiimoteState();

		// data read fields
		private TaskCompletionSource<byte[]>? dataReadTaskCompletionSource;
		private byte[] dataReadResultBuffer;
		private short dataReadRequestedSize;
		private int dataReadAddress;

		private readonly AutoResetEvent mWriteDone = new AutoResetEvent(false);

		// event for status report
		private readonly AutoResetEvent mStatusDone = new AutoResetEvent(false);

		// use a different method to write reports
		private bool mAltWriteMethod;

		// HID device path of this Wiimote
		private string mDevicePath = string.Empty;

		// unique ID
		private readonly Guid mID = Guid.NewGuid();

		// delegate used for enumerating found Wiimotes
		internal delegate bool WiimoteFoundDelegate(string devicePath);

		// kilograms to pounds
		private const float KG2LB = 2.20462262f;

		private CancellationToken _cancellationToken = CancellationToken.None;

		/// <summary>
		/// Connect to the first-found Wiimote
		/// </summary>
		/// <exception cref="WiimoteNotFoundException">Wiimote not found in HID device list</exception>
		public void Connect(CancellationToken ct = default)
		{
			_cancellationToken = ct;

			_hidStream = _hidDevice.Open();
			_hidStream.ReadTimeout = Timeout.Infinite;
			_hidStream.WriteTimeout = 3_000;

			BeginAsyncRead();

			try
			{
				ReadWiimoteCalibration();
			}
			catch
			{
				throw; // TODO: Support the alternative write method?
				// if we fail above, try the alternate HID writes
				mAltWriteMethod = true;
				ReadWiimoteCalibration();
			}

			// force a status check to get the state of any extensions plugged in at startup
			GetStatus();
		}

		private static bool IsWiimote(HIDImports.HIDD_ATTRIBUTES attrib)
		{
			return attrib.VendorID == VID && (attrib.ProductID == PID || attrib.ProductID == PID_WITHMOTIONPLUS);
		}

		public static IEnumerable<HidDevice> FindWiimoteHidDevices()
		{
			return DeviceList.Local
				.GetHidDevices(vendorID: VID, productID: PID)
				.Concat(DeviceList.Local.GetHidDevices(vendorID: VID, productID: PID_WITHMOTIONPLUS));
		}

		/// <summary>
		/// Disconnect from the controller and stop reading data from it
		/// </summary>
		public void Disconnect()
		{
			if (mWiimoteState.MotionPlusState.Status is not MotionPlusStatus.None)
			{
				try
				{
					DeactivateMotionPlusAsync(CancellationToken.None).GetAwaiter().GetResult();
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Failed to deactivate MotionPlus when disposing Wiimote: {ex}");
				}
			}
			_hidStream?.Dispose();
			_hidStream = null;
		}

		/// <summary>
		/// Disconnect from the controller and stop reading data from it
		/// </summary>
		public async Task DisconnectAsync()
		{
			if (_hidStream is not null)
			{
				if (mWiimoteState.MotionPlusState.Status is not MotionPlusStatus.None)
				{
					try
					{
						// TODO: Handle the error that happens on extension initialization if this wasn't done for some reason
						await DeactivateMotionPlusAsync(CancellationToken.None);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Failed to deactivate MotionPlus when disposing Wiimote: {ex}");
					}
				}
				await _hidStream.DisposeAsync();
				_hidStream = null;
			}
		}

		/// <summary>
		/// Start reading asynchronously from the controller
		/// </summary>
		private void BeginAsyncRead()
		{
			if (_hidStream is null || !_hidStream.CanRead || _cancellationToken.IsCancellationRequested)
			{
				return;
			}

			var buff = new byte[REPORT_LENGTH];
			_hidStream.BeginRead(buff, offset: 0, count: REPORT_LENGTH, OnReadData, buff);
		}

		/// <summary>
		/// Callback when data is ready to be processed
		/// </summary>
		/// <param name="ar">State information for the callback</param>
		private void OnReadData(IAsyncResult ar)
		{
			if (ar.AsyncState is not byte[] buff)
			{
				throw new InvalidOperationException($"Expected data read callback to be of type byte[], but was {ar.AsyncState?.GetType().FullName ?? "null"}");
			}

			try
			{
				_hidStream?.EndRead(ar);

				if (ParseInputReport(buff))
				{
					WiimoteChanged?.Invoke(this, new WiimoteChangedEventArgs(mWiimoteState));
				}

				BeginAsyncRead();
			}
			catch (OperationCanceledException)
			{
				Debug.WriteLine("OperationCanceledException");
			}
		}

		/// <summary>
		/// Parse a report sent by the Wiimote
		/// </summary>
		/// <param name="buff">Data buffer to parse</param>
		/// <returns>Returns a boolean noting whether an event needs to be posted</returns>
		private bool ParseInputReport(byte[] buff)
		{
			InputReport type = (InputReport)buff[0];

			Console.WriteLine(string.Join(" ", buff.Select(b => b.ToString("X").PadLeft(2, '0'))));

			switch(type)
			{
				case InputReport.Buttons:
					ParseButtons(buff);
					break;
				case InputReport.ButtonsAccel:
					ParseButtons(buff);
					ParseAccel(buff);
					break;
				case InputReport.ButtonsWith8ExtensionBytes:
					ParseButtons(buff);
					ParseExtension(buff, 3);
					break;
				case InputReport.IRAccel:
					ParseButtons(buff);
					ParseAccel(buff);
					ParseIR(buff);
					break;
				case InputReport.ButtonsExtension:
					ParseButtons(buff);
					ParseExtension(buff, 3);
					break;
				case InputReport.ExtensionAccel:
					ParseButtons(buff);
					ParseAccel(buff);
					ParseExtension(buff, 6);
					break;
				case InputReport.IRExtensionAccel:
					ParseButtons(buff);
					ParseAccel(buff);
					ParseIR(buff);
					ParseExtension(buff, 16);
					break;
				case InputReport.Status:
					ParseButtons(buff);
					mWiimoteState.BatteryRaw = buff[6];
					mWiimoteState.Battery = (((100.0f * 48.0f * (float)((int)buff[6] / 48.0f))) / 192.0f);

					// get the real LED values in case the values from SetLEDs() somehow becomes out of sync, which really shouldn't be possible
					mWiimoteState.LEDState.LED1 = (buff[3] & 0x10) != 0;
					mWiimoteState.LEDState.LED2 = (buff[3] & 0x20) != 0;
					mWiimoteState.LEDState.LED3 = (buff[3] & 0x40) != 0;
					mWiimoteState.LEDState.LED4 = (buff[3] & 0x80) != 0;

					// extension connected?
					bool extension = (buff[3] & 0x02) != 0;
					Debug.WriteLine("Extension: " + extension);

					if (mWiimoteState.ExtensionConnected != extension)
					{
						mWiimoteState.ExtensionConnected = extension;

						if (extension)
						{
							BeginAsyncRead();
							InitializeExtensionAsync(_cancellationToken).GetAwaiter().GetResult(); // TODO: async
						}
						else
						{
							mWiimoteState.ExtensionType = ExtensionType.None;
						}

						// only fire the extension changed event if we have a real extension (i.e. not a balance board)
						if (WiimoteExtensionChanged != null &&
						    mWiimoteState.ExtensionType != ExtensionType.BalanceBoard)
						{
							WiimoteExtensionChanged(this, new WiimoteExtensionChangedEventArgs(mWiimoteState.ExtensionType, mWiimoteState.ExtensionConnected));
						}
					}
					mStatusDone.Set();
					break;
				case InputReport.ReadData:
					ParseButtons(buff);
					ParseReadData(buff);
					break;
				case InputReport.OutputReportAck:
					Debug.WriteLine("ack: " + buff[0] + " " +  buff[1] + " " +buff[2] + " " +buff[3] + " " +buff[4]);
					mWriteDone.Set();
					break;
				default:
					Debug.WriteLine("Unknown report type: " + type.ToString("x"));
					return false;
			}

			return true;
		}

		/// <summary>
		/// Handles setting up an extension when plugged in
		/// </summary>
		private async Task InitializeExtensionAsync(CancellationToken ct = default)
		{
			var extensionIdentifierBufferAtStart = await ReadDataAsync(REGISTER_EXTENSION_TYPE, 6, ct);

			Console.WriteLine(nameof(extensionIdentifierBufferAtStart) + " " + string.Join(' ', extensionIdentifierBufferAtStart.Select(b => b.ToString("X").PadLeft(2, '0'))));

			// TODO: Avoid this weird split initialization logic
			// TODO: Handle MotionPlus + other extension
			if (mWiimoteState.MotionPlusState.Status is
			    MotionPlusStatus.Activated or MotionPlusStatus.ActivationRequested)
			{
				var extensionTypeInner = await ReadExtensionTypeAsync(ct);
				if (extensionTypeInner is ExtensionType.MotionPlus)
				{
					mWiimoteState.ExtensionType = ExtensionType.MotionPlus;
				}
				return;
			}

			await WriteDataAsync(REGISTER_EXTENSION_INIT_1, 0x55, ct);
			await WriteDataAsync(REGISTER_EXTENSION_INIT_2, 0x00, ct);

			// start reading again
			// TODO: Is this necessary?
			BeginAsyncRead();

			var extensionType = await ReadExtensionTypeAsync(ct);
			switch(extensionType)
			{
				case ExtensionType.None:
				case ExtensionType.PartiallyInserted:
					mWiimoteState.ExtensionConnected = false;
					mWiimoteState.ExtensionType = ExtensionType.None;
					return;
				case ExtensionType.Nunchuk:
				case ExtensionType.ClassicController:
				case ExtensionType.Guitar:
				case ExtensionType.BalanceBoard:
				case ExtensionType.Drums:
					mWiimoteState.ExtensionType = extensionType;
					await SetReportTypeAsync(InputReport.ButtonsExtension, true, ct);
					break;
				default:
					throw new WiimoteException("Unknown extension controller found: " + extensionType.ToString("x"));
			}

			byte[] buff;
			switch(mWiimoteState.ExtensionType)
			{
				case ExtensionType.Nunchuk:
					buff = await ReadDataAsync(REGISTER_EXTENSION_CALIBRATION, 16, ct);

					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.X0 = buff[0];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Y0 = buff[1];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Z0 = buff[2];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.XG = buff[4];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.YG = buff[5];
					mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.ZG = buff[6];
					mWiimoteState.NunchukState.CalibrationInfo.MaxX = buff[8];
					mWiimoteState.NunchukState.CalibrationInfo.MinX = buff[9];
					mWiimoteState.NunchukState.CalibrationInfo.MidX = buff[10];
					mWiimoteState.NunchukState.CalibrationInfo.MaxY = buff[11];
					mWiimoteState.NunchukState.CalibrationInfo.MinY = buff[12];
					mWiimoteState.NunchukState.CalibrationInfo.MidY = buff[13];
					break;
				case ExtensionType.ClassicController:
					buff = await ReadDataAsync(REGISTER_EXTENSION_CALIBRATION, 16, ct);

					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXL = (byte)(buff[0] >> 2);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinXL = (byte)(buff[1] >> 2);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MidXL = (byte)(buff[2] >> 2);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYL = (byte)(buff[3] >> 2);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinYL = (byte)(buff[4] >> 2);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MidYL = (byte)(buff[5] >> 2);

					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXR = (byte)(buff[6] >> 3);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinXR = (byte)(buff[7] >> 3);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MidXR = (byte)(buff[8] >> 3);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYR = (byte)(buff[9] >> 3);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinYR = (byte)(buff[10] >> 3);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MidYR = (byte)(buff[11] >> 3);

					// this doesn't seem right...
//					mWiimoteState.ClassicControllerState.AccelCalibrationInfo.MinTriggerL = (byte)(buff[12] >> 3);
//					mWiimoteState.ClassicControllerState.AccelCalibrationInfo.MaxTriggerL = (byte)(buff[14] >> 3);
//					mWiimoteState.ClassicControllerState.AccelCalibrationInfo.MinTriggerR = (byte)(buff[13] >> 3);
//					mWiimoteState.ClassicControllerState.AccelCalibrationInfo.MaxTriggerR = (byte)(buff[15] >> 3);
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinTriggerL = 0;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerL = 31;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MinTriggerR = 0;
					mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerR = 31;
					break;
				case ExtensionType.Guitar:
				case ExtensionType.Drums:
					// there appears to be no calibration data returned by the guitar controller
					break;
				case ExtensionType.BalanceBoard:
					buff = await ReadDataAsync(REGISTER_EXTENSION_CALIBRATION, 32, ct);

					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.TopRight =		(short)((short)buff[4] << 8 | buff[5]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.BottomRight =	(short)((short)buff[6] << 8 | buff[7]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.TopLeft =		(short)((short)buff[8] << 8 | buff[9]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.BottomLeft =	(short)((short)buff[10] << 8 | buff[11]);

					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.TopRight =		(short)((short)buff[12] << 8 | buff[13]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.BottomRight =	(short)((short)buff[14] << 8 | buff[15]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.TopLeft =		(short)((short)buff[16] << 8 | buff[17]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.BottomLeft =	(short)((short)buff[18] << 8 | buff[19]);

					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.TopRight =		(short)((short)buff[20] << 8 | buff[21]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.BottomRight =	(short)((short)buff[22] << 8 | buff[23]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.TopLeft =		(short)((short)buff[24] << 8 | buff[25]);
					mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.BottomLeft =	(short)((short)buff[26] << 8 | buff[27]);
					break;
			}
		}

		private async Task<ExtensionType> ReadExtensionTypeAsync(CancellationToken ct)
		{
			var buff = await ReadDataAsync(REGISTER_EXTENSION_TYPE, 6, ct);
			return ParseExtensionType(buff);
		}

		private static ExtensionType ParseExtensionType(ReadOnlySpan<byte> buff)
		{
			long rawType = ((long)buff[0] << 40) | ((long)buff[1] << 32) |
			               ((long)buff[2]) << 24 | ((long)buff[3]) << 16 |
			               ((long)buff[4]) << 8 | buff[5];
			return (ExtensionType)rawType;
		}

		public Task ActivateMotionPlusAsync(CancellationToken ct)
			=> ActivateMotionPlusAsync(MotionPlusPassthroughMode.None, ct);

		public Task ActivateMotionPlusAsync(MotionPlusPassthroughMode mode, CancellationToken ct)
		{
			// TODO: Is this still allowed for changing passthrough mode, and should be a no-op otherwise?
			if (mWiimoteState.MotionPlusState.Status is not MotionPlusStatus.None)
			{
				throw new InvalidOperationException($"Cannot activate MotionPlus, status is already {mWiimoteState.MotionPlusState.Status}");
			}

			mWiimoteState.MotionPlusState.Status =
				mWiimoteState.ExtensionConnected || mWiimoteState.MotionPlusState.ExtensionConnected
					? MotionPlusStatus.Activated
					: MotionPlusStatus.ActivationRequested;
			return WriteDataAsync(address: 0x04a600fe, (byte)mode, ct);
		}

		public Task DeactivateMotionPlusAsync(CancellationToken ct)
		{
			mWiimoteState.MotionPlusState.Status = MotionPlusStatus.None; // TODO: This should be set later, from status report?
			return WriteDataAsync(address: 0x04a400f0, 0x55, ct);
		}

		/// <summary>
		/// Decrypts data sent from the extension to the Wiimote
		/// </summary>
		/// <param name="buff">Data buffer</param>
		/// <returns>Byte array containing decoded data</returns>
		private byte[] DecryptBuffer(byte[] buff)
		{
			for(int i = 0; i < buff.Length; i++)
				buff[i] = (byte)(((buff[i] ^ 0x17) + 0x17) & 0xff);

			return buff;
		}

		/// <summary>
		/// Parses a standard button report into the ButtonState struct
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseButtons(byte[] buff)
		{
			mWiimoteState.ButtonState.A		= (buff[2] & 0x08) != 0;
			mWiimoteState.ButtonState.B		= (buff[2] & 0x04) != 0;
			mWiimoteState.ButtonState.Minus	= (buff[2] & 0x10) != 0;
			mWiimoteState.ButtonState.Home	= (buff[2] & 0x80) != 0;
			mWiimoteState.ButtonState.Plus	= (buff[1] & 0x10) != 0;
			mWiimoteState.ButtonState.One	= (buff[2] & 0x02) != 0;
			mWiimoteState.ButtonState.Two	= (buff[2] & 0x01) != 0;
			mWiimoteState.ButtonState.Up	= (buff[1] & 0x08) != 0;
			mWiimoteState.ButtonState.Down	= (buff[1] & 0x04) != 0;
			mWiimoteState.ButtonState.Left	= (buff[1] & 0x01) != 0;
			mWiimoteState.ButtonState.Right	= (buff[1] & 0x02) != 0;
		}

		/// <summary>
		/// Parse accelerometer data
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseAccel(byte[] buff)
		{
			mWiimoteState.AccelState.RawValues.X = buff[3];
			mWiimoteState.AccelState.RawValues.Y = buff[4];
			mWiimoteState.AccelState.RawValues.Z = buff[5];

			mWiimoteState.AccelState.Values.X = (float)((float)mWiimoteState.AccelState.RawValues.X - ((int)mWiimoteState.AccelCalibrationInfo.X0)) /
											((float)mWiimoteState.AccelCalibrationInfo.XG - ((int)mWiimoteState.AccelCalibrationInfo.X0));
			mWiimoteState.AccelState.Values.Y = (float)((float)mWiimoteState.AccelState.RawValues.Y - mWiimoteState.AccelCalibrationInfo.Y0) /
											((float)mWiimoteState.AccelCalibrationInfo.YG - mWiimoteState.AccelCalibrationInfo.Y0);
			mWiimoteState.AccelState.Values.Z = (float)((float)mWiimoteState.AccelState.RawValues.Z - mWiimoteState.AccelCalibrationInfo.Z0) /
											((float)mWiimoteState.AccelCalibrationInfo.ZG - mWiimoteState.AccelCalibrationInfo.Z0);
		}

		/// <summary>
		/// Parse IR data from report
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseIR(byte[] buff)
		{
			mWiimoteState.IRState.IRSensors[0].RawPosition.X = buff[6] | ((buff[8] >> 4) & 0x03) << 8;
			mWiimoteState.IRState.IRSensors[0].RawPosition.Y = buff[7] | ((buff[8] >> 6) & 0x03) << 8;

			switch(mWiimoteState.IRState.Mode)
			{
				case IRMode.Basic:
					mWiimoteState.IRState.IRSensors[1].RawPosition.X = buff[9]  | ((buff[8] >> 0) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[1].RawPosition.Y = buff[10] | ((buff[8] >> 2) & 0x03) << 8;

					mWiimoteState.IRState.IRSensors[2].RawPosition.X = buff[11] | ((buff[13] >> 4) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[2].RawPosition.Y = buff[12] | ((buff[13] >> 6) & 0x03) << 8;

					mWiimoteState.IRState.IRSensors[3].RawPosition.X = buff[14] | ((buff[13] >> 0) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[3].RawPosition.Y = buff[15] | ((buff[13] >> 2) & 0x03) << 8;

					mWiimoteState.IRState.IRSensors[0].Size = 0x00;
					mWiimoteState.IRState.IRSensors[1].Size = 0x00;
					mWiimoteState.IRState.IRSensors[2].Size = 0x00;
					mWiimoteState.IRState.IRSensors[3].Size = 0x00;

					mWiimoteState.IRState.IRSensors[0].Found = !(buff[6] == 0xff && buff[7] == 0xff);
					mWiimoteState.IRState.IRSensors[1].Found = !(buff[9] == 0xff && buff[10] == 0xff);
					mWiimoteState.IRState.IRSensors[2].Found = !(buff[11] == 0xff && buff[12] == 0xff);
					mWiimoteState.IRState.IRSensors[3].Found = !(buff[14] == 0xff && buff[15] == 0xff);
					break;
				case IRMode.Extended:
					mWiimoteState.IRState.IRSensors[1].RawPosition.X = buff[9]  | ((buff[11] >> 4) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[1].RawPosition.Y = buff[10] | ((buff[11] >> 6) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[2].RawPosition.X = buff[12] | ((buff[14] >> 4) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[2].RawPosition.Y = buff[13] | ((buff[14] >> 6) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[3].RawPosition.X = buff[15] | ((buff[17] >> 4) & 0x03) << 8;
					mWiimoteState.IRState.IRSensors[3].RawPosition.Y = buff[16] | ((buff[17] >> 6) & 0x03) << 8;

					mWiimoteState.IRState.IRSensors[0].Size = buff[8] & 0x0f;
					mWiimoteState.IRState.IRSensors[1].Size = buff[11] & 0x0f;
					mWiimoteState.IRState.IRSensors[2].Size = buff[14] & 0x0f;
					mWiimoteState.IRState.IRSensors[3].Size = buff[17] & 0x0f;

					mWiimoteState.IRState.IRSensors[0].Found = !(buff[6] == 0xff && buff[7] == 0xff && buff[8] == 0xff);
					mWiimoteState.IRState.IRSensors[1].Found = !(buff[9] == 0xff && buff[10] == 0xff && buff[11] == 0xff);
					mWiimoteState.IRState.IRSensors[2].Found = !(buff[12] == 0xff && buff[13] == 0xff && buff[14] == 0xff);
					mWiimoteState.IRState.IRSensors[3].Found = !(buff[15] == 0xff && buff[16] == 0xff && buff[17] == 0xff);
					break;
			}

			mWiimoteState.IRState.IRSensors[0].Position.X = (float)(mWiimoteState.IRState.IRSensors[0].RawPosition.X / 1023.5f);
			mWiimoteState.IRState.IRSensors[1].Position.X = (float)(mWiimoteState.IRState.IRSensors[1].RawPosition.X / 1023.5f);
			mWiimoteState.IRState.IRSensors[2].Position.X = (float)(mWiimoteState.IRState.IRSensors[2].RawPosition.X / 1023.5f);
			mWiimoteState.IRState.IRSensors[3].Position.X = (float)(mWiimoteState.IRState.IRSensors[3].RawPosition.X / 1023.5f);

			mWiimoteState.IRState.IRSensors[0].Position.Y = (float)(mWiimoteState.IRState.IRSensors[0].RawPosition.Y / 767.5f);
			mWiimoteState.IRState.IRSensors[1].Position.Y = (float)(mWiimoteState.IRState.IRSensors[1].RawPosition.Y / 767.5f);
			mWiimoteState.IRState.IRSensors[2].Position.Y = (float)(mWiimoteState.IRState.IRSensors[2].RawPosition.Y / 767.5f);
			mWiimoteState.IRState.IRSensors[3].Position.Y = (float)(mWiimoteState.IRState.IRSensors[3].RawPosition.Y / 767.5f);

			if(mWiimoteState.IRState.IRSensors[0].Found && mWiimoteState.IRState.IRSensors[1].Found)
			{
				mWiimoteState.IRState.RawMidpoint.X = (mWiimoteState.IRState.IRSensors[1].RawPosition.X + mWiimoteState.IRState.IRSensors[0].RawPosition.X) / 2;
				mWiimoteState.IRState.RawMidpoint.Y = (mWiimoteState.IRState.IRSensors[1].RawPosition.Y + mWiimoteState.IRState.IRSensors[0].RawPosition.Y) / 2;

				mWiimoteState.IRState.Midpoint.X = (mWiimoteState.IRState.IRSensors[1].Position.X + mWiimoteState.IRState.IRSensors[0].Position.X) / 2.0f;
				mWiimoteState.IRState.Midpoint.Y = (mWiimoteState.IRState.IRSensors[1].Position.Y + mWiimoteState.IRState.IRSensors[0].Position.Y) / 2.0f;
			}
			else
				mWiimoteState.IRState.Midpoint.X = mWiimoteState.IRState.Midpoint.Y = 0.0f;
		}

		/// <summary>
		/// Parse data from an extension controller
		/// </summary>
		/// <param name="buff">Data buffer</param>
		/// <param name="offset">Offset into data buffer</param>
		private void ParseExtension(byte[] buff, int offset) => ParseExtension(buff.AsSpan(offset));

		/// <summary>
		/// Parse data from an extension controller
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseExtension(ReadOnlySpan<byte> buff)
		{
			if (mWiimoteState.MotionPlusState.Status == MotionPlusStatus.Activated)
			{
				if (buff.Length < 6)
				{
					Console.WriteLine("Buffer too short for MotionPlus");
					return;
				}

				var parsedMotionPlus = new
				{
					ParsedYawDown = ParseMotionPlusDegreeReport(speed1: buff[0], speed2: buff[3]),
					ParsedRollLeft = ParseMotionPlusDegreeReport(speed1: buff[1], speed2: buff[4]),
					ParsedPitchLeft = ParseMotionPlusDegreeReport(speed1: buff[2], speed2: buff[5]),
					// YawDownSpeed = buff[0].ToString("b8"),
					// YawDownSpeed2 = (buff[3] & 0b11111100).ToString("b8"),
					// RollLeftSpeed = buff[1].ToString("b8"),
					// RollLeftSpeed2 = (buff[4] & 0b11111100).ToString("b8"),
					// PitchLeftSpeed = buff[2].ToString("b8"),
					// PitchLeftSpeed2 = (buff[5] & 0b11111100).ToString("b8"),
					YawSlowMode = (buff[3] & 0b00000010) >> 1,
					PitchSlowMode = buff[3] & 0b00000001,
					RollSlowMode = (buff[4] & 0b00000010) >> 1,
					ExtensionConnectedRaw = buff[4] & 0b00000001,
					ExtensionConnected = (buff[4] & 0b00000001) == 1,
				};

				if (parsedMotionPlus.ExtensionConnected != mWiimoteState.MotionPlusState.ExtensionConnected)
				{
					mWiimoteState.MotionPlusState.ExtensionConnected = parsedMotionPlus.ExtensionConnected;
					InitializeExtensionAsync(_cancellationToken).GetAwaiter().GetResult(); // TODO: async
				}

				Console.WriteLine(parsedMotionPlus);

				return; // TODO: Handle interleaving with other extensions etc
			}

			switch(mWiimoteState.ExtensionType)
			{
				case ExtensionType.Nunchuk:
					mWiimoteState.NunchukState.RawJoystick.X = buff[0];
					mWiimoteState.NunchukState.RawJoystick.Y = buff[1];
					mWiimoteState.NunchukState.AccelState.RawValues.X = buff[2];
					mWiimoteState.NunchukState.AccelState.RawValues.Y = buff[3];
					mWiimoteState.NunchukState.AccelState.RawValues.Z = buff[4];

					mWiimoteState.NunchukState.C = (buff[5] & 0x02) == 0;
					mWiimoteState.NunchukState.Z = (buff[5] & 0x01) == 0;

					mWiimoteState.NunchukState.AccelState.Values.X = (float)((float)mWiimoteState.NunchukState.AccelState.RawValues.X - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.X0) /
													((float)mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.XG - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.X0);
					mWiimoteState.NunchukState.AccelState.Values.Y = (float)((float)mWiimoteState.NunchukState.AccelState.RawValues.Y - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Y0) /
													((float)mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.YG - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Y0);
					mWiimoteState.NunchukState.AccelState.Values.Z = (float)((float)mWiimoteState.NunchukState.AccelState.RawValues.Z - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Z0) /
													((float)mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.ZG - mWiimoteState.NunchukState.CalibrationInfo.AccelCalibration.Z0);

					if(mWiimoteState.NunchukState.CalibrationInfo.MaxX != 0x00)
						mWiimoteState.NunchukState.Joystick.X = (float)((float)mWiimoteState.NunchukState.RawJoystick.X - mWiimoteState.NunchukState.CalibrationInfo.MidX) /
												((float)mWiimoteState.NunchukState.CalibrationInfo.MaxX - mWiimoteState.NunchukState.CalibrationInfo.MinX);

					if(mWiimoteState.NunchukState.CalibrationInfo.MaxY != 0x00)
						mWiimoteState.NunchukState.Joystick.Y = (float)((float)mWiimoteState.NunchukState.RawJoystick.Y - mWiimoteState.NunchukState.CalibrationInfo.MidY) /
												((float)mWiimoteState.NunchukState.CalibrationInfo.MaxY - mWiimoteState.NunchukState.CalibrationInfo.MinY);

					break;

				case ExtensionType.ClassicController:
					mWiimoteState.ClassicControllerState.RawJoystickL.X = (byte)(buff[0] & 0x3f);
					mWiimoteState.ClassicControllerState.RawJoystickL.Y = (byte)(buff[1] & 0x3f);
					mWiimoteState.ClassicControllerState.RawJoystickR.X = (byte)((buff[2] >> 7) | (buff[1] & 0xc0) >> 5 | (buff[0] & 0xc0) >> 3);
					mWiimoteState.ClassicControllerState.RawJoystickR.Y = (byte)(buff[2] & 0x1f);

					mWiimoteState.ClassicControllerState.RawTriggerL = (byte)(((buff[2] & 0x60) >> 2) | (buff[3] >> 5));
					mWiimoteState.ClassicControllerState.RawTriggerR = (byte)(buff[3] & 0x1f);

					mWiimoteState.ClassicControllerState.ButtonState.TriggerR	= (buff[4] & 0x02) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Plus		= (buff[4] & 0x04) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Home		= (buff[4] & 0x08) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Minus		= (buff[4] & 0x10) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.TriggerL	= (buff[4] & 0x20) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Down		= (buff[4] & 0x40) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Right		= (buff[4] & 0x80) == 0;

					mWiimoteState.ClassicControllerState.ButtonState.Up			= (buff[5] & 0x01) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Left		= (buff[5] & 0x02) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.ZR			= (buff[5] & 0x04) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.X			= (buff[5] & 0x08) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.A			= (buff[5] & 0x10) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.Y			= (buff[5] & 0x20) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.B			= (buff[5] & 0x40) == 0;
					mWiimoteState.ClassicControllerState.ButtonState.ZL			= (buff[5] & 0x80) == 0;

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXL != 0x00)
						mWiimoteState.ClassicControllerState.JoystickL.X = (float)((float)mWiimoteState.ClassicControllerState.RawJoystickL.X - mWiimoteState.ClassicControllerState.CalibrationInfo.MidXL) /
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXL - mWiimoteState.ClassicControllerState.CalibrationInfo.MinXL);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYL != 0x00)
						mWiimoteState.ClassicControllerState.JoystickL.Y = (float)((float)mWiimoteState.ClassicControllerState.RawJoystickL.Y - mWiimoteState.ClassicControllerState.CalibrationInfo.MidYL) /
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYL - mWiimoteState.ClassicControllerState.CalibrationInfo.MinYL);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXR != 0x00)
						mWiimoteState.ClassicControllerState.JoystickR.X = (float)((float)mWiimoteState.ClassicControllerState.RawJoystickR.X - mWiimoteState.ClassicControllerState.CalibrationInfo.MidXR) /
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxXR - mWiimoteState.ClassicControllerState.CalibrationInfo.MinXR);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYR != 0x00)
						mWiimoteState.ClassicControllerState.JoystickR.Y = (float)((float)mWiimoteState.ClassicControllerState.RawJoystickR.Y - mWiimoteState.ClassicControllerState.CalibrationInfo.MidYR) /
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxYR - mWiimoteState.ClassicControllerState.CalibrationInfo.MinYR);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerL != 0x00)
						mWiimoteState.ClassicControllerState.TriggerL = (mWiimoteState.ClassicControllerState.RawTriggerL) /
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerL - mWiimoteState.ClassicControllerState.CalibrationInfo.MinTriggerL);

					if(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerR != 0x00)
						mWiimoteState.ClassicControllerState.TriggerR = (mWiimoteState.ClassicControllerState.RawTriggerR) /
						(float)(mWiimoteState.ClassicControllerState.CalibrationInfo.MaxTriggerR - mWiimoteState.ClassicControllerState.CalibrationInfo.MinTriggerR);
					break;

				case ExtensionType.Guitar:
					mWiimoteState.GuitarState.GuitarType = ((buff[0] & 0x80) == 0) ? GuitarType.GuitarHeroWorldTour : GuitarType.GuitarHero3;

					mWiimoteState.GuitarState.ButtonState.Plus		= (buff[4] & 0x04) == 0;
					mWiimoteState.GuitarState.ButtonState.Minus		= (buff[4] & 0x10) == 0;
					mWiimoteState.GuitarState.ButtonState.StrumDown	= (buff[4] & 0x40) == 0;

					mWiimoteState.GuitarState.ButtonState.StrumUp		= (buff[5] & 0x01) == 0;
					mWiimoteState.GuitarState.FretButtonState.Yellow	= (buff[5] & 0x08) == 0;
					mWiimoteState.GuitarState.FretButtonState.Green		= (buff[5] & 0x10) == 0;
					mWiimoteState.GuitarState.FretButtonState.Blue		= (buff[5] & 0x20) == 0;
					mWiimoteState.GuitarState.FretButtonState.Red		= (buff[5] & 0x40) == 0;
					mWiimoteState.GuitarState.FretButtonState.Orange	= (buff[5] & 0x80) == 0;

					// it appears the joystick values are only 6 bits
					mWiimoteState.GuitarState.RawJoystick.X	= (buff[0] & 0x3f);
					mWiimoteState.GuitarState.RawJoystick.Y	= (buff[1] & 0x3f);

					// and the whammy bar is only 5 bits
					mWiimoteState.GuitarState.RawWhammyBar			= (byte)(buff[3] & 0x1f);

					mWiimoteState.GuitarState.Joystick.X			= (float)(mWiimoteState.GuitarState.RawJoystick.X - 0x1f) / 0x3f;	// not fully accurate, but close
					mWiimoteState.GuitarState.Joystick.Y			= (float)(mWiimoteState.GuitarState.RawJoystick.Y - 0x1f) / 0x3f;	// not fully accurate, but close
					mWiimoteState.GuitarState.WhammyBar				= (float)(mWiimoteState.GuitarState.RawWhammyBar) / 0x0a;	// seems like there are 10 positions?

					mWiimoteState.GuitarState.TouchbarState.Yellow	= false;
					mWiimoteState.GuitarState.TouchbarState.Green	= false;
					mWiimoteState.GuitarState.TouchbarState.Blue	= false;
					mWiimoteState.GuitarState.TouchbarState.Red		= false;
					mWiimoteState.GuitarState.TouchbarState.Orange	= false;

					switch(buff[2] & 0x1f)
					{
						case 0x04:
							mWiimoteState.GuitarState.TouchbarState.Green = true;
							break;
						case 0x07:
							mWiimoteState.GuitarState.TouchbarState.Green = true;
							mWiimoteState.GuitarState.TouchbarState.Red = true;
							break;
						case 0x0a:
							mWiimoteState.GuitarState.TouchbarState.Red = true;
							break;
						case 0x0c:
						case 0x0d:
							mWiimoteState.GuitarState.TouchbarState.Red = true;
							mWiimoteState.GuitarState.TouchbarState.Yellow = true;
							break;
						case 0x12:
						case 0x13:
							mWiimoteState.GuitarState.TouchbarState.Yellow = true;
							break;
						case 0x14:
						case 0x15:
							mWiimoteState.GuitarState.TouchbarState.Yellow = true;
							mWiimoteState.GuitarState.TouchbarState.Blue = true;
							break;
						case 0x17:
						case 0x18:
							mWiimoteState.GuitarState.TouchbarState.Blue = true;
							break;
						case 0x1a:
							mWiimoteState.GuitarState.TouchbarState.Blue = true;
							mWiimoteState.GuitarState.TouchbarState.Orange = true;
							break;
						case 0x1f:
							mWiimoteState.GuitarState.TouchbarState.Orange = true;
							break;
					}
					break;

				case ExtensionType.Drums:
					// it appears the joystick values are only 6 bits
					mWiimoteState.DrumsState.RawJoystick.X	= (buff[0] & 0x3f);
					mWiimoteState.DrumsState.RawJoystick.Y	= (buff[1] & 0x3f);

					mWiimoteState.DrumsState.Plus			= (buff[4] & 0x04) == 0;
					mWiimoteState.DrumsState.Minus			= (buff[4] & 0x10) == 0;

					mWiimoteState.DrumsState.Pedal			= (buff[5] & 0x04) == 0;
					mWiimoteState.DrumsState.Blue			= (buff[5] & 0x08) == 0;
					mWiimoteState.DrumsState.Green			= (buff[5] & 0x10) == 0;
					mWiimoteState.DrumsState.Yellow			= (buff[5] & 0x20) == 0;
					mWiimoteState.DrumsState.Red			= (buff[5] & 0x40) == 0;
					mWiimoteState.DrumsState.Orange			= (buff[5] & 0x80) == 0;

					mWiimoteState.DrumsState.Joystick.X		= (float)(mWiimoteState.DrumsState.RawJoystick.X - 0x1f) / 0x3f;	// not fully accurate, but close
					mWiimoteState.DrumsState.Joystick.Y		= (float)(mWiimoteState.DrumsState.RawJoystick.Y - 0x1f) / 0x3f;	// not fully accurate, but close

					if((buff[2] & 0x40) == 0)
					{
						int pad = (buff[2] >> 1) & 0x1f;
						int velocity = (buff[3] >> 5);

						if(velocity != 7)
						{
							switch(pad)
							{
								case 0x1b:
									mWiimoteState.DrumsState.PedalVelocity = velocity;
									break;
								case 0x19:
									mWiimoteState.DrumsState.RedVelocity = velocity;
									break;
								case 0x11:
									mWiimoteState.DrumsState.YellowVelocity = velocity;
									break;
								case 0x0f:
									mWiimoteState.DrumsState.BlueVelocity = velocity;
									break;
								case 0x0e:
									mWiimoteState.DrumsState.OrangeVelocity = velocity;
									break;
								case 0x12:
									mWiimoteState.DrumsState.GreenVelocity = velocity;
									break;
							}
						}
					}

					break;

				case ExtensionType.BalanceBoard:
					mWiimoteState.BalanceBoardState.SensorValuesRaw.TopRight = (short)((short)buff[0] << 8 | buff[1]);
					mWiimoteState.BalanceBoardState.SensorValuesRaw.BottomRight = (short)((short)buff[2] << 8 | buff[3]);
					mWiimoteState.BalanceBoardState.SensorValuesRaw.TopLeft = (short)((short)buff[4] << 8 | buff[5]);
					mWiimoteState.BalanceBoardState.SensorValuesRaw.BottomLeft = (short)((short)buff[6] << 8 | buff[7]);

					mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft = GetBalanceBoardSensorValue(mWiimoteState.BalanceBoardState.SensorValuesRaw.TopLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.TopLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.TopLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.TopLeft);
					mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight = GetBalanceBoardSensorValue(mWiimoteState.BalanceBoardState.SensorValuesRaw.TopRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.TopRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.TopRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.TopRight);
					mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft = GetBalanceBoardSensorValue(mWiimoteState.BalanceBoardState.SensorValuesRaw.BottomLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.BottomLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.BottomLeft, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.BottomLeft);
					mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight = GetBalanceBoardSensorValue(mWiimoteState.BalanceBoardState.SensorValuesRaw.BottomRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg0.BottomRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg17.BottomRight, mWiimoteState.BalanceBoardState.CalibrationInfo.Kg34.BottomRight);

					mWiimoteState.BalanceBoardState.SensorValuesLb.TopLeft = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft * KG2LB);
					mWiimoteState.BalanceBoardState.SensorValuesLb.TopRight = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight * KG2LB);
					mWiimoteState.BalanceBoardState.SensorValuesLb.BottomLeft = (mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft * KG2LB);
					mWiimoteState.BalanceBoardState.SensorValuesLb.BottomRight = (mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight * KG2LB);

					mWiimoteState.BalanceBoardState.WeightKg = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight) / 4.0f;
					mWiimoteState.BalanceBoardState.WeightLb = (mWiimoteState.BalanceBoardState.SensorValuesLb.TopLeft + mWiimoteState.BalanceBoardState.SensorValuesLb.TopRight + mWiimoteState.BalanceBoardState.SensorValuesLb.BottomLeft + mWiimoteState.BalanceBoardState.SensorValuesLb.BottomRight) / 4.0f;

					float Kx = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft) / (mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight);
					float Ky = (mWiimoteState.BalanceBoardState.SensorValuesKg.TopLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.TopRight) / (mWiimoteState.BalanceBoardState.SensorValuesKg.BottomLeft + mWiimoteState.BalanceBoardState.SensorValuesKg.BottomRight);

					mWiimoteState.BalanceBoardState.CenterOfGravity.X = ((float)(Kx - 1) / (float)(Kx + 1)) * (float)(-BSL / 2);
					mWiimoteState.BalanceBoardState.CenterOfGravity.Y = ((float)(Ky - 1) / (float)(Ky + 1)) * (float)(-BSW / 2);
					break;
			}
		}

		private float GetBalanceBoardSensorValue(short sensor, short min, short mid, short max)
		{
			if(max == mid || mid == min)
				return 0;

			if(sensor < mid)
				return 68.0f * ((float)(sensor - min) / (mid - min));
			else
				return 68.0f * ((float)(sensor - mid) / (max - mid)) + 68.0f;
		}

		private static Int16 ParseMotionPlusDegreeReport(byte speed1, byte speed2)
		{
			var rawSpeed = (Int16)((speed2 & 0b11111100) << 6 | speed1);
			return rawSpeed;
		}

		/// <summary>
		/// Parse data returned from a read report
		/// </summary>
		/// <param name="buff">Data buffer</param>
		private void ParseReadData(byte[] buff)
		{
			if (dataReadTaskCompletionSource is null ||
			    dataReadTaskCompletionSource.Task.IsCanceled ||
			    dataReadTaskCompletionSource.Task.IsFaulted ||
			    dataReadTaskCompletionSource.Task.IsCompleted)
			{
				return;
			}

			var errorFlag = buff[3] & 0b1111;

			switch (errorFlag)
			{
				case 0:
					break;
				case 8:
					dataReadTaskCompletionSource.SetException(new WiimoteReadDataException(
						DataReadErrorType.NonExistentMemoryAddress,
						"Error reading data from Wiimote: Bytes do not exist.",
						failureReasonBuffer: buff));
					return;
				case 7:
					dataReadTaskCompletionSource.SetException(new WiimoteReadDataException(
						DataReadErrorType.ReadFromWriteOnlyRegister,
						"Error reading data from Wiimote: Attempt to read from a write-only register or an extension that is not connected.",
						failureReasonBuffer: buff));
					return;
				default:
					dataReadTaskCompletionSource.SetException(new WiimoteReadDataException(
						(DataReadErrorType)errorFlag,
						"Error reading data from Wiimote: Unknown error.",
						failureReasonBuffer: buff));
					return;
			}

			// get our size and offset from the report
			int size = (buff[3] >> 4) + 1;
			int offset = (buff[4] << 8 | buff[5]);

			// add it to the buffer
			Array.Copy(buff, 6, dataReadResultBuffer, offset - dataReadAddress, size);

			// if we've read it all, set the event
			if (dataReadAddress + dataReadRequestedSize == offset + size)
			{
				dataReadTaskCompletionSource.SetResult(dataReadResultBuffer);
			}
		}

		/// <summary>
		/// Returns whether rumble is currently enabled.
		/// </summary>
		/// <returns>Byte indicating true (0x01) or false (0x00)</returns>
		private byte GetRumbleBit()
		{
			return (byte)(mWiimoteState.Rumble ? 0x01 : 0x00);
		}

		/// <summary>
		/// Read calibration information stored on Wiimote
		/// </summary>
		private void ReadWiimoteCalibration()
		{
			// this appears to change the report type to 0x31
			byte[] buff = ReadData(0x0016, 7);

			mWiimoteState.AccelCalibrationInfo.X0 = buff[0];
			mWiimoteState.AccelCalibrationInfo.Y0 = buff[1];
			mWiimoteState.AccelCalibrationInfo.Z0 = buff[2];
			mWiimoteState.AccelCalibrationInfo.XG = buff[4];
			mWiimoteState.AccelCalibrationInfo.YG = buff[5];
			mWiimoteState.AccelCalibrationInfo.ZG = buff[6];
		}

		/// <summary>
		/// Set Wiimote reporting mode (if using an IR report type, IR sensitivity is set to WiiLevel3)
		/// </summary>
		/// <param name="type">Report type</param>
		/// <param name="continuous">Continuous data</param>
		public void SetReportType(InputReport type, bool continuous)
		{
			SetReportType(type, IRSensitivity.Maximum, continuous);
		}

		/// <summary>
		/// Set Wiimote reporting mode (if using an IR report type, IR sensitivity is set to WiiLevel3)
		/// </summary>
		/// <param name="type">Report type</param>
		/// <param name="continuous">Continuous data</param>
		/// <param name="ct"></param>
		public Task SetReportTypeAsync(InputReport type, bool continuous, CancellationToken ct)
			=> SetReportTypeAsync(type, IRSensitivity.Maximum, continuous, ct);

		/// <summary>
		/// Set Wiimote reporting mode
		/// </summary>
		/// <param name="type">Report type</param>
		/// <param name="irSensitivity">IR sensitivity</param>
		/// <param name="continuous">Continuous data</param>
		public void SetReportType(InputReport type, IRSensitivity irSensitivity, bool continuous)
		{
			PrepareSetReportType(type, irSensitivity, continuous);

			WriteReport();
		}

		/// <summary>
		/// Set Wiimote reporting mode
		/// </summary>
		/// <param name="type">Report type</param>
		/// <param name="irSensitivity">IR sensitivity</param>
		/// <param name="continuous">Continuous data</param>
		/// <param name="ct"></param>
		public Task SetReportTypeAsync(InputReport type, IRSensitivity irSensitivity, bool continuous, CancellationToken ct)
		{
			PrepareSetReportType(type, irSensitivity, continuous);

			return WriteReportAsync(ct);
		}

		private void PrepareSetReportType(InputReport type, IRSensitivity irSensitivity, bool continuous)
		{
			// only 1 report type allowed for the BB
			if(mWiimoteState.ExtensionType == ExtensionType.BalanceBoard)
				type = InputReport.ButtonsExtension;

			switch(type)
			{
				case InputReport.IRAccel:
					EnableIR(IRMode.Extended, irSensitivity);
					break;
				case InputReport.IRExtensionAccel:
					EnableIR(IRMode.Basic, irSensitivity);
					break;
				default:
					DisableIR();
					break;
			}

			ClearReport();
			mBuff[0] = (byte)OutputReport.Type;
			mBuff[1] = (byte)((continuous ? 0x04 : 0x00) | (byte)(mWiimoteState.Rumble ? 0x01 : 0x00));
			mBuff[2] = (byte)type;
		}

		/// <summary>
		/// Set the LEDs on the Wiimote
		/// </summary>
		/// <param name="led1">LED 1</param>
		/// <param name="led2">LED 2</param>
		/// <param name="led3">LED 3</param>
		/// <param name="led4">LED 4</param>
		public void SetLEDs(bool led1, bool led2, bool led3, bool led4)
		{
			mWiimoteState.LEDState.LED1 = led1;
			mWiimoteState.LEDState.LED2 = led2;
			mWiimoteState.LEDState.LED3 = led3;
			mWiimoteState.LEDState.LED4 = led4;

			ClearReport();

			mBuff[0] = (byte)OutputReport.LEDs;
			mBuff[1] =	(byte)(
						(led1 ? 0x10 : 0x00) |
						(led2 ? 0x20 : 0x00) |
						(led3 ? 0x40 : 0x00) |
						(led4 ? 0x80 : 0x00) |
						GetRumbleBit());

			WriteReport();
		}

		/// <summary>
		/// Set the LEDs on the Wiimote
		/// </summary>
		/// <param name="leds">The value to be lit up in base2 on the Wiimote</param>
		public void SetLEDs(int leds)
		{
			mWiimoteState.LEDState.LED1 = (leds & 0x01) > 0;
			mWiimoteState.LEDState.LED2 = (leds & 0x02) > 0;
			mWiimoteState.LEDState.LED3 = (leds & 0x04) > 0;
			mWiimoteState.LEDState.LED4 = (leds & 0x08) > 0;

			ClearReport();

			mBuff[0] = (byte)OutputReport.LEDs;
			mBuff[1] =	(byte)(
						((leds & 0x01) > 0 ? 0x10 : 0x00) |
						((leds & 0x02) > 0 ? 0x20 : 0x00) |
						((leds & 0x04) > 0 ? 0x40 : 0x00) |
						((leds & 0x08) > 0 ? 0x80 : 0x00) |
						GetRumbleBit());

			WriteReport();
		}

		/// <summary>
		/// Toggle rumble
		/// </summary>
		/// <param name="on">On or off</param>
		public void SetRumble(bool on)
		{
			mWiimoteState.Rumble = on;

			// the LED report also handles rumble
			SetLEDs(mWiimoteState.LEDState.LED1,
					mWiimoteState.LEDState.LED2,
					mWiimoteState.LEDState.LED3,
					mWiimoteState.LEDState.LED4);
		}

		/// <summary>
		/// Retrieve the current status of the Wiimote and extensions.  Replaces GetBatteryLevel() since it was poorly named.
		/// </summary>
		public void GetStatus()
		{
			ClearReport();

			mBuff[0] = (byte)OutputReport.Status;
			mBuff[1] = GetRumbleBit();

			WriteReport();

			// signal the status report finished
			if(!mStatusDone.WaitOne(3000, false))
				throw new WiimoteException("Timed out waiting for status report");
		}

		/// <summary>
		/// Turn on the IR sensor
		/// </summary>
		/// <param name="mode">The data report mode</param>
		/// <param name="irSensitivity">IR sensitivity</param>
		private void EnableIR(IRMode mode, IRSensitivity irSensitivity)
		{
			mWiimoteState.IRState.Mode = mode;

			ClearReport();
			mBuff[0] = (byte)OutputReport.IR;
			mBuff[1] = (byte)(0x04 | GetRumbleBit());
			WriteReport();

			ClearReport();
			mBuff[0] = (byte)OutputReport.IR2;
			mBuff[1] = (byte)(0x04 | GetRumbleBit());
			WriteReport();

			WriteData(REGISTER_IR, 0x08);
			switch(irSensitivity)
			{
				case IRSensitivity.WiiLevel1:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0x64, 0x00, 0xfe});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0xfd, 0x05});
					break;
				case IRSensitivity.WiiLevel2:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0x96, 0x00, 0xb4});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0xb3, 0x04});
					break;
				case IRSensitivity.WiiLevel3:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0xaa, 0x00, 0x64});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0x63, 0x03});
					break;
				case IRSensitivity.WiiLevel4:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0xc8, 0x00, 0x36});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0x35, 0x03});
					break;
				case IRSensitivity.WiiLevel5:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x07, 0x00, 0x00, 0x71, 0x01, 0x00, 0x72, 0x00, 0x20});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0x1, 0x03});
					break;
				case IRSensitivity.Maximum:
					WriteData(REGISTER_IR_SENSITIVITY_1, 9, new byte[] {0x02, 0x00, 0x00, 0x71, 0x01, 0x00, 0x90, 0x00, 0x41});
					WriteData(REGISTER_IR_SENSITIVITY_2, 2, new byte[] {0x40, 0x00});
					break;
				default:
					throw new ArgumentOutOfRangeException("irSensitivity");
			}
			WriteData(REGISTER_IR_MODE, (byte)mode);
			WriteData(REGISTER_IR, 0x08);
		}

		/// <summary>
		/// Disable the IR sensor
		/// </summary>
		private void DisableIR()
		{
			mWiimoteState.IRState.Mode = IRMode.Off;

			ClearReport();
			mBuff[0] = (byte)OutputReport.IR;
			mBuff[1] = GetRumbleBit();
			WriteReport();

			ClearReport();
			mBuff[0] = (byte)OutputReport.IR2;
			mBuff[1] = GetRumbleBit();
			WriteReport();
		}

		/// <summary>
		/// Initialize the report data buffer
		/// </summary>
		private void ClearReport()
		{
			Array.Clear(mBuff, 0, REPORT_LENGTH);
		}

		/// <summary>
		/// Write a report to the Wiimote
		/// </summary>
		/// <param name="ct"></param>
		private async Task WriteReportAsync(CancellationToken ct)
		{
			Debug.WriteLine("WriteReport: " + mBuff[0].ToString("x"));
			if (mAltWriteMethod)
			{
				throw new NotImplementedException("Alternative fallback write method not implemented");
				// HIDImports.HidD_SetOutputReport(this.mHandle.DangerousGetHandle(), mBuff, (uint)mBuff.Length);
			}
			else
			{
				await HidStream.WriteAsync(mBuff, ct);
			}

			if(mBuff[0] == (byte)OutputReport.WriteMemory)
			{
				Debug.WriteLine("Wait");
				if(!mWriteDone.WaitOne(1000, false))
					Debug.WriteLine("Wait failed");
				//throw new WiimoteException("Error writing data to Wiimote...is it connected?");
			}
		}

		/// <summary>
		/// Write a report to the Wiimote
		/// </summary>
		private void WriteReport()
		{
			// TODO: Replace usages of this method with WriteReportAsync
			Debug.WriteLine("WriteReport: " + mBuff[0].ToString("x"));
			if (mAltWriteMethod)
			{
				throw new NotImplementedException("Alternative fallback write method not implemented");
				// HIDImports.HidD_SetOutputReport(this.mHandle.DangerousGetHandle(), mBuff, (uint)mBuff.Length);
			}
			else
			{
				HidStream.Write(mBuff);
			}

			if(mBuff[0] == (byte)OutputReport.WriteMemory)
			{
				Debug.WriteLine("Wait");
				if(!mWriteDone.WaitOne(1000, false))
					Debug.WriteLine("Wait failed");
				//throw new WiimoteException("Error writing data to Wiimote...is it connected?");
			}
		}

		/// <summary>
		/// Read data or register from Wiimote
		/// </summary>
		/// <param name="address">Address to read</param>
		/// <param name="size">Length to read</param>
		/// <param name="ct">Cancellation token</param>
		/// <exception cref="InvalidOperationException">Another read data operation is already ongoing.</exception>
		/// <exception cref="WiimoteReadDataException">Data read failed.</exception>
		/// <returns>Data buffer</returns>
		public byte[] ReadData(int address, short size, CancellationToken ct = default)
			=> ReadDataAsync(address, size, ct).GetAwaiter().GetResult();

		/// <summary>
		/// Read data or register from Wiimote
		/// </summary>
		/// <param name="address">Address to read</param>
		/// <param name="size">Length to read</param>
		/// <param name="ct">Cancellation token</param>
		/// <exception cref="InvalidOperationException">Another read data operation is already ongoing.</exception>
		/// <exception cref="WiimoteReadDataException">Data read failed.</exception>
		/// <returns>Data buffer</returns>
		public async Task<byte[]> ReadDataAsync(int address, short size, CancellationToken ct = default)
		{
			var previousCompletionSource = Interlocked.CompareExchange(ref dataReadTaskCompletionSource, new(), null);
			if (previousCompletionSource is not null)
			{
				throw new InvalidOperationException("Can't read data while a previous data read is ongoing");
			}

			ClearReport();

			dataReadResultBuffer = new byte[size];
			dataReadAddress = address & 0xffff;
			dataReadRequestedSize = size;

			mBuff[0] = (byte)OutputReport.ReadMemory;
			mBuff[1] = (byte)(((address & 0xff000000) >> 24) | GetRumbleBit());
			mBuff[2] = (byte)((address & 0x00ff0000)  >> 16);
			mBuff[3] = (byte)((address & 0x0000ff00)  >>  8);
			mBuff[4] = (byte)(address & 0x000000ff);

			mBuff[5] = (byte)((size & 0xff00) >> 8);
			mBuff[6] = (byte)(size & 0xff);

			await WriteReportAsync(ct);

			try
			{
				return await dataReadTaskCompletionSource.Task;
			}
			finally
			{
				dataReadTaskCompletionSource = null;
			}
		}

		/// <summary>
		/// Write a single byte to the Wiimote
		/// </summary>
		/// <param name="address">Address to write</param>
		/// <param name="data">Byte to write</param>
		public void WriteData(int address, byte data)
		{
			WriteData(address, 1, [data]);
		}

		public Task WriteDataAsync(int address, byte data, CancellationToken ct)
			=> WriteDataAsync(address, 1, [data], ct);

		/// <summary>
		/// Write a byte array to a specified address
		/// </summary>
		/// <param name="address">Address to write</param>
		/// <param name="size">Length of buffer</param>
		/// <param name="buff">Data buffer</param>
		public void WriteData(int address, byte size, ReadOnlySpan<byte> buff)
		{
			ClearReport();

			mBuff[0] = (byte)OutputReport.WriteMemory;
			mBuff[1] = (byte)(((address & 0xff000000) >> 24) | GetRumbleBit());
			mBuff[2] = (byte)((address & 0x00ff0000)  >> 16);
			mBuff[3] = (byte)((address & 0x0000ff00)  >>  8);
			mBuff[4] = (byte)(address & 0x000000ff);
			mBuff[5] = size;
			buff.CopyTo(mBuff.AsSpan(6..(6 + size)));

			WriteReport();
		}

		public Task WriteDataAsync(int address, byte size, ReadOnlySpan<byte> buff, CancellationToken ct)
		{
			ClearReport();

			mBuff[0] = (byte)OutputReport.WriteMemory;
			mBuff[1] = (byte)(((address & 0xff000000) >> 24) | GetRumbleBit());
			mBuff[2] = (byte)((address & 0x00ff0000)  >> 16);
			mBuff[3] = (byte)((address & 0x0000ff00)  >>  8);
			mBuff[4] = (byte)(address & 0x000000ff);
			mBuff[5] = size;
			buff.CopyTo(mBuff.AsSpan(6..(6 + size)));

			return WriteReportAsync(ct);
		}

		/// <summary>
		/// Current Wiimote state
		/// </summary>
		public WiimoteState WiimoteState => mWiimoteState;

		///<summary>
		/// Unique identifier for this Wiimote (not persisted across application instances)
		///</summary>
		public Guid ID => mID;

		/// <summary>
		/// HID device path for this Wiimote (valid until Wiimote is disconnected)
		/// </summary>
		public string HIDDevicePath => mDevicePath;

		#region IDisposable and IAsyncDisposable Members

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				Disconnect();
			}
		}

		public async ValueTask DisposeAsync()
		{
			await DisposeAsync(true);
			GC.SuppressFinalize(this);
		}

		public async ValueTask DisposeAsync(bool disposing)
		{
			if (disposing)
			{
				await DisconnectAsync();
			}
		}

		#endregion
	}

	/// <summary>
	/// Thrown when no Wiimotes are found in the HID device list
	/// </summary>
	public class WiimoteNotFoundException : ApplicationException
	{
		/// <summary>
		/// Default constructor
		/// </summary>
		public WiimoteNotFoundException()
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		public WiimoteNotFoundException(string message) : base(message)
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		/// <param name="innerException">Inner exception</param>
		public WiimoteNotFoundException(string message, Exception innerException) : base(message, innerException)
		{
		}
	}

	/// <summary>
	/// Represents errors that occur during the execution of the Wiimote library
	/// </summary>
	public class WiimoteException : ApplicationException
	{
		/// <summary>
		/// Default constructor
		/// </summary>
		public WiimoteException()
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		public WiimoteException(string message) : base(message)
		{
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Error message</param>
		/// <param name="innerException">Inner exception</param>
		public WiimoteException(string message, Exception innerException) : base(message, innerException)
		{
		}
	}
}