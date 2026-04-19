using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Spark.Controllers
{
	// ──────────────────────────────────────────────────────────────────────────
	// Win32 P/Invoke helpers
	// ──────────────────────────────────────────────────────────────────────────
	internal static class MediaNativeMethods
	{
		// OpenProcess — needed to get a handle with PROCESS_VM_READ rights
		[DllImport("kernel32.dll", SetLastError = true)]
		internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

		// CloseHandle — must close handles we open ourselves
		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CloseHandle(IntPtr hObject);

		// ReadProcessMemory — replaces pymem.read_uchar
		[DllImport("kernel32.dll", SetLastError = true)]
		internal static extern bool ReadProcessMemory(
			IntPtr hProcess,
			IntPtr lpBaseAddress,
			[Out] byte[] lpBuffer,
			int dwSize,
			out int lpNumberOfBytesRead);

		internal const uint PROCESS_VM_READ           = 0x0010;
		internal const uint PROCESS_QUERY_INFORMATION = 0x0400;

		// SendInput — replaces win32api.keybd_event
		[DllImport("user32.dll")]
		internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

		[StructLayout(LayoutKind.Sequential)]
		internal struct INPUT
		{
			public uint type;          // 1 = INPUT_KEYBOARD
			public KEYBDINPUT ki;
			public long padding;       // union padding
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct KEYBDINPUT
		{
			public ushort wVk;
			public ushort wScan;
			public uint dwFlags;       // 0 = keydown, 2 = KEYEVENTF_KEYUP
			public uint time;
			public UIntPtr dwExtraInfo;
		}

		internal const uint INPUT_KEYBOARD = 1;
		internal const uint KEYEVENTF_KEYUP = 0x0002;

		// Media key virtual codes
		internal const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;
		internal const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
		internal const ushort VK_MEDIA_PREV_TRACK = 0xB1;

		internal static void PressMediaKey(ushort vk)
		{
			try
			{
				INPUT[] inputs = new INPUT[2];

				inputs[0].type      = INPUT_KEYBOARD;
				inputs[0].ki.wVk    = vk;
				inputs[0].ki.dwFlags = 0;

				inputs[1].type      = INPUT_KEYBOARD;
				inputs[1].ki.wVk    = vk;
				inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;

				SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
			}
			catch (Exception ex)
			{
				Logger.LogRow(Logger.LogType.Error, $"[MediaController] SendInput failed: {ex.Message}");
			}
		}
	}


	public enum MediaAction
	{
		PlayPause,
		NextTrack,
		PrevTrack,
	}

	public class EchoVRButtonDetector : IDisposable
	{
		// Known offsets
		private static readonly long[] ButtonOffsets =
		{
			0x20C7CA8,
			0x20C7CA0, 0x20C7CB0, 0x20C7C98, 0x20C7CB8,
			0x207CA8,  0x20C7D00, 0x20C8000
		};

		// ── State ─────────────────────────────────────────────────────────────
		private IntPtr _processHandle = IntPtr.Zero; // handle we own (must CloseHandle)
		private IntPtr _baseAddress   = IntPtr.Zero;
		private IntPtr _buttonAddress = IntPtr.Zero;
		private bool   _connected;

		/// <summary>
		/// How many consecutive ReadProcessMemory failures we tolerate before
		
		/// </summary>
		private const int FailureTolerance = 10;
		private int _consecutiveReadFailures;

		private int    _lastState;
		private double _pressStartTime;
		private double _lastReleaseTime;
		private int    _clickCount;
		private bool   _holdDetected;
		private bool   _detectionActive;

		private Timer  _clickTimer;
		private Timer  _holdTimer;
		private Timer  _reconnectTimer;
		private Thread _pollThread;
		private volatile bool _running;

		private readonly object _stateLock = new object();

		// ── Public interface ───────────────────────────────────────────────────
		public bool IsConnected => _connected;

		/// <summary>Raised when an action fires or the connection status changes.</summary>
		public event Action<string> StatusChanged;

		// ── Lifecycle ─────────────────────────────────────────────────────────
		public void Start()
		{
			if (_running) return;
			_running = true;

			TryConnect();

			_pollThread = new Thread(PollLoop) { IsBackground = true, Name = "MediaCtrl-Poll" };
			_pollThread.Start();
		}

		public void Stop()
		{
			_running = false;
			_reconnectTimer?.Dispose();
			_reconnectTimer = null;
			_clickTimer?.Dispose();
			_clickTimer = null;
			_holdTimer?.Dispose();
			_holdTimer = null;
			CloseProcess();
		}

		public void Dispose() => Stop();

		// ── Connection ────────────────────────────────────────────────────────
		private void TryConnect()
		{
			CloseProcess();
			_connected = false;
			_buttonAddress = IntPtr.Zero;
			_consecutiveReadFailures = 0;

			try
			{
				Process[] procs = Process.GetProcessesByName("echovr");
				if (procs.Length == 0)
				{
					RaiseStatus("Disconnected");
					ScheduleReconnect();
					return;
				}

				Process proc = procs[0];

				// Open with explicit PROCESS_VM_READ | PROCESS_QUERY_INFORMATION so
				// ReadProcessMemory works correctly regardless of .NET handle access.
				_processHandle = MediaNativeMethods.OpenProcess(
					MediaNativeMethods.PROCESS_VM_READ |
					MediaNativeMethods.PROCESS_QUERY_INFORMATION,
					false,
					proc.Id);

				if (_processHandle == IntPtr.Zero)
				{
					Logger.LogRow(Logger.LogType.Error,
						$"[MediaController] OpenProcess failed (error {Marshal.GetLastWin32Error()})");
					RaiseStatus("Disconnected");
					ScheduleReconnect();
					return;
				}

				// Base address of the main module
				_baseAddress = proc.MainModule?.BaseAddress ?? IntPtr.Zero;
				if (_baseAddress == IntPtr.Zero)
				{
					RaiseStatus("Disconnected");
					ScheduleReconnect();
					return;
				}

				_buttonAddress = ScanForButtonAddress();

				if (_buttonAddress != IntPtr.Zero &&
				    ReadByte(_buttonAddress, out byte val) &&
				    (val == 0 || val == 1))
				{
					_connected = true;
					Logger.LogRow(Logger.LogType.Info,
						$"[MediaController] Connected. Button address: {_buttonAddress:X}");
					RaiseStatus("Connected");
				}
				else
				{
					RaiseStatus("Disconnected");
					ScheduleReconnect();
				}
			}
			catch (Exception ex)
			{
				Logger.LogRow(Logger.LogType.Error, $"[MediaController] Connect failed: {ex.Message}");
				RaiseStatus("Disconnected");
				ScheduleReconnect();
			}
		}

		private void CloseProcess()
		{
			// Only close handles we opened ourselves via OpenProcess
			if (_processHandle != IntPtr.Zero)
			{
				MediaNativeMethods.CloseHandle(_processHandle);
				_processHandle = IntPtr.Zero;
			}
			_baseAddress = IntPtr.Zero;
			_connected   = false;
		}

		private IntPtr ScanForButtonAddress()
		{
			foreach (long offset in ButtonOffsets)
			{
				IntPtr addr = IntPtr.Add(_baseAddress, (int)offset);
				if (ReadByte(addr, out byte val) && (val == 0 || val == 1))
					return addr;
			}

			// Fallback: scan ±256 bytes around the primary offset
			for (int delta = -0x100; delta <= 0x100; delta += 4)
			{
				IntPtr addr = IntPtr.Add(_baseAddress, (int)(0x20C7CA8 + delta));
				if (ReadByte(addr, out byte val) && (val == 0 || val == 1))
				{
					Logger.LogRow(Logger.LogType.Info,
						$"[MediaController] Found button at offset delta {delta:X}");
					return addr;
				}
			}
			return IntPtr.Zero;
		}

		private bool ReadByte(IntPtr address, out byte value)
		{
			value = 0;
			if (_processHandle == IntPtr.Zero) return false;
			byte[] buf = new byte[1];
			bool ok = MediaNativeMethods.ReadProcessMemory(_processHandle, address, buf, 1, out int read);
			if (!ok || read != 1) return false;
			value = buf[0];
			return true;
		}

		private void ScheduleReconnect()
		{
			if (!SparkSettings.instance.mediaControllerAutoReconnect) return;
			_reconnectTimer?.Dispose();
			_reconnectTimer = new Timer(_ =>
			{
				if (!_running) return;
				TryConnect();
			}, null, 5000, Timeout.Infinite);
		}

		// ── Poll loop ─────────────────────────────────────────────────────────
		private void PollLoop()
		{
			while (_running)
			{
				try
				{
					if (_connected)
						CheckButtonActions();
				}
				catch (Exception ex)
				{
					Logger.LogRow(Logger.LogType.Error, $"[MediaController] Poll error: {ex.Message}");
					Disconnect();
				}
				Thread.Sleep(10); // 100 Hz
			}
		}

		/// <summary>
		/// Declares an actual disconnect (after tolerance exceeded or a hard exception).
		/// </summary>
		private void Disconnect()
		{
			_connected = false;
			CloseProcess();
			RaiseStatus("Disconnected");
			ScheduleReconnect();
		}

		// ── Button state machine (ported 1:1 from Python) ─────────────────────
		private void CheckButtonActions()
		{
			if (!ReadByte(_buttonAddress, out byte raw))
			{
				// Tolerate transient failures before declaring disconnect
				_consecutiveReadFailures++;
				if (_consecutiveReadFailures >= FailureTolerance)
				{
					Logger.LogRow(Logger.LogType.Error,
						$"[MediaController] {FailureTolerance} consecutive read failures — disconnecting");
					Disconnect();
				}
				return;
			}

			// Successful read — reset failure counter
			_consecutiveReadFailures = 0;

			int currentState = raw;
			double currentTime = GetUnixTime();

			SparkSettings s = SparkSettings.instance;
			double debounce  = s.mediaControllerDebounceDelay;
			double threshold = s.mediaControllerDetectionThreshold;
			
			// Use custom hold threshold if Clipping is active and set to 'Hold'
			double holdSec = (s.mediaControllerCustomEnabled && s.mediaControllerCustomTrigger == 1)
				? s.mediaControllerCustomHoldThreshold 
				: s.mediaControllerHoldThreshold;

			lock (_stateLock)
			{
				// ── Rising edge ───────────────────────────────────────────────
				if (currentState == 1 && _lastState == 0)
				{
					if ((currentTime - _lastReleaseTime) < debounce)
					{
						_lastState = currentState;
						return;
					}

					_pressStartTime = currentTime;
					_holdDetected   = false;

					if (!_detectionActive) _detectionActive = true;

					// CRITICAL: Cancel the multi-click timer because a new click has started.
					// This prevents "3 clicks" from firing while you are in the middle of a 4th click.
					_clickTimer?.Dispose();
					_clickTimer = null;

					_holdTimer?.Dispose();

					// Only arm the hold timer if a hold action is actually configured:
					// - Media Controller is enabled (Play/Pause on hold)
					// - OR Controller Clipping is set to Hold mode
					bool needHoldTimer = s.mediaControllerEnabled ||
						(s.mediaControllerCustomEnabled && s.mediaControllerCustomTrigger == 1);

					if (needHoldTimer)
					{
						_holdTimer = new Timer(_ => ProcessHold(), null,
							(int)(holdSec * 1000), Timeout.Infinite);
					}
					else
					{
						_holdTimer = null;
					}
				}
				// ── Sustained press ───────────────────────────────────────────
				else if (currentState == 1 && _lastState == 1)
				{
					double held = currentTime - _pressStartTime;
					bool showHoldProgress = s.mediaControllerEnabled ||
						(s.mediaControllerCustomEnabled && s.mediaControllerCustomTrigger == 1);

					if (held >= 1.0 && !_holdDetected && showHoldProgress)
					{
						if (held < holdSec)
						{
							int progress = (int)(held / holdSec * 100);
							RaiseStatus($"Hold: {progress}%…");
						}
						else
						{
							ProcessHold();
						}
					}
				}
				// ── Falling edge ──────────────────────────────────────────────
				else if (currentState == 0 && _lastState == 1)
				{
					double pressDuration = currentTime - _pressStartTime;
					_lastReleaseTime = currentTime;

					_holdTimer?.Dispose();
					_holdTimer = null;

					if (_holdDetected)
					{
						ResetDetection();
						_lastState = currentState;
						return;
					}

					if (pressDuration < threshold)
					{
						_lastState = currentState;
						return;
					}

					if (pressDuration < 1.0)
					{
						_clickCount++;
						Logger.LogRow(Logger.LogType.Info,
							$"[MediaController] Click #{_clickCount}");

						_clickTimer?.Dispose();
						_clickTimer = new Timer(_ => ProcessClicks(), null,
							(int)(s.mediaControllerClickTimeout * 1000), Timeout.Infinite);
					}
					else
					{
						Logger.LogRow(Logger.LogType.Info,
							$"[MediaController] Long press ({pressDuration:F1}s) – ignored");
						ResetDetection();
					}
				}

				_lastState = currentState;
			}
		}

		private void ProcessClicks()
		{
			lock (_stateLock)
			{
				if (_holdDetected) { ResetDetection(); return; }

				int clicks = _clickCount;
				Logger.LogRow(Logger.LogType.Info,
					$"[MediaController] Processing {clicks} click(s)");

				SparkSettings s = SparkSettings.instance;

				// ── Custom Click Trigger ──
				if (s.mediaControllerCustomEnabled && s.mediaControllerCustomTrigger == 2 && clicks == s.mediaControllerCustomClicks)
				{
					SendCustomShortcut();
					RaiseStatus($"Custom Action ({clicks} clicks)");
				}
				// ── Media Track Actions (only when Media Controller is enabled) ──
				else if (s.mediaControllerEnabled)
				{
					if (clicks == s.mediaControllerPrevClicks)
					{
						SendKey(MediaAction.PrevTrack);
						RaiseStatus($"Prev Track ({clicks} clicks)");
					}
					else if (clicks == s.mediaControllerNextClicks)
					{
						SendKey(MediaAction.NextTrack);
						RaiseStatus($"Next Track ({clicks} clicks)");
					}
				}

				ResetDetection();
			}
		}

		private void ProcessHold()
		{
			lock (_stateLock)
			{
				if (_holdDetected) return;
				_holdDetected = true;
				Logger.LogRow(Logger.LogType.Info, "[MediaController] Hold detected");

				_clickTimer?.Dispose();
				_clickTimer = null;
				_clickCount  = 0;

				SparkSettings s = SparkSettings.instance;

				// ── Custom Hold Trigger ──
				if (s.mediaControllerCustomEnabled && s.mediaControllerCustomTrigger == 1)
				{
					SendCustomShortcut();
					RaiseStatus($"Custom Action (hold)");
				}
				// ── Default Play/Pause (only when Media Controller is enabled) ──
				else if (s.mediaControllerEnabled)
				{
					SendKey(MediaAction.PlayPause);
					RaiseStatus($"Play/Pause ({s.mediaControllerHoldThreshold:F1}s hold)");
				}
			}
		}

		private void SendCustomShortcut()
		{
			SparkSettings s = SparkSettings.instance;
			ushort k1 = (ushort)s.mediaControllerCustomKey1;
			ushort k2 = (ushort)s.mediaControllerCustomKey2;

			if (k1 == 0 && k2 == 0) return;

			Task.Run(async () =>
			{
				try
				{
					// Key 1 Down
					if (k1 != 0) Keyboard.SendKey(k1, false, Keyboard.InputType.Keyboard);
					
					// Key 2 Down
					if (k2 != 0) Keyboard.SendKey(k2, false, Keyboard.InputType.Keyboard);

					await Task.Delay(50);

					// Key 2 Up
					if (k2 != 0) Keyboard.SendKey(k2, true, Keyboard.InputType.Keyboard);

					// Key 1 Up
					if (k1 != 0) Keyboard.SendKey(k1, true, Keyboard.InputType.Keyboard);
				}
				catch (Exception ex)
				{
					Logger.LogRow(Logger.LogType.Error, $"[MediaController] SendCustomShortcut failed: {ex.Message}");
				}
			});
		}

		private void ResetDetection()
		{
			_clickCount = 0;
			_clickTimer?.Dispose();
			_clickTimer = null;
			_holdTimer?.Dispose();
			_holdTimer = null;
			_holdDetected    = false;
			_detectionActive = false;
		}

		// ── Media key dispatch ────────────────────────────────────────────────
		public static void SendKey(MediaAction action)
		{
			ushort vk = action switch
			{
				MediaAction.PlayPause => MediaNativeMethods.VK_MEDIA_PLAY_PAUSE,
				MediaAction.NextTrack => MediaNativeMethods.VK_MEDIA_NEXT_TRACK,
				MediaAction.PrevTrack => MediaNativeMethods.VK_MEDIA_PREV_TRACK,
				_                     => 0,
			};
			if (vk != 0) MediaNativeMethods.PressMediaKey(vk);
		}

		// ── Helpers ───────────────────────────────────────────────────────────
		private static double GetUnixTime() =>
			(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

		private void RaiseStatus(string msg) => StatusChanged?.Invoke(msg);
	}
}
