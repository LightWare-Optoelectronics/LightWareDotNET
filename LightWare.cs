using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Globalization;
using System.Diagnostics;
using System.IO.Ports;
using System.Windows.Forms;
using System.IO;

namespace LightWareDotNET
{
	public enum ReadingType
	{
		Distance,
		LostSignal,
	}

	public enum DataType
	{
		HumanInterface,
		MachineInterface,
	}

	public class Device
	{
		public delegate void SerialErrorDelegate();
		public event SerialErrorDelegate SerialError;

		protected SerialPort _serial;
		protected Control _uiInvokeControl;
		protected DataType _dataInterface;
		
		public virtual void ProcessHMIData(byte[] Data) { }

		// TODO: Using get port names might work for testing USB removal.
		// Ideally we want to be notified if this happens.
		// Could just do serial ports ourselves, should be able to detect com port handle close.

		public bool ConnectSerialPort(string PortName, int BaudRate, DataType DataInterface, Control UIControl)
		{
			if (_serial != null)
				DisconnectSerialPort();
			
			_serial = new SerialPort();
			_serial.PortName = PortName;
			_serial.BaudRate = BaudRate;
			_serial.Parity = Parity.None;
			_serial.DataBits = 8;
			_serial.StopBits = StopBits.One;
			_serial.Handshake = Handshake.None;
			_serial.ReadTimeout = 500;
			_serial.WriteTimeout = 500;
			_serial.ErrorReceived += _SerialErrorReceived;
			_serial.DataReceived += _SerialDataRecevied;
			_uiInvokeControl = UIControl;
			_dataInterface = DataInterface;

			try
			{
				_serial.Open();
			}
			catch (Exception Ex)
			{
				//throw(Ex);
				return false;
			}

			return true;
		}

		private void _ThreadCloseSerialProc()
		{
			_serial.Close();
		}

		public void DisconnectSerialPort()
		{
			// NOTE: We need to call the serial close on another thread or there will be a deadlock between the UI thread and the data received event.

			if (_serial != null)
			{	
				Thread serialCloser = new Thread(_ThreadCloseSerialProc);
				serialCloser.Start();

				while (_serial.IsOpen)
				{
					Thread.Sleep(100);
				};

				_serial = null;
			}
		}

		private void _SerialErrorReceived(object sender, SerialErrorReceivedEventArgs e)
		{
			//Console.WriteLine("Error: " + e.EventType.ToString());
			
			if (SerialError != null)
			{
				if (_uiInvokeControl != null)
				{
					_uiInvokeControl.Invoke((MethodInvoker)delegate
					{
						SerialError();
					});
				}
				else
				{
					SerialError();
				}
			}
		}

		private void _SerialDataRecevied(object sender, SerialDataReceivedEventArgs e)
		{
			try
			{
				byte[] buffer = new byte[_serial.BytesToRead];
				_serial.Read(buffer, 0, buffer.Length);

				if (_dataInterface == DataType.HumanInterface)
				{
					ProcessHMIData(buffer);
				}
				else
				{
					// NOTE: No other interfaces implemented yet!
					//throw(new NotImplementedException());
				}
			}
			catch (IOException E)
			{
				//Console.WriteLine("IOException: " + E.Message);
				// TODO: Serial error invoke? Or will that happen manually?
				DisconnectSerialPort();
			}
			catch (Exception E)
			{
				//Console.WriteLine("Other Exception: " + E.Message);
				// Can happen when invoke is called but form has shut down already.
			}
		}
	}

	public class SinglePointReading
	{
		public ReadingType Type;
		public float Distance;

		public SinglePointReading()
		{
			Type = ReadingType.LostSignal;
			Distance = 0.0f;
		}

		public SinglePointReading(float distance)
		{
			if (distance == float.MaxValue)
			{
				Type = ReadingType.LostSignal;
				Distance = 0.0f;
			}
			else
			{
				Type = ReadingType.Distance;
				Distance = distance;
			}
		}
	}

	public class SF30 : Device
	{

		private byte[] _buffer = new byte[32];
		private int _bufferLen = 0;
		private int _parseState = 0;

		private double _updateTimer = 0.0;
		private int _readingCount = 0;
		private int _validReadingCount = 0;
		private float _averageReading = 0.0f;

		public long TotalReadings { get; private set; }
		public int ReadingFrequency { get; private set; }

		public delegate void ReceivedReadingDelegate(Reading R);
		public event ReceivedReadingDelegate ReceivedReading;

