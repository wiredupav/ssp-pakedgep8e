//Please uncomment the #define line below if you want to include the sample code 
// in the compiled output.
// for the sample to work, you'll have to add a reference to the SimplSharpPro.UI dll to your project.
// #define IncludeSampleCode

using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Crestron.SimplSharp;                          				// For Basic SIMPL# Classes
using Crestron.SimplSharp.CrestronSockets;
using Crestron.SimplSharpPro;                       				// For Basic SIMPL#Pro classes
using Crestron.SimplSharpPro.CrestronThread;        	// For Threading
using Crestron.SimplSharpPro.Diagnostics;		    		// For System Monitor Access
using Crestron.SimplSharpPro.DeviceSupport;         	// For Generic Device Support
using Crestron.SimplSharpPro.EthernetCommunication;
using Crestron.SimplSharp.Net.Http;
using Crestron.SimplSharpProInternal;
using Crestron.SimplSharp.CrestronLogger;
using Crestron.SimplSharp.Scheduler;
using Crestron.SimplSharp.CrestronIO;


namespace Drivers
{
    public abstract class Device
    {
        // Fields
        protected IROutputPort ControlIR;
        protected ComPort ControlSerial;
        protected TCPClient ControlTCP;
        protected UDPServer ControlUDP;
        protected Relay ControlRelay;
        protected Versiport ControlVP;

        // Properties
        public virtual string Name { get; set; }
        public virtual bool Power { get; set; }
        
        // Events
        public class PowerEventArgs : EventArgs
        {
            public bool state;
            public PowerEventArgs(bool newstate)
            {
                this.state = newstate;
            }
        }
        public delegate void PowerEventHandler(object sender, PowerEventArgs args);
        private event PowerEventHandler _PowerEvent;
        public event PowerEventHandler PowerEvent
        {
            add
            {
                _PowerEvent += value;
                if (_PowerEvent != null)
                {
                    OnPowerEvent(this.Power);
                }
            }
            remove
            {
                _PowerEvent -= value;
            }
        }

        protected void OnPowerEvent(bool state)
        {
            if (_PowerEvent != null)
            {
                CrestronInvoke.BeginInvoke(new CrestronSharpHelperDelegate(o => _PowerEvent(this, new PowerEventArgs(state))));
            }
        }

        protected virtual void HandleProgramEvent(eProgramStatusEventType programStatusEventType)
        {
            if (programStatusEventType == eProgramStatusEventType.Stopping)
            {
                try { Terminate(); }
                catch (Exception e) { ErrorLog.Exception("Exception stopping " + this.Name + " driver", e); };
            }
        }
        protected virtual void HandleSystemEvent(eSystemEventType systemEventType)
        {
            if (systemEventType == eSystemEventType.Rebooting)
            {
                try { Terminate(); }
                catch (Exception e) { ErrorLog.Exception("Exception stopping " + this.Name + " driver", e); };
            }
        }
        protected virtual void Terminate()
        {
            try
            {
                //if (ControlCEC != null)
                //ControlCEC = null;
                if (ControlIR != null)
                    ControlIR.UnRegister();
                if (ControlRelay != null)
                    ControlRelay.UnRegister();
                if (ControlSerial != null)
                    ControlSerial.UnRegister();
                if (ControlTCP != null)
                    ControlTCP.Dispose();
                if (ControlUDP != null)
                    ControlUDP.Dispose();
                if (ControlVP != null)
                    ControlVP.UnRegister();
            }
            catch (Exception e)
            {
                ErrorLog.Exception("Exception terminating " + this.Name, e);
            }
        }

        // Methods
        public virtual void ConsoleHandler(string[] cmdarr)
        {
            CrestronConsole.ConsoleCommandResponse("No commands defined");
            return;
        }

        public virtual void GoToChannel(string channel) { }

        // Constructor

        public Device(string name)
        {
            Outputs = new List<IO>();
            Inputs = new List<IO>();
            this.Name = name;
            this.AppUrl = "";
            this.RoomsUsing = new List<Room>();
        }
    }

