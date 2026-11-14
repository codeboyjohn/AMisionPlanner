using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;

// dns, ip address
// tcplistner

namespace MissionPlanner.Comms
{
    public class UdpSerial : CommsBase, ICommsSerial, IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public readonly List<IPEndPoint> EndPointList = new List<IPEndPoint>();

        private bool _isopen;

        public bool CancelConnect = false;
        /// <summary>
        /// add to EndPointList if need when injecting
        /// </summary>
        public UdpClient client = new UdpClient();

        private MemoryStream rbuffer = new MemoryStream();

        /// <summary>
        ///     this is the remote endpoint we send messages too. this class does not support multiple remote endpoints.
        /// </summary>
        public IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

        /// <summary>The host supplies the UI; the communications library has no WinForms dependency.</summary>
        public delegate inputboxreturn NetworkInterfacePicker(ref IPAddress address);

        public static event NetworkInterfacePicker SelectNetworkInterface;

        /// <summary>
        /// Ask the user to select a local NIC address, initially selecting the supplied address.
        /// Returns false without changing the address if cancelled or no host UI is available.
        /// This only selects an address; it does not bind or reopen the UDP socket.
        /// </summary>
        public bool TrySelectLocalAddress(ref IPAddress address)
        {
            var picker = SelectNetworkInterface;
            if (picker == null)
                return false;

            var selected = address;
            if (picker(ref selected) != inputboxreturn.OK || selected == null)
                return false;

            address = selected;
            return true;
        }

        public string ConfigRef { get; set; } = "";

        public UdpSerial()
        {
            //System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
            //System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Port = "51019";
            Interface_IP = "192.168.168.10";
            Mult_Addr = "224.1.0.44";
            ReadTimeout = 500;
        }

        public UdpSerial(UdpClient client)
        {
            this.client = client;
            _isopen = true;
            ReadTimeout = 500;
        }

        public string Port { get; set; }
        public string Mult_Addr { get; set; }
        public string Interface_IP { get; set; }

        public int WriteBufferSize { get; set; }
        public int WriteTimeout { get; set; }
        public bool RtsEnable { get; set; }
        public Stream BaseStream => new UDPStream(this);

        public void toggleDTR()
        {
        }

        public int ReadTimeout
        {
            get; // { return client.ReceiveTimeout; }
            set; // { client.ReceiveTimeout = value; }
        }

        public int ReadBufferSize { get; set; }

        public int BaudRate { get; set; }

        public int DataBits { get; set; }

        public string PortName
        {
            get => "UDP" + Port;
            set { }
        }

        public int BytesToRead => (int)(client.Available + rbuffer.Length - rbuffer.Position);

        public int BytesToWrite => 0;

        public bool IsOpen
        {
            get
            {
                if (client?.Client == null) return false;
                return _isopen;
            }
            set => _isopen = value;
        }

        public bool DtrEnable { get; set; }