		public class Reading : SinglePointReading
		{
			public Reading() : base() { }
			public Reading(float distance) : base(distance) { }
		}

		public override void ProcessHMIData(byte[] Data)
		{
			for (int i = 0; i < Data.Length; ++i)
			{
				ProcessHMIData(Data[i]);
			}
		}

		public void ProcessHMIData(byte data)
		{
			Reading result = null;

			if (data == '\r')
				return;

			if (data == '\n')
			{
				if (_parseState == 1)
				{
					string packet = Encoding.UTF8.GetString(_buffer, 0, _bufferLen);
					//Console.WriteLine("Process Packet: [" + packet + "]");

					float distance = 0.0f;

					++TotalReadings;
					++_readingCount;

					if (float.TryParse(packet, NumberStyles.Any, CultureInfo.InvariantCulture, out distance))
					{
						result = new Reading(distance);
						++_validReadingCount;
						_averageReading += distance;
					}
					else
					{
						result = new Reading();
					}

					double time = (double)Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

					if (time - _updateTimer >= 1.0)
					{
						ReadingFrequency = _readingCount;

						float avgDist = 0.0f;

						if (_validReadingCount > 0)
							avgDist = _averageReading / _validReadingCount;

						_updateTimer = time;
						_readingCount = 0;
						_validReadingCount = 0;
						_averageReading = 0.0f;

						//Console.WriteLine("Hz: " + ReadingFrequency + "\t Distance: " + avgDist + "m");
					}
				}

				_parseState = 1;
				_bufferLen = 0;
			}
			else if (_parseState == 1)
			{
				if (_bufferLen == 32)
				{
					_parseState = 0;
					_bufferLen = 0;
				}
				else if (data == '.' || (data >= '0' && data <= '9') || data == '.' || data == '-')
				{
					_buffer[_bufferLen++] = data;
				}
			}

			if (ReceivedReading != null && result != null)
			{
				if (_uiInvokeControl != null)
				{
					_uiInvokeControl.Invoke((MethodInvoker)delegate
					{
						ReceivedReading(result);
					});
				}
				else
				{
					ReceivedReading(result);
				}
			}
			
			return;
		}
	}

	public class SF33 : Device
	{
		public delegate void ReceivedReadingDelegate(Reading R);

		public event ReceivedReadingDelegate ReceivedReading;

		private string _buffer = "";
		private int _parseState = 0;

		/*
		private double _updateTimer = 0.0;
		private int _readingCount = 0;
		private int _validReadingCount = 0;
		private float _averageReading = 0.0f;
		*/

		public class Reading
		{
			public SinglePointReading[] Beam;

			public Reading()
			{
				Beam = new SinglePointReading[3];
				Beam[0] = new SinglePointReading();
				Beam[1] = new SinglePointReading();
				Beam[2] = new SinglePointReading();
			}

			public Reading(float Dist1, float Dist2, float Dist3)
			{
				Beam = new SinglePointReading[3];
				Beam[0] = new SinglePointReading(Dist1);
				Beam[1] = new SinglePointReading(Dist2);
				Beam[2] = new SinglePointReading(Dist3);
			}
		}

		public override void ProcessHMIData(byte[] Data)
		{
			for (int i = 0; i < Data.Length; ++i)
			{
				ProcessHMIData(Data[i]);
			}
		}

		public void ProcessHMIData(byte data)
		{
			Reading result = null;

			if (data == '\r')
				return;

			if (data == '\n')
			{
				if (_parseState == 1)
				{
					string[] parts = _buffer.Split('m');
					//Console.WriteLine("Buffer: " + _buffer + " Parts: " + parts.Length);

					if (parts.Length == 4)
					{
						result = new Reading();

						for (int i = 0; i < 3; ++i)
						{
							float dist = 0.0f;
							if (float.TryParse(parts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out dist))
							{
								result.Beam[i].Type = ReadingType.Distance;
								result.Beam[i].Distance = dist;
							}
						}
					}
				}

				_parseState = 1;
				_buffer = "";
			}
			else if (_parseState == 1)
			{
				_buffer += Convert.ToChar(data);
			}

			if (ReceivedReading != null && result != null)
			{
				if (_uiInvokeControl != null)
				{
					_uiInvokeControl.BeginInvoke((MethodInvoker)delegate
					{
						ReceivedReading(result);
					});
				}
				else
				{
					ReceivedReading(result);
				}
			}

			return;
		}
	}
}