    public class PakedgeP8E : Device
    {
        // Classes
        public class Outlet
        {
            private PakedgeP8E _PDU;
            private bool _State;
            public int Number
            {
                get;
                protected set;
            }
            public string Name
            {
                get;
                protected set;
            }
            public bool PowerFB
            {
                get
                {
                    return _State;
                }
                set
                {
                    _State = value;
                }
            }
            public void Reboot()
            {
                _PDU.SendCmd("prb " + Number.ToString() + "\r\n", false);
                _PDU._Socket.ReceiveData();
                PowerFB = _PDU.GetOutletState(this);
            }
            public bool Power
            {
                get
                {
                    return _State;
                }
                set
                {
                    _PDU.SendCmd("pset " + Number.ToString() + " " + ((value) ? "1" : "0") + "\r\n", false);
                    PowerFB = _PDU.GetOutletState(this);
                }
            }
            public Outlet(int num, string name, bool state, PakedgeP8E pdu)
            {
                Number = num;
                Name = name;
                _State = state;
                _PDU = pdu;
            }
        }
        // Fields
        private TCPClient _Socket;
        protected CTimer ReconnectTimer, _PollTimer, _LoginTimer;
        private int _LoginCount;
        private CrestronQueue<String> _SentMessages;

        // Events

        // Properties
        public override bool Power
        {
            get
            {
                return true;
            }
            set
            {
                base.Power = true;
            }
        }
        public List<Outlet> Outlets;

        // Methods
        private void Login(object timer)
        {
            if (_Socket.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                _Socket.SocketStatusChange -= SocketStatusHandler;
                _Socket.DisconnectFromServer();
            }
            SocketErrorCodes err = _Socket.ConnectToServer();
            while (_Socket.ClientStatus == SocketStatus.SOCKET_STATUS_WAITING)
            {
                Thread.Sleep(100);
            }
            if (_Socket.ClientStatus != SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                ErrorLog.Error("Error connecting to " + this.Name);
            }
            _Socket.SocketStatusChange += new TCPClientSocketStatusChangeEventHandler(SocketStatusHandler);

            string data = "";
            int rxlen = 0;

            // Check for splash screen
            Thread.Sleep(250);
            while (_Socket.DataAvailable)
            {
                rxlen = _Socket.ReceiveData();
                data += Encoding.ASCII.GetString(_Socket.IncomingDataBuffer, 0, rxlen);
            }

            // Check for prompt
            // Send login
            if (data.Contains("\r\n> "))
            {
                _SentMessages.TryToDequeue();
                SendCmd("login\r\n");
            }
            else
            // if no prompt, send another line feed
            {
                SendCmd("\r\n");

                // Check for prompt again
                Thread.Sleep(250);
                while (_Socket.DataAvailable)
                {
                    rxlen = _Socket.ReceiveData();
                    data += Encoding.ASCII.GetString(_Socket.IncomingDataBuffer, 0, rxlen);
                }

                // On prompt, send login
                if (data.Contains("\r\n> "))
                {
                    _SentMessages.TryToDequeue();
                    SendCmd("login\r\n");
                }
            }

            // Check for username prompt:
            Thread.Sleep(250);
            data = "";
            while (_Socket.DataAvailable)
            {
                rxlen = _Socket.ReceiveData();
                data += Encoding.ASCII.GetString(_Socket.IncomingDataBuffer, 0, rxlen);
            }

            if (data.Contains("user name:"))
            // Send the username
            {
                _SentMessages.TryToDequeue();
                SendCmd("gear7support\r\n");
            }


            // Check for password prompt
            Thread.Sleep(250);
            data = "";
            while (_Socket.DataAvailable)
            {
                rxlen = _Socket.ReceiveData();
                data += Encoding.ASCII.GetString(_Socket.IncomingDataBuffer, 0, rxlen);
            }

            if (data.Contains("password:"))
            // Send the password
            {
                _SentMessages.TryToDequeue();
                SendCmd("password123!\r\n");
            }

            // Check for success / failure
            Thread.Sleep(250);
            data = "";
            while (_Socket.DataAvailable)
            {
                rxlen = _Socket.ReceiveData();
                data += Encoding.ASCII.GetString(_Socket.IncomingDataBuffer, 0, rxlen);
            }

            if (data.Contains("login success"))
            {
                _SentMessages.TryToDequeue();
                ErrorLog.Notice("Logged in to " + this.Name);
                GetOutlets();
                _PollTimer = new CTimer(new CTimerCallbackFunction(Poll), 30000);
                return;
            }
            else
            {
                _SentMessages.TryToDequeue();
                ErrorLog.Error("Couldn't log in to " + this.Name);
                _LoginCount++;
                if (_LoginCount < 5)
                {
                    _LoginTimer = new CTimer(new CTimerCallbackFunction(Login), 5000);
                }
                return;
            }
        }