        public void Open()
        {
            if (client?.Client?.Connected == true || IsOpen)
            {
                log.Info("UDPSerial socket already open");
                return;
            }

            var dest = Port;

            dest = OnSettings("UDP_port" + ConfigRef, dest);

            if (inputboxreturn.Cancel == OnInputBoxShow("Listen Port",
                    "Enter Local port (ensure remote end is already sending)", ref dest)) return;

            var localAddress = Interface_IP;
            
            localAddress = OnSettings("Interface_IP" + ConfigRef, localAddress);

            // Load the previously persisted IP address.
            IPAddress local_ip = IPAddress.Parse(localAddress);
            
            // The host supplies the NIC picker when a desktop UI is available.
            if (SelectNetworkInterface != null)
            {
                if (!TrySelectLocalAddress(ref local_ip))
                    return;
            }

            var multicast_source = Mult_Addr;
            multicast_source = OnSettings("Mult_Addr" + ConfigRef, multicast_source);

            if (inputboxreturn.Cancel == OnInputBoxShow("Listen Multicast?",
                    "Enter Multicast to Subscribe too", ref multicast_source)) return;

            // Validate before closing the existing socket or saving any settings.
            IPAddress groupAddress = IPAddress.Parse(multicast_source);
            bool multicast = !groupAddress.Equals(IPAddress.Any);
            if (multicast && (groupAddress.AddressFamily != AddressFamily.InterNetwork ||
                local_ip.AddressFamily != AddressFamily.InterNetwork ||
                groupAddress.GetAddressBytes()[0] < 224 || groupAddress.GetAddressBytes()[0] > 239))
                throw new ArgumentException("Select an IPv4 NIC and an IPv4 multicast group (224.0.0.0–239.255.255.255), or enter 0.0.0.0 for ordinary UDP.");

            // Multicast receives on the wildcard endpoint; membership selects its interface.
            // Ordinary UDP receives only on the selected local address.
            var bindAddress = multicast || SelectNetworkInterface == null ? IPAddress.Any : local_ip;
            var localEndPoint = new IPEndPoint(bindAddress, int.Parse(dest));
            client?.Close();
            client = new UdpClient(localEndPoint);
            if (multicast)
            {
                try
                {
                    client.JoinMulticastGroup(groupAddress, local_ip);
                }
                catch (SocketException ex)
                {
                    client.Close();
                    throw new InvalidOperationException(
                        $"Could not join multicast group {groupAddress} " +
                        $"on local interface {local_ip}. " +
                        $"Socket error: {ex.SocketErrorCode}. {ex.Message}",
                        ex);
                }
            }

            Port = dest;
            Interface_IP = local_ip.ToString();
            Mult_Addr = groupAddress.ToString();
            OnSettings("Interface_IP" + ConfigRef, Interface_IP, true);
            OnSettings("UDP_port" + ConfigRef, Port, true);
            OnSettings("Mult_Addr" + ConfigRef, Mult_Addr, true);
            log.InfoFormat("UDPSerial listening on {0}", localEndPoint);

            EndPointList.Clear();
            rbuffer.SetLength(0);
            rbuffer.Position = 0;

            while (true)
            {
                Thread.Sleep(500);

                if (CancelConnect)
                {
                    try
                    {
                        client.Close();
                    }
                    catch
                    {
                    }

                    return;
                }

                if (BytesToRead > 0)
                    break;
            }

            if (BytesToRead == 0)
                return;

            try
            {
                // reset any previous connection
                RemoteIpEndPoint = new IPEndPoint(
                    client.Client.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

                client.Receive(ref RemoteIpEndPoint);
                log.InfoFormat("UDPSerial connecting to {0} : {1}", RemoteIpEndPoint.Address, RemoteIpEndPoint.Port);
                EndPointList.Add(RemoteIpEndPoint);
                _isopen = true;
            }
            catch (Exception ex)
            {
                if (client != null && client.Client.Connected) client.Close();
                log.Info(ex.ToString());
                //CustomMessageBox.Show("Please check your Firewall settings\nPlease try running this command\n1.    Run the following command in an elevated command prompt to disable Windows Firewall temporarily:\n    \nNetsh advfirewall set allprofiles state off\n    \nNote: This is just for test; please turn it back on with the command 'Netsh advfirewall set allprofiles state on'.\n", "Error");
                throw new Exception("The socket/UDPSerial is closed " + ex);
            }
        }

        public int Read(byte[] readto, int offset, int length)
        {
            VerifyConnected();
            if (length < 1) return 0;

            var deadline = DateTime.Now.AddMilliseconds(ReadTimeout);

            lock (rbuffer)
            {
                if (rbuffer.Position == rbuffer.Length)
                    rbuffer.SetLength(0);

                var position = rbuffer.Position;

                while ((rbuffer.Length - rbuffer.Position) < length && DateTime.Now < deadline)
                {
                    // read more
                    while (client.Available > 0 && (rbuffer.Length - rbuffer.Position) < length)
                    {
                        var currentRemoteIpEndPoint = new IPEndPoint(
                            client.Client.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                        // assumes the udp packets are mavlink aligned, if we are receiving from more than one source
                        var b = client.Receive(ref currentRemoteIpEndPoint);
                        rbuffer.Seek(0, SeekOrigin.End);
                        rbuffer.Write(b, 0, b.Length);
                        rbuffer.Seek(position, SeekOrigin.Begin);

                        if (!EndPointList.Contains(currentRemoteIpEndPoint))
                            EndPointList.Add(currentRemoteIpEndPoint);
                    }

                    Thread.Yield();
                }

                // prevent read past end of array
                if (rbuffer.Length - rbuffer.Position < length)
                    length = (int)(rbuffer.Length - rbuffer.Position);

                return rbuffer.Read(readto, offset, length);
            }
        }

        public int ReadByte()
        {
            VerifyConnected();
            var count = 0;
            while (BytesToRead == 0)
            {
                Thread.Sleep(1);
                if (count > ReadTimeout)
                    throw new Exception("NetSerial Timeout on read");
                count++;
            }

            var buffer = new byte[1];
            Read(buffer, 0, 1);
            return buffer[0];
        }

        public int ReadChar()
        {
            return ReadByte();
        }

        public string ReadExisting()
        {
            VerifyConnected();
            var data = new byte[client.Available];
            if (data.Length > 0)
                Read(data, 0, data.Length);

            var line = Encoding.ASCII.GetString(data, 0, data.Length);

            return line;
        }

        public void WriteLine(string line)
        {
            VerifyConnected();
            line = line + "\n";
            Write(line);
        }

        public void Write(string line)
        {
            VerifyConnected();
            var data = new ASCIIEncoding().GetBytes(line);
            Write(data, 0, data.Length);
        }

        public void Write(byte[] write, int offset, int length)
        {
            VerifyConnected();
            // this is not ideal. but works
            foreach (var ipEndPoint in EndPointList)
                try
                {
                    client.Send(write, length, ipEndPoint);
                }
                catch
                {
                } //throw new Exception("Comport / Socket Closed"); }
        }

        public void DiscardInBuffer()
        {
            VerifyConnected();
            var size = client.Available;
            var crap = new byte[size];
            log.InfoFormat("UdpSerial DiscardInBuffer {0}", size);
            Read(crap, 0, size);
        }

        public string ReadLine()
        {
            var temp = new byte[4000];
            var count = 0;
            var timeout = 0;

            while (timeout <= 100)
            {
                if (!IsOpen) break;
                if (BytesToRead > 0)
                {
                    var letter = (byte) ReadByte();

                    temp[count] = letter;

                    if (letter == '\n') // normal line
                        break;

                    count++;
                    if (count == temp.Length)
                        break;
                    timeout = 0;
                }
                else
                {
                    timeout++;
                    Thread.Sleep(5);
                }
            }

            Array.Resize(ref temp, count + 1);

            return Encoding.ASCII.GetString(temp, 0, temp.Length);
        }

        public void Close()
        {
            _isopen = false;
            if (client != null) client.Close();

            client = new UdpClient();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void VerifyConnected()
        {
            if (client == null || !IsOpen)
            {
                Close();
                throw new Exception("The socket/serialproxy is closed");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // dispose managed resources
                Close();
                client = null;
            }

            // free native resources
        }
    }
}