        private void Poll(object timer)
        {
            _PollTimer.Stop();
            GetOutlets();
            _PollTimer.Reset(30000);
        }

        private List<string> GetAndTrimOutletStates()
        {
            SendCmd("pshow\r\n");
            string data = "";
            int rxlen = 0;
            while (_Socket.DataAvailable)
            {
                rxlen = _Socket.ReceiveData();
                data += Encoding.ASCII.GetString(_Socket.IncomingDataBuffer, 0, rxlen);
            }
            data = data.Replace("\r\n", "\n");
            string[] data_arr = data.Split('\n');

            List<string> lines = new List<string>(data_arr);

            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains("*******"))
                    lines[i] = "";
                if (lines[i].Contains("*  "))
                    lines[i] = "";
                if (lines[i].Contains("> "))
                    lines[i] = "";
                if (lines[i] == (">"))
                    lines[i] = "";
                if (lines[i].Contains("Port |"))
                    lines[i] = "";
                if (lines[i].Contains("pshow"))
                    lines[i] = "";
                if (lines[i].Contains("---"))
                    lines[i] = "";
            }

            lines.RemoveAll(item => item == "");

            return lines;
        }

        private void GetOutlets()
        {
            List<string> lines = GetAndTrimOutletStates();

            List<List<string>> outlets = new List<List<string>>();

            foreach (string line in lines)
            {
                string[] arr = line.Split('|');
                // Trim down the outlet number
                arr[0] = arr[0].Replace(" ", "");
                int outletnum = int.Parse(arr[0]);
                // Get the name
                arr[1].Trim();
                string outletname = arr[1];
                // Get the state
                arr[2] = arr[2].Trim();
                bool outletstate = (arr[2] == "ON") ? true : false;
                if (Outlets.Find(item => item.Number == outletnum) != null)
                {
                    Outlets.Find(item => item.Number == outletnum).PowerFB = outletstate;
                }
                else
                {
                    Outlets.Add(new Outlet(outletnum, outletname, outletstate, this));
                }
            }
        }

        private bool GetOutletState(Outlet outlet)
        {
            List<string> lines = GetAndTrimOutletStates();

            List<List<string>> outlets = new List<List<string>>();

            foreach (string line in lines)
            {
                string[] arr = line.Split('|');
                // Trim down the outlet number
                arr[0] = arr[0].Replace(" ", "");
                int outletnum = int.Parse(arr[0]);
                if (outletnum == outlet.Number)
                {
                    // Get the name
                    arr[1].Trim();
                    string outletname = arr[1];
                    // Get the state
                    arr[2] = arr[2].Trim();
                    bool outletstate = (arr[2] == "ON") ? true : false;

                    return outletstate;
                }
                else
                {
                    continue;
                }
            }
            return false;
        }
        private void SendCmd(string data)
        {
            bool restartTimer = false;
            if (_PollTimer != null)
            {
                if (!_PollTimer.Disposed)
                {
                    try
                    {
                        _PollTimer.Stop();
                        restartTimer = true;
                    }
                    catch
                    {
                    }
                }
            }
            byte[] data_arr = Encoding.ASCII.GetBytes(data);
            int data_len = Encoding.ASCII.GetByteCount(data);

            _Socket.SendData(data_arr, data_len);
            _SentMessages.Enqueue(data);
            if (restartTimer) _PollTimer.Reset(30000);
        }
        private void SendCmd(string data, bool queue)
        {
            byte[] data_arr = Encoding.ASCII.GetBytes(data);
            int data_len = Encoding.ASCII.GetByteCount(data);

            _Socket.SendData(data_arr, data_len);
        }
        private void ParseRxd(byte[] data)
        {
            try
            {
                string strdata = Encoding.ASCII.GetString(data, 0, data.Length);
                string[] data_lines = strdata.Replace("\n", "").Split('\r');
                List<List<string>> data_arr = new List<List<string>>();

                foreach (string i in data_lines)
                {
                    string[] temparr = (i.Split(' '));
                    data_arr.Add(new List<string>(temparr));
                }

                for (int i = 0; i < data_arr.Count; i++)
                {
                    if (data_arr[i] == null)
                        continue;
                }
            }
            catch (Exception e)
            {
                ErrorLog.Exception("Caught exception in " + this.Name + " ParseRxd:", e);
            }
        }
        // Callbacks
        protected override void HandleProgramEvent(eProgramStatusEventType programStatusEventType)
        {
            if (programStatusEventType == eProgramStatusEventType.Stopping)
            {
                try { Terminate(); }
                catch (Exception e) { ErrorLog.Exception("Exception stopping " + this.Name + " driver", e); };
            }
        }
        protected override void HandleSystemEvent(eSystemEventType systemEventType)
        {
            if (systemEventType == eSystemEventType.Rebooting)
            {
                try { Terminate(); }
                catch (Exception e) { ErrorLog.Exception("Exception stopping " + this.Name + " driver", e); };
            }
        }

        private void SocketStatusHandler(TCPClient client, SocketStatus status)
        {
            ErrorLog.Notice(this.Name + " - Socket Status Handler - status = " + status.ToString());

            if (status != SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                ErrorLog.Notice(this.Name + " - Link not connected");
                if (Thread.CurrentThread.ThreadState == Thread.eThreadStates.ThreadAborting)
                {
                    ErrorLog.Notice(this.Name + " - Link thread aborting");
                }
                else
                {
                    ErrorLog.Notice(this.Name + " - Starting reconnect timer");
                    ReconnectTimer = new CTimer(new CTimerCallbackFunction(ReconnectCallback), 30000);
                }
            }
        }
        protected void ReconnectCallback(object sender)
        {
            ReconnectTimer.Stop();
            ReconnectTimer.Dispose();
            try
            {
                ErrorLog.Notice(this.Name + " - Retrying Login");
                Login(null);
            }
            catch (Exception e)
            {
                ErrorLog.Exception("Exception occured in " + this.Name + " reconnect Callback", e);
            }
        }
        private void ReceiveMsg(object obj, int len)
        {
            TCPClient client = (TCPClient)obj;
            try
            {
                if (len > 0)
                {
                    List<byte> rxd = new List<byte>(len);
                    byte[] temprxd = new byte[len];
                    Array.Copy(client.IncomingDataBuffer, temprxd, len);
                    rxd.AddRange(temprxd);
                    while (client.DataAvailable)
                    {
                        int morelen = client.ReceiveData();
                        byte[] morerxd = new byte[morelen];
                        Array.Copy(client.IncomingDataBuffer, morerxd, len);
                        rxd.AddRange(morerxd);
                    }
                    CrestronInvoke.BeginInvoke(new CrestronSharpHelperDelegate((o) => ParseRxd(rxd.ToArray())));
                }
            }
            catch (TimeoutException e)
            {
                ErrorLog.Exception("Exception: ", e);
            }
            client.ReceiveDataAsync(new TCPClientReceiveCallback(ReceiveMsg));
        }
        // Constructor
        public PakedgeP8E(string name, String newIP)
            : base(name)
        {
            // Register system event handlers
            CrestronEnvironment.ProgramStatusEventHandler += new ProgramStatusEventHandler(this.HandleProgramEvent);
            CrestronEnvironment.SystemEventHandler += new SystemEventHandler(this.HandleSystemEvent);

            _SentMessages = new CrestronQueue<string>();

            _Socket = new TCPClient(newIP, 23, 16384);
            _Socket.SocketSendOrReceiveTimeOutInMs = 20000;

            _Socket.SocketStatusChange += new TCPClientSocketStatusChangeEventHandler(SocketStatusHandler);

            Outlets = new List<Outlet>();

            Login(null);
        }

        protected override void Terminate()
        {
            _Socket.SocketStatusChange -= SocketStatusHandler;

            try
            {
                SendCmd("logout\r\n");
                _Socket.DisconnectFromServer();
                if (_LoginTimer != null)
                {
                    _LoginTimer.Stop();
                    _LoginTimer.Dispose();
                    _LoginTimer = null;
                }
                if (_PollTimer != null)
                {
                    _PollTimer.Stop();
                    _PollTimer.Dispose();
                    _PollTimer = null;
                }
            }
            catch (Exception e)
            {
                ErrorLog.Exception("Exception disconnecting " + this.Name, e);
            }
        }
    }
}